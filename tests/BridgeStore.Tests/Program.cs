using System.Text;
using System.Text.Json;
using ChatPCBridge;
using ChatPCBridge.Core;

// All I/O uses this unique disposable directory. Never invoke production Root,
// import/share activation, settings, clipboard, or the current-user inbox.
var root = Path.Combine(Path.GetTempPath(), "ChatPCBridge-store-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var tests = new (string Name, Func<Task> Run)[]
{
    ("Bounded copy accepts the exact limit", ExactLimit),
    ("Windows StorageFile streams work with bounded copy", StorageFileStream),
    ("Unknown-length streams cannot overrun the write limit", OversizedStream),
    ("Failed reads remove partial output and preserve the source", FailedRead),
    ("Existing destination files cannot be overwritten", NoOverwrite),
    ("Cancelled copy removes partial output", CancelledCopy),
    ("Valid batch and analysis paths are accepted", ValidBatch),
    ("Manifest folder redirection is rejected", ForeignFolder),
    ("Manifest attachment traversal is rejected", ForeignFile),
    ("Prefix-sibling attachment paths are rejected", SiblingPath),
    ("Manifest analysis paths outside the analysis directory are rejected", ForeignText),
    ("Invalid and mismatched batch IDs are rejected", InvalidId),
    ("Oversized JSON manifests are rejected", OversizedManifest),
    ("Null and duplicate file entries are rejected", InvalidFiles),
    ("Oversized batch metadata is rejected", OversizedBatch),
};
var failures = 0;
try
{
    foreach (var test in tests)
    {
        try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
        catch (Exception exception) { failures++; Console.Error.WriteLine("FAIL " + test.Name + ": " + exception); }
    }
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

async Task ExactLimit()
{
    var path = NewOutput();
    var bytes = Encoding.UTF8.GetBytes("精确读取 🧩");
    using var source = new MemoryStream(bytes);
    var copied = await BridgeStore.CopyWithLimitAsync(source, path, bytes.Length);
    Assert(copied == bytes.Length && (await File.ReadAllBytesAsync(path)).SequenceEqual(bytes), "Exact-limit copy failed.");
}

async Task OversizedStream()
{
    var path = NewOutput();
    using var source = new UnknownLengthStream(new byte[300_000]);
    await ExpectException<IOException>(() => BridgeStore.CopyWithLimitAsync(source, path, 100_000));
    Assert(source.BytesRead <= 100_001, "Reader consumed more than the quota plus one EOF probe byte.");
    Assert(!File.Exists(path) && !Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Any(), "Partial output survived quota failure.");
}

async Task StorageFileStream()
{
    var inputPath = NewOutput();
    var content = Encoding.UTF8.GetBytes("Windows synthetic shared stream 🧩");
    await File.WriteAllBytesAsync(inputPath, content);
    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(inputPath);
    var outputPath = NewOutput();
    using (var sharedStream = await file.OpenReadAsync())
    await using (var source = sharedStream.AsStreamForRead())
    {
        var copied = await BridgeStore.CopyWithLimitAsync(source, outputPath, content.Length);
        Assert(copied == content.Length, "StorageFile stream length changed.");
    }
    Assert((await File.ReadAllBytesAsync(outputPath)).SequenceEqual(content), "StorageFile stream contents changed.");
}

async Task FailedRead()
{
    var path = NewOutput();
    using var source = new UnknownLengthStream(new byte[300_000], failAfterFirstRead: true);
    await ExpectException<IOException>(() => BridgeStore.CopyWithLimitAsync(source, path, 300_000));
    Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Any(), "Failed read left partial output.");
}

async Task NoOverwrite()
{
    var path = NewOutput();
    await File.WriteAllTextAsync(path, "original");
    using var source = new MemoryStream(new byte[] { 1, 2, 3 });
    await ExpectException<IOException>(() => BridgeStore.CopyWithLimitAsync(source, path, 3));
    Assert(await File.ReadAllTextAsync(path) == "original", "Original destination was overwritten.");
    Assert(Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Count() == 1, "Collision left a partial file.");
}

async Task CancelledCopy()
{
    var path = NewOutput();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    using var source = new MemoryStream(new byte[10]);
    await ExpectException<OperationCanceledException>(() => BridgeStore.CopyWithLimitAsync(source, path, 10, cancellation.Token));
    Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Any(), "Cancelled copy left partial output.");
}

Task ValidBatch()
{
    var (inbox, batch) = MakeBatch();
    WriteBatch(batch);
    Assert(BridgeStore.ReadBatchFromInbox(inbox, batch.Id)?.Files.Count == 1, "Valid batch was rejected.");
    return Task.CompletedTask;
}

Task ForeignFolder()
{
    var (inbox, batch) = MakeBatch();
    var manifest = Path.Combine(batch.Folder, "batch.json");
    batch.Folder = Path.Combine(root, "outside");
    File.WriteAllText(manifest, JsonSerializer.Serialize(batch));
    Assert(BridgeStore.ReadBatchFromInbox(inbox, batch.Id) is null, "Foreign folder was accepted.");
    return Task.CompletedTask;
}

