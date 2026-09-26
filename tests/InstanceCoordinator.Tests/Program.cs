using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ChatPCBridge;

internal static class Program
{
    private const string AcceptedBatch = "20260926-203000-0123abcd";
    private const string RejectedBatch = "20260926-203001-0123abcd";
    private const string ThrowingBatch = "20260926-203002-0123abcd";
    private const string FaultedBatch = "20260926-203003-0123abcd";
    private const string SlowBatch = "20260926-203004-0123abcd";
    private const string StalledBatch = "20260926-203005-0123abcd";
    private static int _passed;
    private static readonly List<Process> Children = [];

    // Synchronous Main intentionally keeps mutex acquisition and disposal on
    // one OS thread, just as the production WPF dispatcher does.
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--child") return Child(args);
            RunIntegrationTests();
            Console.WriteLine($"PASS: {_passed} checks including cross-process IPC; no production scope or archive paths used.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            foreach (var child in Children)
            {
                try { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(); } }
                catch { }
                child.Dispose();
            }
        }
    }

    private static void RunIntegrationTests()
    {
        var scope = Guid.NewGuid().ToString("N");
        var primary = StartChild(scope, "primary");
        Check(ReadLine(primary) == "READY", "first process acquires the primary mutex");
        Check(RunChild(scope, "probe") == "SECONDARY", "second process cannot acquire the held mutex");
        Check(RunChild(scope, "activate") == "ACK", "activation is acknowledged across processes");
        Check(RunChild(scope, "batch") == "ACK", "valid batch is acknowledged across processes");
        Check(RunChild(scope, "handler-reject") == "REJECT", "handler rejection propagates to the sender");
        Check(RunChild(scope, "invalid") == "REJECT", "invalid batch is refused by NotifyAsync");
        Check(RunChild(scope, "raw-invalid") == "NO", "server independently rejects a raw path-traversal batch");
        primary.StandardInput.WriteLine("STATS");
        primary.StandardInput.Flush();
        var stats = ReadLine(primary);
        var expectedStats = "STATS:activate:,batch-ready:" + AcceptedBatch + ",batch-ready:" + RejectedBatch;
        Check(stats == expectedStats, "invalid notices never reach the primary handler");

        foreach (var mode in new[] { "raw-empty", "raw-json", "raw-null", "raw-utf8", "raw-activate-handoff", "raw-activate-batch" })
            Check(RunChild(scope, mode) == "NO", mode + " is rejected without acknowledgement");
        Check(RunChild(scope, "raw-oversize") is "NO" or "CLOSED", "oversized request is rejected or disconnected without success");
        Check(RunChild(scope, "raw-eof") == "SENT", "client disconnects after valid JSON without the required terminator");
        Check(RunChild(scope, "activate") == "ACK", "listener recovers after malformed frames and early EOF");
        primary.StandardInput.WriteLine("STATS");
        primary.StandardInput.Flush();
        Check(ReadLine(primary) == expectedStats + ",activate:", "malformed and unterminated frames never invoke the handler");
        Check(RunChild(scope, "raw-limit") == "OK", "exactly 1 KiB of JSON payload plus newline is accepted");
        Check(RunChild(scope, "raw-fragmented") == "OK", "fragmented valid request is read as one frame");
        Check(RunChild(scope, "raw-stalled") == "CLOSED", "unterminated open connection is closed by the server deadline");
        Check(RunChild(scope, "handler-throw") == "REJECT", "synchronous handler exception is never acknowledged as success");
        Check(RunChild(scope, "handler-fault") == "REJECT", "faulted handler task is never acknowledged as success");
        Check(RunChild(scope, "handler-slow") == "ACK", "client waits long enough for a successful handler inside the server deadline");
        Check(RunChild(scope, "handler-stalled") == "REJECT", "unfinished handler receives no success acknowledgement");
        Check(RunChild(scope, "activate") == "ACK", "listener remains usable after read and handler deadlines");
        primary.StandardInput.WriteLine("STOP");
        primary.StandardInput.Flush();
        Check(ReadLine(primary) == "STOPPED", "primary explicitly disposes its coordinator on its owner thread");
        WaitForExit(primary);
        Check(RunChild(scope, "probe") == "PRIMARY", "new process takes over after normal release");

        // An abrupt exit exercises AbandonedMutexException, not normal disposal.
        var abandonedScope = Guid.NewGuid().ToString("N");
        var abandoned = StartChild(abandonedScope, "primary");
        Check(ReadLine(abandoned) == "READY", "abandonment test primary starts in a separate GUID scope");
        var survivor = StartChild(abandonedScope, "takeover-wait");
        Check(ReadLine(survivor) == "SECONDARY", "survivor holds an existing mutex handle before the primary exits");
        abandoned.Kill(entireProcessTree: true);
        abandoned.WaitForExit();
        survivor.StandardInput.WriteLine("TAKEOVER");
        survivor.StandardInput.Flush();
        Check(ReadLine(survivor) == "PRIMARY", "survivor takes ownership of an abandoned mutex");
        WaitForExit(survivor);

        var logScope = Guid.NewGuid().ToString("N");
        var logPrimary = StartChild(logScope, "primary-log-throws");
        Check(ReadLine(logPrimary) == "READY", "logging failure test uses a separate primary scope");
        Check(RunChild(logScope, "handler-throw") == "REJECT", "throwing logger cannot turn a failed handler into success");
        Check(RunChild(logScope, "activate") == "ACK", "throwing logger cannot terminate the listener");
        logPrimary.StandardInput.WriteLine("STOP");
        logPrimary.StandardInput.Flush();
        Check(ReadLine(logPrimary) == "STOPPED", "logging failure test primary stops normally");
        WaitForExit(logPrimary);

        foreach (var mode in new[] { "fake-ack-short", "fake-ack-wrong", "fake-ack-fragmented" })
        {
            var ackScope = Guid.NewGuid().ToString("N");
            var ackServer = StartChild(ackScope, mode);
            Check(ReadLine(ackServer) == "READY", mode + " uses a separate test-only pipe");
            Check(RunChild(ackScope, "activate") == (mode == "fake-ack-fragmented" ? "ACK" : "REJECT"),
                mode + " requires a complete, exact OK newline response");
            WaitForExit(ackServer);
        }

        Check(!new ActivationNotice("activate", Handoff: true).IsValid &&
            !new ActivationNotice("activate", AcceptedBatch).IsValid &&
            !new ActivationNotice("batch-ready", AcceptedBatch + "\n").IsValid &&
            !new ActivationNotice("batch-ready", AcceptedBatch.ToUpperInvariant()).IsValid,
            "activation and batch identifiers enforce the complete protocol shape");
        foreach (var invalidScope in new[] { "", "../outside", "Global\\outside", "test\n", new string('a', 151) })
        {
            try
            {
                using var invalid = new InstanceCoordinator(invalidScope);
                throw new InvalidOperationException("Unsafe test scope was accepted.");
            }
            catch (ArgumentException) { }
        }
        Check(true, "invalid mutex and pipe scopes are rejected before creating named objects");
        var disposable = new InstanceCoordinator(Guid.NewGuid().ToString("N"));
        Check(disposable.TryBecomePrimary(), "disposal test uses its own primary scope");
        disposable.Dispose();
        disposable.Dispose();
        Check(!disposable.NotifyAsync(new ActivationNotice("activate")).GetAwaiter().GetResult(),
            "repeated disposal is safe and a disposed coordinator cannot notify");
    }

    private static int Child(string[] args)
    {
        if (args.Length != 3 || !Guid.TryParseExact(args[1], "N", out _))
            throw new ArgumentException("Every child must receive an explicit GUID test scope.");
        string scope = args[1];
        using var coordinator = new InstanceCoordinator(scope);
        if (args[2] is "primary" or "primary-log-throws")
        {
            if (!coordinator.TryBecomePrimary()) throw new InvalidOperationException("Test primary lock was occupied.");
            var notices = new ConcurrentQueue<ActivationNotice>();
            coordinator.StartServer(notice =>
            {
                notices.Enqueue(notice);
                if (notice.BatchId == ThrowingBatch) throw new IOException("Synthetic synchronous handler failure.");
                if (notice.BatchId == FaultedBatch) return Task.FromException<bool>(new IOException("Synthetic asynchronous handler failure."));
                if (notice.BatchId == SlowBatch) return Task.Delay(TimeSpan.FromMilliseconds(4300)).ContinueWith(_ => true);
                if (notice.BatchId == StalledBatch) return new TaskCompletionSource<bool>().Task;
                return Task.FromResult(notice.Kind == "activate" || notice.BatchId == AcceptedBatch);
            }, _ =>
            {
                if (args[2] == "primary-log-throws") throw new IOException("Synthetic logging failure.");
            });
            Console.WriteLine("READY");
            string? command;
            while ((command = Console.ReadLine()) is not null)
            {
                if (command == "STATS")
                    Console.WriteLine("STATS:" + string.Join(',', notices.Select(n => n.Kind + ":" + n.BatchId)));
                else if (command == "STOP") break;
                else throw new ArgumentException("Unknown primary command.");
            }
            // The using statement releases on this same thread when Child returns.
            Console.WriteLine("STOPPED");
            return 0;
        }
        if (args[2] is "probe" or "takeover-wait")
        {
            Console.WriteLine(coordinator.TryBecomePrimary() ? "PRIMARY" : "SECONDARY");
            if (args[2] == "takeover-wait")
            {
                if (Console.ReadLine() != "TAKEOVER") throw new ArgumentException("Missing takeover command.");
                Console.WriteLine(coordinator.TryBecomePrimary() ? "PRIMARY" : "SECONDARY");
            }
            return 0;
        }
        if (args[2].StartsWith("fake-ack-", StringComparison.Ordinal))
        {
            using var pipe = new NamedPipeServerStream("ChatPCBridge-main-" + scope, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Console.WriteLine("READY");
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            var one = new byte[1];
            while (pipe.ReadAsync(one, timeout.Token).AsTask().GetAwaiter().GetResult() == 1 && one[0] != (byte)'\n') { }
            var response = Encoding.ASCII.GetBytes(args[2] == "fake-ack-short" ? "OK" : args[2] == "fake-ack-wrong" ? "OK?" : "OK\n");
            foreach (var value in response)
                pipe.WriteAsync(new[] { value }, timeout.Token).AsTask().GetAwaiter().GetResult();
            pipe.FlushAsync(timeout.Token).GetAwaiter().GetResult();
            return 0;
        }
        if (args[2].StartsWith("raw-", StringComparison.Ordinal))
        {
            // Bypass NotifyAsync validation to exercise the server trust boundary.
            using var pipe = new NamedPipeClientStream(".", "ChatPCBridge-main-" + scope,
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            var validJson = JsonSerializer.Serialize(new ActivationNotice("activate"));
            var text = args[2] switch
            {
                "raw-invalid" => JsonSerializer.Serialize(new ActivationNotice("batch-ready", "../outside")) + "\n",
                "raw-empty" => "\n",
                "raw-json" => "{not-json}\n",
                "raw-null" => "null\n",
                "raw-oversize" => validJson.PadRight(1025) + "\n",
                "raw-limit" => validJson.PadRight(1024) + "\n",
                "raw-activate-handoff" => JsonSerializer.Serialize(new ActivationNotice("activate", Handoff: true)) + "\n",
                "raw-activate-batch" => JsonSerializer.Serialize(new ActivationNotice("activate", AcceptedBatch)) + "\n",
                "raw-eof" or "raw-stalled" => validJson,
                "raw-fragmented" => validJson + "\n",
                "raw-utf8" => "{\"Kind\":\"activate\",\"Ignored\":\"?\"}\n",
                _ => throw new ArgumentException("Unknown raw mode.")
            };
            var payload = Encoding.UTF8.GetBytes(text);
            if (args[2] == "raw-utf8") payload[Array.IndexOf(payload, (byte)'?')] = 0xff;
            if (args[2] == "raw-fragmented")
            {
                foreach (var value in payload)
                    pipe.WriteAsync(new[] { value }, timeout.Token).AsTask().GetAwaiter().GetResult();
            }
            else
            {
                try { pipe.WriteAsync(payload, timeout.Token).AsTask().GetAwaiter().GetResult(); }
                catch (IOException) when (args[2] == "raw-oversize") { Console.WriteLine("CLOSED"); return 0; }
            }
            pipe.FlushAsync(timeout.Token).GetAwaiter().GetResult();
            if (args[2] == "raw-eof") { Console.WriteLine("SENT"); return 0; }
            var response = new byte[3];
            try { pipe.ReadExactlyAsync(response, timeout.Token).AsTask().GetAwaiter().GetResult(); }
            catch (IOException) when (args[2] is "raw-stalled" or "raw-oversize") { Console.WriteLine("CLOSED"); return 0; }
            Console.WriteLine(Encoding.ASCII.GetString(response).Trim());
            return 0;
        }
        var noticeToSend = args[2] switch
        {
            "activate" => new ActivationNotice("activate"),
            "batch" => new ActivationNotice("batch-ready", AcceptedBatch, true),
            "handler-reject" => new ActivationNotice("batch-ready", RejectedBatch, true),
            "handler-throw" => new ActivationNotice("batch-ready", ThrowingBatch),
            "handler-fault" => new ActivationNotice("batch-ready", FaultedBatch),
            "handler-slow" => new ActivationNotice("batch-ready", SlowBatch),
            "handler-stalled" => new ActivationNotice("batch-ready", StalledBatch),
            "invalid" => new ActivationNotice("batch-ready", "../outside", true),
            _ => throw new ArgumentException("Unknown child mode.")
        };
        Console.WriteLine(coordinator.NotifyAsync(noticeToSend).GetAwaiter().GetResult() ? "ACK" : "REJECT");
        return 0;
    }

    private static Process StartChild(string scope, string mode)
    {
        // Never accept a production SID/session scope, even inside the harness.
        if (!Guid.TryParseExact(scope, "N", out _)) throw new ArgumentException("A GUID scope is required.");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--child");
        start.ArgumentList.Add(scope);
        start.ArgumentList.Add(mode);
        var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start test child.");
        // Main's finally block cleans up only processes created by this harness.
        Children.Add(child);
        return child;
    }

    private static string RunChild(string scope, string mode)
    {
        var child = StartChild(scope, mode);
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try { WaitForExit(child); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Child failed: " + stderr.GetAwaiter().GetResult(), ex);
        }
        string error = stderr.GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException("Unexpected child stderr: " + error);
        return stdout.GetAwaiter().GetResult().Trim();
    }

    private static string ReadLine(Process child)
    {
        try { return child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult() ?? throw new IOException("Child closed stdout."); }
        catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
    }

    private static void WaitForExit(Process child)
    {
        try { child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
        catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
        if (child.ExitCode != 0) throw new InvalidOperationException($"Child exited {child.ExitCode}.");
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException("FAIL: " + message);
        _passed++;
        Console.WriteLine("PASS: " + message);
    }
}
