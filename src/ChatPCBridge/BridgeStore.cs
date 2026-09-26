using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatPCBridge.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;

namespace ChatPCBridge;

public sealed class ReceivedFile
{
    public string Name { get; set; } = "";
    public string SavedPath { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public ArchiveInspection? Inspection { get; set; }
    public string? Error { get; set; }
}

public sealed class BatchRecord
{
    public string Id { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.Now;
    public string Source { get; set; } = "";
    public string Folder { get; set; } = "";
    public string State { get; set; } = "receiving";
    public string? Error { get; set; }
    public string[] Formats { get; set; } = [];
    public List<ReceivedFile> Files { get; set; } = [];
    [JsonIgnore] public string Title => Files.Count == 0 ? "接收记录" : Files[0].Name + (Files.Count > 1 ? $" 等 {Files.Count} 个文件" : "");
    [JsonIgnore] public string Subtitle => $"{ReceivedAt:MM-dd HH:mm} · {(State == "saved" ? "已保存" : State == "receiving" ? "未完成" : "接收有误")}";
}

public sealed class BridgeSettings
{
    public bool AutoOpenChatGpt { get; set; } = true;
    public string ChatGptTarget { get; set; } = "current";
}

public static class BridgeStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string Root { get; } = ResolveRoot();
    public static string Inbox => Path.Combine(Root, "Inbox");
    private const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxBatchBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxFiles = 32;
    private static string ResolveRoot()
    {
        // Outside MSIX LocalState so removing/updating the app preserves received archives.
        // Avoid Documents/Desktop, which may be redirected to a cloud sync folder.
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ChatPCBridge");
    }

    public static void Log(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(Root);
            // Exception messages can contain private file names and full user paths.
            var safeMessage = new string(message.Where(character => !char.IsControl(character)).Take(200).ToArray());
            File.AppendAllText(Path.Combine(Root, "bridge.log"), $"{DateTimeOffset.Now:O} {safeMessage}{(exception is null ? "" : $" | {exception.GetType().Name} (0x{exception.HResult:X8})")}\n");
        }
        catch { }
    }

