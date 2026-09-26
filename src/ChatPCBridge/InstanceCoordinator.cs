using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatPCBridge;

public sealed record ActivationNotice(string Kind, string? BatchId = null, bool Handoff = false)
{
    public bool IsValid => Kind == "activate" && BatchId is null && !Handoff ||
        Kind == "batch-ready" && BatchId is { Length: 24 } &&
        Regex.IsMatch(BatchId, @"\A[0-9]{8}-[0-9]{6}-[0-9a-f]{8}\z");
}

// A live ShareOperation stays in its activated process until saved. Only the
// primary process owns a window; other processes notify it and then exit.
public sealed class InstanceCoordinator : IDisposable
{
    private const int MaxNoticeBytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _server;
    private bool _ownsMutex;
    private bool _disposed;

    public InstanceCoordinator(string? testScope = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var userId = identity.User?.Value ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var scope = testScope ?? $"{userId}-{process.SessionId}";
        if (!Regex.IsMatch(scope, @"\A[a-zA-Z0-9_-]{1,150}\z")) throw new ArgumentException("Invalid instance scope.");
        _pipeName = "ChatPCBridge-main-" + scope;
        // The name routes per-user/session instances; it is not authentication.
        // CurrentUserOnly restricts OS access, not other programs run by this user.
        _mutex = new Mutex(false, _pipeName,
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true });
    }

    // Call and dispose on the WPF dispatcher thread (mutex ownership).
    public bool TryBecomePrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ownsMutex) return true;
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        return _ownsMutex;
    }

    public void StartServer(Func<ActivationNotice, Task<bool>> handler, Action<Exception> log)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(log);
        if (!_ownsMutex || _server is not null) throw new InvalidOperationException("Only the primary instance can listen.");
        _server = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var token = requestTimeout.Token;
                    var notice = await ReadNoticeAsync(pipe, token);
                    var accepted = false;
                    if (notice?.IsValid == true)
                    {
                        try
                        {
                            // A timeout cannot cancel arbitrary handler work. Never ACK
                            // until it has actually returned true; do not retry here.
                            accepted = await handler(notice).WaitAsync(token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            TryLog(log, ex);
                        }
                    }
                    await pipe.WriteAsync(Encoding.ASCII.GetBytes(accepted ? "OK\n" : "NO\n"), token);
                    await pipe.FlushAsync(token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    TryLog(log, ex);
                    try { await Task.Delay(100, _stop.Token); } catch (OperationCanceledException) { break; }
                }
            }
        });
    }

    public async Task<bool> NotifyAsync(ActivationNotice notice)
    {
        if (_disposed || !notice.IsValid) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            // The server permits five seconds after connection; leave time for
            // its reply instead of expiring first during a slow UI activation.
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(notice) + "\n"), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            var response = new byte[3];
            await pipe.ReadExactlyAsync(response, timeout.Token);
            return Encoding.ASCII.GetString(response) == "OK\n";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<ActivationNotice?> ReadNoticeAsync(Stream pipe, CancellationToken token)
    {
        // One newline-terminated UTF-8 JSON frame per connection. EOF is never
        // a terminator, and the extra byte permits exactly 1 KiB plus newline.
        var bytes = new byte[MaxNoticeBytes + 1];
        for (var count = 0; count <= MaxNoticeBytes; count++)
        {
            if (await pipe.ReadAsync(bytes.AsMemory(count, 1), token) != 1) return null;
            if (bytes[count] != (byte)'\n') continue;
            try { return JsonSerializer.Deserialize<ActivationNotice>(StrictUtf8.GetString(bytes, 0, count)); }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException) { return null; }
        }
        return null;
    }

    private static void TryLog(Action<Exception> log, Exception error)
    {
        // Disk/logging failures must not permanently stop instance activation.
        try { log(error); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_ownsMutex) { _mutex.ReleaseMutex(); _ownsMutex = false; }
        _mutex.Dispose();
        // Do not synchronously join the server on the UI thread: it may itself
        // be waiting for a pending dispatcher notification.
        if (_server is null) _stop.Dispose();
        else _ = _server.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
