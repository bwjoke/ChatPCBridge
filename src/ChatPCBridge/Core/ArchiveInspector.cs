using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatPCBridge.Core;

public sealed record ArchiveInspection(
    bool IsZip,
    int EntryCount,
    string Summary,
    IReadOnlyList<string> TextFiles,
    IReadOnlyList<string> Warnings);

/// <summary>Reads a saved archive without changing it or extracting archive paths.</summary>
public static class ArchiveInspector
{
    private const int MaxEntries = 2_000;
    private const long MaxArchiveBytes = 1024L * 1024 * 1024;
    private const int MaxTextBytes = 16 * 1024 * 1024;
    private const long MaxTotalTextBytes = 32L * 1024 * 1024;
    private const long MaxCompressedTextBytes = 32L * 1024 * 1024;
    private const long MaxTotalCompressedTextBytes = 64L * 1024 * 1024;
    private const int MaxTextFiles = 8;
    private const int MaxWarnings = 24;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, true, true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(true, true, true);
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static async Task<ArchiveInspection> InspectAsync(
        string savedFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savedFilePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(savedFilePath), ".zip", StringComparison.OrdinalIgnoreCase))
            return new(false, 0, "文件已保存，可直接选择原文件上传。", Array.Empty<string>(), Array.Empty<string>());

        var textFiles = new List<string>();
        var warnings = new List<string>();
        var warningCount = 0;
        var entryCount = 0;
        var archiveOpened = false;
        var fileCount = 0;
        var imageCount = 0;
        var videoCount = 0;
        var textCandidateCount = 0;
        long totalBytesRead = 0;
        long totalCompressedBytes = 0;
        long totalOutputBytes = 0;
        string? outputDirectory = null;

        void Warn(string message)
        {
            warningCount++;
            if (warnings.Count < MaxWarnings)
                warnings.Add(message);
        }

        try
        {
            await using var file = new FileStream(savedFilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (file.Length > MaxArchiveBytes)
            {
                Warn("原文件超过 1 GB，已跳过聊天文本整理；仍可使用原文件。");
                return Result(false, "原始文件已保留，文件较大，暂未检查内容。");
            }

            // Bound central-directory parsing before ZipArchive allocates entry metadata.
            // A typical exported chat archive is a small, single-volume ZIP.
            var directoryCheck = await CheckDirectoryBudgetAsync(file, cancellationToken).ConfigureAwait(false);
            if (directoryCheck.Warning is not null)
            {
                Warn(directoryCheck.Warning);
                entryCount = directoryCheck.EntryCount;
                return Result(false, "原始 ZIP 已保留，暂未检查全部内容。");
            }

            file.Position = 0;
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            entryCount = archive.Entries.Count;
            archiveOpened = true;
            if (entryCount > MaxEntries)
            {
                Warn($"文件数量超过 {MaxEntries:N0} 项，已跳过聊天文本整理。");
                return Result(true, "ZIP 已保留，文件数量较多，暂未准备聊天文本。");
            }

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    continue;
                fileCount++;
                var extension = Path.GetExtension(entry.FullName).ToLowerInvariant();
                if (extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".heic")
                    imageCount++;
                if (extension is ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" or ".m4v")
                    videoCount++;
                if (extension != ".txt")
                    continue;
                textCandidateCount++;

                if (IsLink(entry))
                {
                    Warn($"“{DisplayName(entry.FullName)}”不是普通文本文件，已跳过。");
                    continue;
                }
                if (textFiles.Count >= MaxTextFiles)
                    continue;
                if (entry.Length > MaxTextBytes)
                {
                    Warn($"“{DisplayName(entry.FullName)}”超过 16 MB，已保留在原 ZIP 中。");
                    continue;
                }
                if (entry.CompressedLength > MaxCompressedTextBytes ||
                    entry.CompressedLength > MaxTotalCompressedTextBytes - totalCompressedBytes)
                {
                    Warn($"“{DisplayName(entry.FullName)}”超出本次读取容量，已保留在原 ZIP 中。");
                    continue;
                }
                var remainingBudget = MaxTotalTextBytes - totalBytesRead;
                if (remainingBudget <= 1 || entry.Length >= remainingBudget)
                {
                    Warn($"“{DisplayName(entry.FullName)}”超出本次文本整理容量，已保留在原 ZIP 中。");
                    continue;
                }

                string? pendingPath = null;
                try
                {
                    // Count the compressed range even when decoding throws before yielding
                    // output, so repeated corrupt entries cannot bypass the read budget.
                    totalCompressedBytes += entry.CompressedLength;
                    await using var entryStream = entry.Open();
                    var read = await ReadBoundedAsync(entryStream,
                        (int)Math.Min(MaxTextBytes, remainingBudget - 1),
                        count => totalBytesRead += count, cancellationToken).ConfigureAwait(false);
                    if (read.Bytes is null)
                    {
                        Warn($"“{DisplayName(entry.FullName)}”内容过大，已停止读取并保留原 ZIP。");
                        continue;
                    }
                    if (read.Bytes.LongLength != entry.Length || ComputeCrc32(read.Bytes) != entry.Crc32)
                    {
                        Warn($"“{DisplayName(entry.FullName)}”内容不完整，已保留原 ZIP 供检查。");
                        continue;
                    }
                    var text = DecodeText(read.Bytes);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        Warn($"“{DisplayName(entry.FullName)}”没有可用文字。");
                        continue;
                    }
                    if (text.Contains('\0'))
                    {
                        Warn($"“{DisplayName(entry.FullName)}”不是可识别的聊天文本，已保留在原 ZIP 中。");
                        continue;
                    }
                    var outputBytes = StrictUtf8.GetByteCount(text);
                    if (outputBytes > MaxTextBytes || outputBytes > MaxTotalTextBytes - totalOutputBytes)
                    {
                        Warn($"“{DisplayName(entry.FullName)}”转换后的文本过大，已保留在原 ZIP 中。");
                        continue;
                    }

                    // Generated names only: entry.FullName is never used as a filesystem path.
                    outputDirectory ??= CreateOutputDirectory(savedFilePath);
                    var outputPath = Path.Combine(outputDirectory, $"chat-{textFiles.Count + 1:000}.txt");
                    pendingPath = outputPath + ".partial";
                    await File.WriteAllTextAsync(pendingPath, text, new UTF8Encoding(false), cancellationToken)
                        .ConfigureAwait(false);
                    File.Move(pendingPath, outputPath);
                    pendingPath = null;
                    textFiles.Add(Path.GetFullPath(outputPath));
                    totalOutputBytes += outputBytes;
                }
                catch (DecoderFallbackException)
                {
                    Warn($"“{DisplayName(entry.FullName)}”的文字编码暂不支持，已保留在原 ZIP 中。");
                }
                catch (Exception exception) when (IsExpectedFailure(exception))
                {
                    Warn($"“{DisplayName(entry.FullName)}”未能整理完成，已保留在原 ZIP 中。");
                }
                finally
                {
                    if (pendingPath is not null)
                        TryDelete(pendingPath);
                }
            }

            if (textCandidateCount > MaxTextFiles && textFiles.Count == MaxTextFiles)
                Warn("最多准备 8 份聊天文本；其余内容仍完整保留在原 ZIP 中。");

            var summary = $"ZIP 包含 {fileCount} 个文件";
            if (imageCount > 0 || videoCount > 0)
                summary += $"（图片 {imageCount} 个、视频 {videoCount} 个）";
            summary += textFiles.Count > 0
                ? $"，已准备 {textFiles.Count} 份聊天文本供上传。"
                : textCandidateCount == 0 ? "，未找到 TXT 聊天文本。" : "，暂未准备可上传的聊天文本。";
            if (warningCount > 0)
                summary += "部分内容需要注意，请查看提示；原 ZIP 已保留。";
            return Result(true, summary);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Warn(archiveOpened ? "ZIP 的部分内容无法读取，请检查原文件。" : "无法打开 ZIP，请检查原文件是否完整。");
            return Result(archiveOpened, textFiles.Count > 0
                ? $"已准备 {textFiles.Count} 份聊天文本，但 ZIP 未能全部检查完成。"
                : "ZIP 未能检查完成；仍可使用保存的原文件。");
        }

        ArchiveInspection Result(bool isZip, string summary)
        {
            if (warningCount > warnings.Count)
                warnings.Add($"另外还有 {warningCount - warnings.Count} 项内容未能整理；请保留原 ZIP。");
            return new(isZip, entryCount, summary, textFiles.ToArray(), warnings.ToArray());
        }
    }

    private static string CreateOutputDirectory(string savedFilePath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(savedFilePath))!;
        var analysis = Path.Combine(parent, "analysis");
        foreach (var path in new[] { parent, analysis })
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked output directories are not supported.");
        }
        var directory = Path.Combine(analysis, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static bool IsLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0;

    private static bool IsExpectedFailure(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException;

    private static string DisplayName(string name) =>
        new(name.Where(character => !char.IsControl(character)).Take(100).ToArray());

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320U ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }

    private static uint ComputeCrc32(byte[] bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 4 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) ||
             (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)))
            throw new DecoderFallbackException("UTF-32 is not supported.");
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return StrictUtf16Le.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return StrictUtf16Be.GetString(bytes, 2, bytes.Length - 2);
        return StrictUtf8.GetString(bytes);
    }

    private static async Task<(byte[]? Bytes, int BytesRead)> ReadBoundedAsync(
        Stream stream, int limit, Action<int> onBytesRead, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var content = new MemoryStream();
        var bytesRead = 0;
        try
        {
            while (bytesRead <= limit)
            {
                var count = Math.Min(buffer.Length, limit + 1 - bytesRead);
                var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return (content.ToArray(), bytesRead);
                bytesRead += read;
                onBytesRead(read);
                if (bytesRead > limit)
                    return (null, bytesRead);
                content.Write(buffer, 0, read);
            }
            return (null, bytesRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<(int EntryCount, string? Warning)> CheckDirectoryBudgetAsync(
        FileStream file, CancellationToken cancellationToken)
    {
        const int maxTailBytes = ushort.MaxValue + 22;
        var tail = new byte[(int)Math.Min(file.Length, maxTailBytes)];
        file.Position = file.Length - tail.Length;
        await file.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
        for (var offset = tail.Length - 22; offset >= 0; offset--)
        {
            if (BitConverter.ToUInt32(tail, offset) != 0x06054B50 ||
                offset + 22 + BitConverter.ToUInt16(tail, offset + 20) != tail.Length)
                continue;
            var count = BitConverter.ToUInt16(tail, offset + 10);
            if (BitConverter.ToUInt16(tail, offset + 4) != 0 || BitConverter.ToUInt16(tail, offset + 6) != 0 ||
                BitConverter.ToUInt16(tail, offset + 8) != count)
                throw new InvalidDataException("Multi-volume archives are not supported.");
            var directoryBytes = BitConverter.ToUInt32(tail, offset + 12);
            var directoryOffset = BitConverter.ToUInt32(tail, offset + 16);
            if (count == ushort.MaxValue || directoryBytes == uint.MaxValue || directoryOffset == uint.MaxValue)
                return (0, "此 ZIP 使用了暂不支持的扩展格式，请直接使用原 ZIP。");
            if (count > MaxEntries)
                return (count, $"文件数量超过 {MaxEntries:N0} 项，已跳过聊天文本整理。");
            var actualDirectorySpan = file.Length - tail.Length + offset - directoryOffset;
            if (actualDirectorySpan < directoryBytes)
                throw new InvalidDataException("ZIP central directory bounds are invalid.");
            if (directoryBytes > 8 * 1024 * 1024 || actualDirectorySpan > 8 * 1024 * 1024)
                return (count, "ZIP 的文件目录较大，已跳过聊天文本整理，请使用原 ZIP。");
            // Validate the actual record count before ZipArchive allocates entry objects.
            // The EOCD count itself is untrusted and can understate hundreds of thousands
            // of tiny directory records packed into a modest central-directory byte span.
            var directory = new byte[directoryBytes];
            file.Position = directoryOffset;
            await file.ReadExactlyAsync(directory, cancellationToken).ConfigureAwait(false);
            var cursor = 0;
            var actualCount = 0;
            while (cursor < directory.Length)
            {
                if (directory.Length - cursor < 46 || BitConverter.ToUInt32(directory, cursor) != 0x02014B50)
                    throw new InvalidDataException("Invalid central directory record.");
                actualCount++;
                if (actualCount > MaxEntries)
                    return (actualCount, $"文件数量超过 {MaxEntries:N0} 项，已跳过聊天文本整理。");
                var recordBytes = 46 + BitConverter.ToUInt16(directory, cursor + 28) +
                    BitConverter.ToUInt16(directory, cursor + 30) + BitConverter.ToUInt16(directory, cursor + 32);
                if (recordBytes > directory.Length - cursor)
                    throw new InvalidDataException("Incomplete central directory record.");
                cursor += recordBytes;
            }
            if (actualCount != count) throw new InvalidDataException("ZIP entry count mismatch.");
            return (count, null);
        }
        throw new InvalidDataException("ZIP end directory was not found.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