Task ForeignFile()
{
    var (inbox, batch) = MakeBatch();
    batch.Files[0].SavedPath = Path.Combine(batch.Folder, "..", "..", "private.txt");
    WriteBatch(batch);
    Assert(BridgeStore.ReadBatchFromInbox(inbox, batch.Id) is null, "Traversal attachment was accepted.");
    return Task.CompletedTask;
}

Task SiblingPath()
{
    var (inbox, batch) = MakeBatch();
    batch.Files[0].SavedPath = batch.Folder + "-sibling\\secret.txt";
    WriteBatch(batch);
    Assert(BridgeStore.ReadBatchFromInbox(inbox, batch.Id) is null, "Prefix sibling escaped its directory.");
    return Task.CompletedTask;
}

Task ForeignText()
{
    var (inbox, batch) = MakeBatch();
    batch.Files[0].Inspection = new ArchiveInspection(true, 1, "test", new[] { Path.Combine(batch.Folder, "secret.txt") }, Array.Empty<string>());
    WriteBatch(batch);
    Assert(BridgeStore.ReadBatchFromInbox(inbox, batch.Id) is null, "Foreign analysis path was accepted.");
    return Task.CompletedTask;
}

Task InvalidId()
{
    var (inbox, batch) = MakeBatch();
    var expectedId = batch.Id;
    batch.Id = "20260101-000000-ffffffff";
    WriteBatch(batch);
    Assert(BridgeStore.ReadBatchFromInbox(inbox, expectedId) is null, "Mismatched ID was accepted.");
    Assert(BridgeStore.ReadBatchFromInbox(inbox, "../other") is null, "Traversal ID was accepted.");
    return Task.CompletedTask;
}

Task OversizedManifest()
{
    var (inbox, batch) = MakeBatch();
    File.WriteAllText(Path.Combine(batch.Folder, "batch.json"), new string(' ', 1024 * 1024 + 1));
    Assert(BridgeStore.ReadBatchFromInbox(inbox, batch.Id) is null, "Oversized manifest was accepted.");
    return Task.CompletedTask;
}

Task InvalidFiles()
{
    var (inbox, batch) = MakeBatch();
    batch.Files.Add(batch.Files[0]);
    WriteBatch(batch);
    Assert(BridgeStore.ReadBatchFromInbox(inbox, batch.Id) is null, "Duplicate attachment path was accepted.");
    batch.Files = null!;
    WriteBatch(batch);
    Assert(BridgeStore.ReadBatchFromInbox(inbox, batch.Id) is null, "Null files were accepted.");
    return Task.CompletedTask;
}

Task OversizedBatch()
{
    var (inbox, batch) = MakeBatch();
    batch.Files = Enumerable.Range(1, 3).Select(index => new ReceivedFile
    {
        Name = "large.zip", SavedPath = Path.Combine(batch.Folder, index.ToString(), "large.zip"), Size = 2L * 1024 * 1024 * 1024
    }).ToList();
    WriteBatch(batch);
    Assert(BridgeStore.ReadBatchFromInbox(inbox, batch.Id) is null, "Batch quota metadata was accepted.");
    return Task.CompletedTask;
}

string NewOutput()
{
    var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return Path.Combine(directory, "result.zip");
}

(string Inbox, BatchRecord Batch) MakeBatch()
{
    var inbox = Path.Combine(root, Guid.NewGuid().ToString("N"), "Inbox");
    var id = "20260926-120000-" + Guid.NewGuid().ToString("N")[..8];
    var folder = Path.Combine(inbox, id);
    var fileDirectory = Path.Combine(folder, "01");
    var text = Path.Combine(fileDirectory, "analysis", Guid.NewGuid().ToString("N"), "chat-001.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(text)!);
    File.WriteAllText(text, "synthetic");
    var file = Path.Combine(fileDirectory, "synthetic.zip");
    File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
    return (inbox, new BatchRecord
    {
        Id = id, Folder = folder, State = "saved", Source = "synthetic test", Formats = ["StorageItems"],
        Files = [new ReceivedFile { Name = "synthetic.zip", SavedPath = file, Size = 3,
            Inspection = new ArchiveInspection(true, 1, "synthetic", new[] { text }, Array.Empty<string>()) }]
    });
}

static void WriteBatch(BatchRecord batch) => File.WriteAllText(Path.Combine(batch.Folder, "batch.json"), JsonSerializer.Serialize(batch));
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task ExpectException<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

sealed class UnknownLengthStream(byte[] data, bool failAfterFirstRead = false) : Stream
{
    public int BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (failAfterFirstRead && BytesRead > 0) throw new IOException("Synthetic read failure.");
        var read = Math.Min(count, data.Length - BytesRead);
        Array.Copy(data, BytesRead, buffer, offset, read);
        BytesRead += read;
        return read;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = new byte[buffer.Length];
        var read = Read(temporary, 0, temporary.Length);
        temporary.AsMemory(0, read).CopyTo(buffer);
        return ValueTask.FromResult(read);
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