    public static BridgeSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(Path.Combine(Root, "settings.json"))) ?? new(); }
        catch { return new(); }
    }
    public static void SaveSettings(BridgeSettings settings) => File.WriteAllText(Path.Combine(Root, "settings.json"), JsonSerializer.Serialize(settings, Json));

    public static IEnumerable<BatchRecord> LoadBatches()
    {
        Directory.CreateDirectory(Inbox);
        foreach (var dir in Directory.EnumerateDirectories(Inbox).OrderDescending().Take(80))
        {
            var batch = ReadBatchFromInbox(Inbox, Path.GetFileName(dir));
            if (batch is not null) yield return batch;
        }
    }
    public static BatchRecord? LoadBatch(string id)
    {
        return ReadBatchFromInbox(Inbox, id);
    }

    // Metadata read from disk is untrusted. An IPC notice conveys only a batch ID;
    // neither the notice nor a changed manifest may nominate arbitrary local files.
    internal static BatchRecord? ReadBatchFromInbox(string inbox, string id)
    {
        if (!new ActivationNotice("batch-ready", id).IsValid) return null;
        try
        {
            var folder = Path.Combine(Path.GetFullPath(inbox), id);
            var path = Path.Combine(folder, "batch.json");
            RejectLink(Path.GetDirectoryName(Path.GetFullPath(inbox))!);
            EnsureContainedPath(inbox, path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxManifestBytes) return null;
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            while (bytes.Length <= MaxManifestBytes)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, MaxManifestBytes + 1 - bytes.Length));
                if (read == 0) break;
                bytes.Write(buffer, 0, read);
            }
            if (bytes.Length > MaxManifestBytes) return null;
            var batch = JsonSerializer.Deserialize<BatchRecord>(bytes.ToArray());
            if (batch is null) return null;
            ValidateBatch(batch, inbox, id);
            return batch;
        }
        catch { return null; }
    }

    private static void ValidateBatch(BatchRecord batch, string inbox, string expectedId)
    {
        if (!new ActivationNotice("batch-ready", expectedId).IsValid || batch.Id != expectedId ||
            batch.State is not ("receiving" or "saved" or "error") || batch.Files is null ||
            batch.Files.Count > MaxFiles || batch.Formats is null || batch.Formats.Length > 64)
            throw new InvalidDataException("Invalid batch metadata.");
        var folder = Path.GetFullPath(Path.Combine(inbox, expectedId));
        if (!Path.IsPathFullyQualified(batch.Folder) ||
            !string.Equals(Path.GetFullPath(batch.Folder), folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid batch folder.");
        EnsureContainedPath(inbox, folder);
        RejectLink(Path.GetDirectoryName(Path.GetFullPath(inbox))!);
        long total = 0;
        var savedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in batch.Files)
        {
            if (file is null || file.Size < 0 || file.Size > MaxFileBytes)
                throw new InvalidDataException("Invalid file metadata.");
            EnsureContainedPath(folder, file.SavedPath);
            if (Path.GetExtension(file.SavedPath).ToLowerInvariant() is not (".zip" or ".txt") ||
                !savedPaths.Add(Path.GetFullPath(file.SavedPath)))
                throw new InvalidDataException("Invalid saved file path.");
            total += file.Size;
            if (total > MaxBatchBytes) throw new InvalidDataException("Batch size exceeds the limit.");
            if (File.Exists(file.SavedPath) && new FileInfo(file.SavedPath).Length > MaxFileBytes)
                throw new InvalidDataException("Saved file size exceeds the limit.");
            if (file.Inspection is not { } inspection) continue;
            if (inspection.TextFiles is null || inspection.TextFiles.Count > 8 || inspection.Warnings is null ||
                inspection.Warnings.Count > 25)
                throw new InvalidDataException("Invalid text inspection metadata.");
            var analysis = Path.Combine(Path.GetDirectoryName(file.SavedPath)!, "analysis");
            foreach (var textPath in inspection.TextFiles)
            {
                EnsureContainedPath(analysis, textPath);
                if (!Path.GetExtension(textPath).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid analysis file path.");
                if (File.Exists(textPath) && new FileInfo(textPath).Length > 16 * 1024 * 1024)
                    throw new InvalidDataException("Analysis file size exceeds the limit.");
            }
        }
    }

    internal static void EnsureContainedPath(string directory, string path)
    {
        if (!Path.IsPathFullyQualified(directory) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("A stored path is not absolute.");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A stored path leaves its batch folder.");
        var relative = Path.GetRelativePath(root, full);
        if (relative.Contains(':')) throw new InvalidDataException("Alternate data streams are not supported.");
        RejectLink(root);
        var current = root;
        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            RejectLink(current);
        }
    }

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked archive paths are not supported.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    private static BatchRecord NewBatch(string source, string[] formats)
    {
        string id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var batch = new BatchRecord { Id = id, Source = source, Formats = formats, Folder = Path.Combine(Inbox, id) };
        EnsureContainedPath(Root, batch.Folder);
        Directory.CreateDirectory(batch.Folder);
        Save(batch);
        return batch;
    }
    public static void Save(BatchRecord batch)
    {
        ValidateBatch(batch, Inbox, batch.Id);
        var dest = Path.Combine(batch.Folder, "batch.json");
        EnsureContainedPath(Inbox, dest);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, Json);
        if (bytes.Length > MaxManifestBytes) throw new InvalidDataException("Batch metadata exceeds the size limit.");
        var temporary = dest + ".new-" + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(bytes);
            }
            File.Move(temporary, dest, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
    private static string SafeFileName(string name)
    {
        name = Path.GetFileName(name).TrimEnd('.', ' ');
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 180) name = name[..140] + Path.GetExtension(name)[..Math.Min(20, Path.GetExtension(name).Length)];
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(name) || stem is "CON" or "PRN" or "AUX" or "NUL" || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[0-9¹²³]$")) name = "received_" + name;
        return name;
    }
    private static string FileDestination(BatchRecord batch, int number, string name)
    {
        string dir = Path.Combine(batch.Folder, $"{number:D2}");
        EnsureContainedPath(Inbox, dir);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, SafeFileName(name));
    }

    internal static async Task<long> CopyWithLimitAsync(Stream source, string destination, long byteLimit,
        CancellationToken cancellationToken = default)
    {
        if (byteLimit < 0) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            long copied = 0;
            await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                created = true;
                var buffer = new byte[81920];
                while (true)
                {
                    var remaining = byteLimit - copied;
                    var count = (int)Math.Min(buffer.Length, remaining >= buffer.Length ? buffer.Length : remaining + 1);
                    var read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (read > remaining) throw new IOException("文件实际大小超过接收上限，未完成副本已清理。");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(partial, destination, overwrite: false);
            created = false;
            return copied;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(partial); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static async Task<BatchRecord> ReceiveAsync(ShareOperation operation, Action<string> status)
    {
        BatchRecord? batch = null;
        try
        {
            operation.ReportStarted();
            var data = operation.Data;
            batch = NewBatch("Windows 分享", data.AvailableFormats.ToArray());
            if (!data.Contains(StandardDataFormats.StorageItems))
                throw new InvalidOperationException("没有收到文件。请在来源应用中使用合并转发，并选择聊天 ZIP。");
            var items = await data.GetStorageItemsAsync();
            if (items.Count == 0 || items.Count > MaxFiles) throw new InvalidOperationException($"每次可接收 1–{MaxFiles} 个文件。");
            long batchBytes = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not StorageFile file) throw new InvalidOperationException("此次分享包含文件夹，请改为分享 ZIP 文件。");
                string extension = Path.GetExtension(file.Name).ToLowerInvariant();
                if (extension is not (".zip" or ".txt")) throw new InvalidOperationException("当前版本接收 ZIP 和 TXT 文件。");
                status($"正在完整保存 {i + 1}/{items.Count}：{file.Name}");
                var basic = await file.GetBasicPropertiesAsync();
                if (basic.Size > MaxFileBytes) throw new InvalidOperationException("单个文件超过 2 GB，建议分批选择记录。");
                var copyLimit = Math.Min(MaxFileBytes, MaxBatchBytes - batchBytes);
                if (basic.Size > (ulong)copyLimit) throw new InvalidOperationException("本批文件总大小超过 4 GB，请分批转发。");
                string destination = FileDestination(batch, i + 1, file.Name);
                long size;
                // Opening and reading the stream hydrates deferred shared files while the
                // ShareOperation is alive, without trusting the provider's advertised size.
                using (var sharedStream = await file.OpenReadAsync())
                await using (var source = sharedStream.AsStreamForRead())
                    size = await CopyWithLimitAsync(source, destination, copyLimit);
                if (basic.Size != 0 && size != (long)basic.Size)
                    throw new IOException("文件长度校验未通过，未将此次接收标记为成功。");
                batchBytes += size;
                batch.Files.Add(new ReceivedFile { Name = file.Name, SavedPath = destination, Size = size });
                Save(batch);
            }
            batch.State = "saved";
            Save(batch);
            operation.ReportDataRetrieved();
            operation.ReportCompleted();
            Log($"Share saved: {batch.Id}; files={batch.Files.Count}");
        }
        catch (Exception ex)
        {
            if (batch is not null)
            {
                batch.State = "error"; batch.Error = ex.Message;
                try { Save(batch); } catch (Exception saveError) { Log("Failed to persist receive error", saveError); }
            }
            try { operation.ReportError("ChatPCBridge：" + ex.Message); } catch { }
            Log("Share failed", ex);
            throw;
        }
        // Content inspection is intentionally after the source is free to finish.
        await InspectBatchAsync(batch!, status);
        return batch!;
    }

    public static async Task<BatchRecord> ImportAsync(string[] paths, Action<string> status)
    {
        if (paths.Length == 0 || paths.Length > MaxFiles) throw new InvalidOperationException($"每次可导入 1–{MaxFiles} 个文件。");
        var batch = NewBatch("手动导入", ["StorageItems"]);
        try
        {
            long batchBytes = 0;
            for (int i = 0; i < paths.Length; i++)
            {
                var info = new FileInfo(paths[i]);
                if (!info.Exists || info.Extension.ToLowerInvariant() is not (".zip" or ".txt")) throw new InvalidOperationException("请选择 ZIP 或 TXT 文件。");
                if (info.Length > MaxFileBytes) throw new InvalidOperationException("单个文件不能超过 2 GB。");
                var copyLimit = Math.Min(MaxFileBytes, MaxBatchBytes - batchBytes);
                if (info.Length > copyLimit) throw new InvalidOperationException("本批文件总大小超过 4 GB，请分批导入。");
                status("正在保存：" + info.Name);
                string destination = FileDestination(batch, i + 1, info.Name);
                long size;
                await using (var source = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                    size = await CopyWithLimitAsync(source, destination, copyLimit);
                if (size != info.Length) throw new IOException("文件长度校验失败。");
                batchBytes += size;
                batch.Files.Add(new ReceivedFile { Name = info.Name, SavedPath = destination, Size = size });
                Save(batch);
            }
            batch.State = "saved";
            Save(batch);
            await InspectBatchAsync(batch, status);
            return batch;
        }
        catch (Exception ex)
        {
            batch.State = "error"; batch.Error = ex.Message;
            try { Save(batch); } catch (Exception saveError) { Log("Failed to persist import error", saveError); }
            throw;
        }
    }

    private static async Task InspectBatchAsync(BatchRecord batch, Action<string> status)
    {
        foreach (var file in batch.Files)
        {
            status("原件已保存，正在准备分析文件…");
            try
            {
                await using (var stream = File.OpenRead(file.SavedPath))
                    file.Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                file.Inspection = await ArchiveInspector.InspectAsync(file.SavedPath);
            }
            catch (Exception ex) { file.Error = "原件已保留；生成分析文件未完成：" + ex.Message; Log("Inspection", ex); }
        }
        Save(batch);
    }
}
