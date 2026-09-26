using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ChatPCBridge.Core;

var testDirectory = Directory.CreateTempSubdirectory("ChatPCBridge-tests-");
var root = testDirectory.FullName;
var tests = new (string Name, Func<Task> Run)[]
{
    ("UTF-8 Chinese text is preserved and original archive is unchanged", NormalUnicode),
    ("UTF-16 BOM text is converted without losing Chinese characters", Utf16),
    ("Invalid ZIP reports failure", InvalidZip),
    ("Archive traversal names cannot write outside the generated directory", Traversal),
    ("Oversized compressed text is not extracted", Oversized),
    ("Forged text length cannot evade the actual read cap", ForgedLength),
    ("Cumulative text read budget is bounded", TotalBudget),
    ("Empty archives and archives without TXT are handled", NoText),
    ("Invalid text encoding, empty text, and symlinks are reported", InvalidText),
    ("Too many archive entries are rejected before extraction", TooManyEntries),
    ("Forged directory entry count cannot bypass the member limit", ForgedEntryCount),
    ("Compressed input has its own budget", CompressedBudget),
    ("UTF-16 conversion cannot exceed the output text limit", ConvertedOutputBudget),
    ("Only eight text files are prepared", ManyTextFiles),
    ("Cancellation is propagated", Cancellation),
};
var failures = 0;
try
{
    foreach (var test in tests)
    {
        try
        {
            await test.Run();
            Console.WriteLine("PASS " + test.Name);
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine("FAIL " + test.Name + Environment.NewLine + exception);
        }
    }
}
finally
{
    testDirectory.Delete(recursive: true);
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

async Task NormalUnicode()
{
    const string message = "张三 2026-09-26 19:00\n你好，ChatGPT！🧩\nhttps://example.com\n";
    var path = MakeZip(("聊天记录.txt", Encoding.UTF8.GetBytes(message)), ("图片/图.png", new byte[] { 1, 2 }));
    var before = SHA256.HashData(await File.ReadAllBytesAsync(path));
    var result = await ArchiveInspector.InspectAsync(path);
    Assert(result.IsZip && result.EntryCount == 2 && result.Warnings.Count == 0, "Archive status is incorrect.");
    Assert(result.TextFiles.Count == 1, "Expected one generated text file.");
    Assert(await File.ReadAllTextAsync(result.TextFiles[0]) == message, "Chat text changed.");
    var after = SHA256.HashData(await File.ReadAllBytesAsync(path));
    Assert(before.SequenceEqual(after), "Original ZIP changed.");
    Assert(result.Summary.Contains("图片 1 个"), "Media count is missing.");
}

async Task Utf16()
{
    const string message = "李四：保持编码\r\n测试 UTF-16 🌞";
    var encoding = new UnicodeEncoding(false, true, true);
    var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(message)).ToArray();
    var result = await ArchiveInspector.InspectAsync(MakeZip(("chat.txt", bytes)));
    Assert(result.TextFiles.Count == 1 && result.Warnings.Count == 0, "UTF-16 failed.");
    Assert(await File.ReadAllTextAsync(result.TextFiles[0]) == message, "UTF-16 text changed.");
}

async Task InvalidZip()
{
    var path = NewPath();
    await File.WriteAllTextAsync(path, "This is not a ZIP.");
    var result = await ArchiveInspector.InspectAsync(path);
    Assert(!result.IsZip && result.TextFiles.Count == 0 && result.Warnings.Count > 0, "Invalid ZIP was accepted.");
}

async Task Traversal()
{
    var path = MakeZip(("../../outside.txt", Encoding.UTF8.GetBytes("safe contents")),
        ("C:\\escape.txt", Encoding.UTF8.GetBytes("also safe")));
    var result = await ArchiveInspector.InspectAsync(path);
    var outputRoot = Path.Combine(Path.GetDirectoryName(path)!, "analysis") + Path.DirectorySeparatorChar;
    Assert(result.TextFiles.Count == 2, "Text contents should remain available.");
    Assert(result.TextFiles.All(file => Path.GetFullPath(file).StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase)),
        "A generated file escaped its output directory.");
    Assert(result.TextFiles.All(file => Path.GetFileName(file).StartsWith("chat-")), "Member names were used as output paths.");
    Assert(!File.Exists(Path.Combine(root, "outside.txt")), "Traversal file was written.");
}

async Task Oversized()
{
    var path = MakeZip(("large.txt", Enumerable.Repeat((byte)'a', 16 * 1024 * 1024 + 1).ToArray()));
    Assert(new FileInfo(path).Length < 128 * 1024, "Test payload was not sufficiently compressed.");
    var result = await ArchiveInspector.InspectAsync(path);
    Assert(result.IsZip && result.TextFiles.Count == 0 && result.Warnings.Count > 0, "Oversized text was prepared.");
}

async Task ForgedLength()
{
    var path = MakeZip(("forged.txt", Enumerable.Repeat((byte)'x', 16 * 1024 * 1024 + 100).ToArray()));
    var bytes = await File.ReadAllBytesAsync(path);
    for (var offset = 0; offset <= bytes.Length - 46; offset++)
    {
        if (BitConverter.ToUInt32(bytes, offset) == 0x02014B50)
        {
            BitConverter.GetBytes(16U).CopyTo(bytes, offset + 24);
            break;
        }
    }
    await File.WriteAllBytesAsync(path, bytes);
    var result = await ArchiveInspector.InspectAsync(path);
    Assert(result.TextFiles.Count == 0 && result.Warnings.Count > 0, "Forged length evaded the read cap.");
}

async Task TotalBudget()
{
    var content = Enumerable.Repeat((byte)'a', 12 * 1024 * 1024).ToArray();
    var result = await ArchiveInspector.InspectAsync(MakeZip(("a.txt", content), ("b.txt", content), ("c.txt", content)));
    Assert(result.TextFiles.Count == 2 && result.Warnings.Count > 0, "Cumulative budget was not enforced.");
    Assert(result.TextFiles.Sum(path => new FileInfo(path).Length) <= 32L * 1024 * 1024, "Output exceeded the budget.");
}

async Task NoText()
{
    var empty = await ArchiveInspector.InspectAsync(MakeZip());
    Assert(empty.IsZip && empty.EntryCount == 0 && empty.TextFiles.Count == 0, "Empty ZIP failed.");
    var missing = await ArchiveInspector.InspectAsync(MakeZip(("image.png", new byte[] { 1 })));
    Assert(missing.IsZip && missing.EntryCount == 1 && missing.TextFiles.Count == 0, "Media-only ZIP failed.");
    Assert(missing.Summary.Contains("未找到 TXT"), "Missing TXT should be explained.");
}

async Task InvalidText()
{
    var path = MakeZip(("invalid.txt", new byte[] { 0xFF, 0xFF, 0xFF }),
        ("empty.txt", Encoding.UTF8.GetBytes(" \r\n")), ("link.txt", Encoding.UTF8.GetBytes("../secret")));
    using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        archive.GetEntry("link.txt")!.ExternalAttributes = unchecked((int)0xA1FF0000);
    var result = await ArchiveInspector.InspectAsync(path);
    Assert(result.TextFiles.Count == 0 && result.Warnings.Count == 3, "Invalid/empty/link entries were not reported.");
}

async Task TooManyEntries()
{
    var entries = Enumerable.Range(0, 2001).Select(index => ($"{index}.txt", new byte[] { 97 })).ToArray();
    var result = await ArchiveInspector.InspectAsync(MakeZip(entries));
    Assert(result.EntryCount == 2001 && result.TextFiles.Count == 0 && result.Warnings.Count > 0, "Entry limit failed.");
}

async Task ManyTextFiles()
{
    var entries = Enumerable.Range(0, 10).Select(index => ($"{index}.txt", new byte[] { 97 })).ToArray();
    var result = await ArchiveInspector.InspectAsync(MakeZip(entries));
    Assert(result.IsZip && result.TextFiles.Count == 8 && result.Warnings.Count > 0, "Text-file limit failed.");
}

async Task ForgedEntryCount()
{
    var entries = Enumerable.Range(0, 2001).Select(index => ($"{index}.txt", new byte[] { 97 })).ToArray();
    var path = MakeZip(entries);
    var bytes = await File.ReadAllBytesAsync(path);
    var end = bytes.Length - 22;
    BitConverter.GetBytes((ushort)1).CopyTo(bytes, end + 8);
    BitConverter.GetBytes((ushort)1).CopyTo(bytes, end + 10);
    await File.WriteAllBytesAsync(path, bytes);
    var result = await ArchiveInspector.InspectAsync(path);
    Assert(result.TextFiles.Count == 0 && result.EntryCount == 2001 && result.Warnings.Count > 0,
        "Forged EOCD count bypassed directory validation.");
}

async Task CompressedBudget()
{
    var path = MakeZip(("chat.txt", Encoding.UTF8.GetBytes("synthetic")));
    var bytes = await File.ReadAllBytesAsync(path);
    for (var offset = 0; offset <= bytes.Length - 46; offset++)
    {
        if (BitConverter.ToUInt32(bytes, offset) != 0x02014B50) continue;
        BitConverter.GetBytes(32U * 1024 * 1024 + 1).CopyTo(bytes, offset + 20);
        break;
    }
    await File.WriteAllBytesAsync(path, bytes);
    var result = await ArchiveInspector.InspectAsync(path);
    Assert(result.TextFiles.Count == 0 && result.Warnings.Any(warning => warning.Contains("读取容量")),
        "Compressed input limit was not checked before decoding.");
}

async Task ConvertedOutputBudget()
{
    var text = new string('中', 6 * 1024 * 1024);
    var encoding = new UnicodeEncoding(false, true, true);
    var content = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
    var result = await ArchiveInspector.InspectAsync(MakeZip(("utf16.txt", content)));
    Assert(result.TextFiles.Count == 0 && result.Warnings.Any(warning => warning.Contains("转换后的文本过大")),
        "UTF-16 transcoding expanded beyond the output limit.");
}

async Task Cancellation()
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try { await ArchiveInspector.InspectAsync(MakeZip(), cancellation.Token); }
    catch (OperationCanceledException) { return; }
    throw new Exception("Cancellation was ignored.");
}

string NewPath()
{
    var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return Path.Combine(directory, "chat.zip");
}

string MakeZip(params (string Name, byte[] Content)[] entries)
{
    var path = NewPath();
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var entry in entries)
    {
        using var output = archive.CreateEntry(entry.Name, CompressionLevel.SmallestSize).Open();
        output.Write(entry.Content);
    }
    return path;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
