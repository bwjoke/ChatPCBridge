using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation.Metadata;
using Windows.Management.Deployment;
using Windows.Storage;

namespace ShareProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 4 || args[0] != "--file" || args[2] != "--output")
        {
            ShowUsage();
            return 2;
        }
        var discoverOnly = false;
        var inspectIcons = false;
        int? maxTargets = null;
        for (var index = 4; index < args.Length; index++)
        {
            if (args[index] == "--discover-only" && !discoverOnly)
            {
                discoverOnly = true;
            }
            else if (args[index] == "--inspect-icons" && !inspectIcons)
            {
                inspectIcons = true;
            }
            else if (args[index] == "--max-targets" && maxTargets is null && index + 1 < args.Length &&
                     int.TryParse(args[++index], out var value) && value >= 0)
            {
                maxTargets = value;
            }
            else
            {
                ShowUsage();
                return 2;
            }
        }
        if ((maxTargets is not null || inspectIcons) && !discoverOnly)
        {
            MessageBox.Show("--max-targets 和 --inspect-icons 仅用于 --discover-only 诊断模式。");
            return 2;
        }
        var input = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[3]);
        if (File.Exists(output) || !string.Equals(Path.GetExtension(output), ".json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("结果路径必须是尚不存在的独立 .json 文件，不能覆盖已有文件。");
            return 2;
        }
        var application = new Application();
        var window = new ProbeWindow(input, output, discoverOnly, maxTargets, inspectIcons);
        return application.Run(window);
    }

    private static void ShowUsage() => MessageBox.Show(
        "用法：ShareProbe --file <合成测试.zip> --output <结果.json> [--discover-only [--max-targets N] [--inspect-icons]]\n" +
        "只枚举模式省略 --max-targets 时保持系统默认；N 为非负整数，0 的含义由系统决定。");
}

internal sealed class ProbeWindow : Window
{
    private readonly string _filePath;
    private readonly string _outputPath;
    private readonly bool _discoverOnly;
    private readonly int? _maxTargets;
    private readonly bool _inspectIcons;
    private readonly TextBox _status;
    private readonly ProbeResult _result = new();
    private readonly Dictionary<string, TransferTarget> _targets = new(StringComparer.Ordinal);
    private readonly List<string> _targetOrder = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(40));
    private readonly TaskCompletionSource _enumerationComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TransferTargetWatcher? _watcher;
    private bool _finished;

    public ProbeWindow(string filePath, string outputPath, bool discoverOnly, int? maxTargets, bool inspectIcons)
    {
        _filePath = filePath;
        _outputPath = outputPath;
        _discoverOnly = discoverOnly;
        _maxTargets = maxTargets;
        _inspectIcons = inspectIcons;
        _result.DiscoverOnly = discoverOnly;
        _result.InspectIcons = inspectIcons;
        _result.RequestedMaxAppTargets = maxTargets;
        Title = discoverOnly ? "ChatPCBridge — 分享目标枚举诊断" : "ChatPCBridge — 系统分享测试";
        Width = 680;
        Height = 330;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _status = new TextBox { Margin = new Thickness(18), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 15 };
        Content = _status;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) =>
        {
            _timeout.Cancel();
            if (!_finished)
            {
                _result.Errors.Add("测试窗口被关闭，分享验证未完成。");
                _result.State = "closed";
                SaveResult();
            }
        };
    }

    private async Task RunAsync()
    {
        try
        {
            _result.InputFile = _filePath;
            _result.StartedUtc = DateTimeOffset.UtcNow;
            if (!File.Exists(_filePath) || !string.Equals(Path.GetExtension(_filePath), ".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("必须指定现有的合成测试 ZIP。");
            if (new FileInfo(_filePath).Length > 4 * 1024 * 1024)
                throw new InvalidOperationException("本工具仅接受 4 MB 以内的合成测试 ZIP。");
            _result.ApiPresent = ApiInformation.IsTypePresent("Windows.ApplicationModel.DataTransfer.TransferTargetWatcher");
            if (!_result.ApiPresent)
                throw new NotSupportedException("此系统未提供 TransferTargetWatcher。");

            var packageManager = new PackageManager();
            var installedPackages = packageManager.FindPackagesForUser("").ToArray();
            var ownPackages = installedPackages.Where(package => package.Id.Name == "ChatPCBridge.Local").ToArray();
            _result.ExpectedTargetLabel = "ChatPCBridge";
            if (ownPackages.Length != 1 && !_discoverOnly)
                throw new InvalidOperationException($"应存在一个 {_result.ExpectedTargetLabel}.Local 安装包，实际为 {ownPackages.Length}。");
            if (ownPackages.Length == 1)
            {
                var ownPackage = ownPackages[0];
                _result.ExpectedPackageName = ownPackage.Id.Name;
                _result.PackageFamilyName = ownPackage.Id.FamilyName;
                _result.AllowedAppId = ownPackage.Id.FamilyName + "!App";
            }

            var file = await StorageFile.GetFileFromPathAsync(_filePath).AsTask(_timeout.Token);
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.Properties.Title = "ChatPCBridge 合成聊天分享测试";
            data.Properties.Description = "本文件只包含合成测试内容。";
            data.SetStorageItems(new[] { file }, readOnly: true);
            _result.DataPackageSupported = TransferTargetWatcher.IsSupported(data.GetView());
            if (!_result.DataPackageSupported)
                throw new InvalidOperationException("系统不支持此测试文件的分享目标发现。");

            // The system filter permits only the installed self-built app. A second exact
            // target-label/ID guard immediately before transfer prevents fallback to any app.
            var discovery = new TransferTargetDiscoveryOptions(data.GetView());
            _result.DefaultMaxAppTargets = discovery.MaxAppTargets;
            if (_discoverOnly)
            {
                // Omission preserves the real constructor default. Explicit zero is passed
                // through unchanged; the public API documentation assigns it no special meaning.
                if (_maxTargets is not null)
                    discovery.MaxAppTargets = _maxTargets.Value;
            }
            else
            {
                discovery.MaxAppTargets = 20;
                discovery.AllowedTargetAppIds = new[] { _result.AllowedAppId };
            }
            _result.DiscoveryOptions = new DiscoveryOptionsResult(discovery.MaxAppTargets,
                discovery.AllowedTargetAppIds ?? Array.Empty<string>(), !_discoverOnly,
                data.GetView().AvailableFormats.ToArray(), data.RequestedOperation.ToString());
            _watcher = TransferTarget.CreateWatcher(discovery);
            _watcher.Added += (_, args) => Dispatcher.BeginInvoke(() => UpdateTarget(args.Target, "Added"));
            _watcher.Updated += (_, args) => Dispatcher.BeginInvoke(() => UpdateTarget(args.Target, "Updated"));
            _watcher.Removed += (_, args) => Dispatcher.BeginInvoke(() =>
            {
                _targets.Remove(args.Target.Id);
                _targetOrder.Remove(args.Target.Id);
                RecordEvent(args.Target, "Removed");
                UpdateSnapshot();
            });
            _watcher.EnumerationCompleted += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _result.EnumerationCompleted = true;
                _enumerationComplete.TrySetResult();
            });
            _watcher.Stopped += (_, _) => Dispatcher.BeginInvoke(() => _result.WatcherStopped = true);
            _result.State = "discovering";
            Log(_discoverOnly
                ? $"正在枚举全部匹配的 Windows 分享入口；系统默认上限 {_result.DefaultMaxAppTargets}，本次上限 {discovery.MaxAppTargets}。不会执行分享。"
                : $"正在通过 Windows 查找已安装的 {_result.ExpectedTargetLabel} 分享入口…");
            SaveResult();
            _watcher.Start();
            await _enumerationComplete.Task.WaitAsync(_timeout.Token);
            _result.BridgeDiscovered = _targets.Values.Any(IsOwnTarget);

            if (_discoverOnly)
            {
                if (_inspectIcons)
                {
                    _result.State = "inspecting-icons";
                    Log("正在读取每个目标的系统图标，并保存图像读取结果…");
                    await InspectIconsAsync();
                }
                _result.State = "discovery-completed";
                Log($"枚举完成，共 {_targets.Count} 个目标，{_result.ExpectedTargetLabel} {(_result.BridgeDiscovered ? "已出现" : "未出现")}。结果已保存。");
                return;
            }

            var candidates = _targets.Values.Where(IsOwnTarget).ToArray();
            if (candidates.Length != 1)
                throw new InvalidOperationException($"仅允许自建 {_result.ExpectedTargetLabel} 分享目标；找到 {candidates.Length} 个匹配目标。");
            var target = candidates[0];
            if (!target.IsEnabled)
                throw new InvalidOperationException($"{_result.ExpectedTargetLabel} 分享目标存在，但当前不可用。");

            var hwnd = new WindowInteropHelper(this).Handle;
            // Documented Windows SDK interop function, implemented in the Windows system
            // Windows.UI.dll. Do not assume WindowId.Value is numerically equal to HWND.
            Marshal.ThrowExceptionForHR(GetWindowIdFromWindow(hwnd, out var windowId));
            _result.WindowId = windowId.Value;
            _result.SelectedTargetId = target.Id;
            _result.State = "transferring";
            Log($"已发现 {target.Label}。正在分享合成 ZIP；等待接收程序确认完成…");
            SaveResult();
            if (!IsOwnTarget(target))
                throw new InvalidOperationException("分享目标验证失败。");
            _result.TransferAttempted = true;
            var transfer = await _watcher.TransferToAsync(target, windowId).AsTask(_timeout.Token);
            _result.TransferResult = new TransferResult(transfer.Succeeded,
                transfer.ExtendedError?.HResult, transfer.ExtendedError?.Message);
            _result.State = transfer.Succeeded ? "succeeded" : "failed";
            if (!transfer.Succeeded)
                _result.Errors.Add(transfer.ExtendedError?.ToString() ?? "系统报告分享失败，未提供详情。");
            Log(transfer.Succeeded ? "Windows 已报告分享完成。验证结果已保存，可以关闭此窗口。" : "Windows 报告分享失败，请检查结果 JSON。");
        }
        catch (OperationCanceledException)
        {
            _result.State = "timed-out";
            _result.Errors.Add("分享验证在 40 秒内未完成（或窗口被关闭）。");
            Log("测试未能在 40 秒内完成。结果已保存。");
        }
        catch (Exception exception)
        {
            _result.State = "failed";
            _result.Errors.Add(exception.ToString());
            Log("测试失败：" + exception.Message);
        }
        finally
        {
            try { _watcher?.Stop(); }
            catch (Exception exception) { _result.Errors.Add("停止发现失败：" + exception.Message); }
            _finished = true;
            _result.CompletedUtc = DateTimeOffset.UtcNow;
            SaveResult();
            if (_discoverOnly)
                Close();
        }
    }

    private bool IsOwnTarget(TransferTarget target) =>
        string.Equals(target.Label, _result.ExpectedTargetLabel, StringComparison.Ordinal) &&
        (string.Equals(target.Id, _result.AllowedAppId, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(target.Id, _result.PackageFamilyName, StringComparison.OrdinalIgnoreCase));

    private async Task InspectIconsAsync()
    {
        if (!_discoverOnly)
            throw new InvalidOperationException("图标检查只能用于只枚举模式。");
        const int maxIconBytes = 4 * 1024 * 1024;
        var targets = _targetOrder.Select(id => _targets[id]).ToArray();
        _result.IconDirectory = Path.Combine(Path.GetDirectoryName(_outputPath)!, "share-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_result.IconDirectory);
        for (var index = 0; index < targets.Length; index++)
        {
            _timeout.Token.ThrowIfCancellationRequested();
            var target = targets[index];
            var iconResult = new IconResult { Order = index + 1, Label = target.Label, Id = target.Id };
            _result.Icons.Add(iconResult);
            using var iconTimeout = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token);
            iconTimeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var reference = target.DisplayIcon;
                iconResult.ReferencePresent = reference is not null;
                if (reference is null)
                    throw new InvalidOperationException("系统未提供 DisplayIcon。");
                using var icon = await reference.OpenReadAsync().AsTask(iconTimeout.Token);
                iconResult.OpenSucceeded = true;
                iconResult.ContentType = icon.ContentType;
                iconResult.DeclaredSize = icon.Size;
                if (icon.Size > maxIconBytes)
                    throw new InvalidOperationException("图标超过诊断读取上限 4 MB。");
                using var input = icon.AsStreamForRead();
                using var memory = new MemoryStream();
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0,
                        (int)Math.Min(buffer.Length, maxIconBytes + 1 - memory.Length)), iconTimeout.Token);
                    if (read == 0)
                        break;
                    memory.Write(buffer, 0, read);
                    if (memory.Length > maxIconBytes)
                        throw new InvalidOperationException("实际图标内容超过诊断读取上限。");
                }
                var bytes = memory.ToArray();
                iconResult.ReadSucceeded = true;
                iconResult.ActualSize = bytes.Length;
                iconResult.SignatureHex = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 16)));
                iconResult.Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
                var isPng = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                var prefix = Path.Combine(_result.IconDirectory, $"target-{index + 1:000}");
                iconResult.RawPath = prefix + (isPng ? ".png" : ".source");
                await File.WriteAllBytesAsync(iconResult.RawPath, bytes, iconTimeout.Token);

                using var imageStream = new MemoryStream(bytes, writable: false);
                var decoder = BitmapDecoder.Create(imageStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                iconResult.Decoder = decoder.GetType().Name;
                iconResult.ContainerFormat = decoder.CodecInfo?.ContainerFormat.ToString();
                iconResult.FrameCount = decoder.Frames.Count;
                if (decoder.Frames.Count == 0)
                    throw new InvalidOperationException("图标没有可解码图像。");
                var frame = decoder.Frames[0];
                iconResult.PixelWidth = frame.PixelWidth;
                iconResult.PixelHeight = frame.PixelHeight;
                iconResult.PixelFormat = frame.Format.ToString();
                iconResult.DpiX = frame.DpiX;
                iconResult.DpiY = frame.DpiY;
                iconResult.DecodeSucceeded = true;
                if (frame.PixelWidth > 2048 || frame.PixelHeight > 2048)
                    throw new InvalidOperationException("图标尺寸超过诊断上限 2048 像素。");
                iconResult.PngPath = prefix + ".png";
                if (!isPng)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(frame));
                    using var output = new FileStream(iconResult.PngPath, FileMode.CreateNew, FileAccess.Write);
                    encoder.Save(output);
                }
                iconResult.Succeeded = true;
            }
            catch (OperationCanceledException) when (!_timeout.IsCancellationRequested)
            {
                iconResult.Error = "读取该图标在 4 秒内未完成。";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                iconResult.Error = exception.ToString();
                iconResult.HResult = exception.HResult;
            }
            finally { SaveResult(); }
        }
    }

    private void UpdateTarget(TransferTarget target, string eventKind)
    {
        if (!_targets.ContainsKey(target.Id))
            _targetOrder.Add(target.Id);
        _targets[target.Id] = target;
        RecordEvent(target, eventKind);
        UpdateSnapshot();
    }

    private void RecordEvent(TransferTarget target, string eventKind) =>
        _result.TargetEvents.Add(new TargetEventResult(_result.TargetEvents.Count + 1, eventKind,
            target.Label, target.Id, target.IsEnabled, DateTimeOffset.UtcNow));

    private void UpdateSnapshot()
    {
        _result.Targets = _targetOrder.Select((id, index) =>
        {
            var item = _targets[id];
            return new TargetResult(index + 1, item.Label, item.Id, item.IsEnabled);
        }).ToArray();
        SaveResult();
    }

    private void Log(string message) => _status.AppendText(message + Environment.NewLine + Environment.NewLine);

    private void SaveResult()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
            File.WriteAllText(_outputPath, JsonSerializer.Serialize(_result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) { Log("无法写入验证结果：" + exception.Message); }
    }

    [DllImport("Windows.UI.dll", ExactSpelling = true)]
    private static extern int GetWindowIdFromWindow(nint hwnd, out Windows.UI.WindowId windowId);
}

internal sealed class ProbeResult
{
    public string State { get; set; } = "starting";
    public bool DiscoverOnly { get; set; }
    public bool InspectIcons { get; set; }
    public int? RequestedMaxAppTargets { get; set; }
    public int? DefaultMaxAppTargets { get; set; }
    public DiscoveryOptionsResult? DiscoveryOptions { get; set; }
    public bool TransferAttempted { get; set; }
    public bool BridgeDiscovered { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string InputFile { get; set; } = "";
    public bool ApiPresent { get; set; }
    public bool DataPackageSupported { get; set; }
    public string PackageFamilyName { get; set; } = "";
    public string ExpectedPackageName { get; set; } = "";
    public string ExpectedTargetLabel { get; set; } = "";
    public string AllowedAppId { get; set; } = "";
    public bool EnumerationCompleted { get; set; }
    public bool WatcherStopped { get; set; }
    public ulong WindowId { get; set; }
    public string? SelectedTargetId { get; set; }
    public TargetResult[] Targets { get; set; } = Array.Empty<TargetResult>();
    public List<TargetEventResult> TargetEvents { get; } = new();
    public string? IconDirectory { get; set; }
    public List<IconResult> Icons { get; } = new();
    public TransferResult? TransferResult { get; set; }
    public List<string> Errors { get; } = new();
}

internal sealed record TargetResult(int Order, string Label, string Id, bool IsEnabled);
internal sealed record TargetEventResult(int Sequence, string Event, string Label, string Id, bool IsEnabled, DateTimeOffset ObservedUtc);
internal sealed record DiscoveryOptionsResult(int MaxAppTargets, string[] AllowedTargetAppIds, bool AllowedTargetAppIdsSet,
    string[] DataFormats, string RequestedOperation);
internal sealed record TransferResult(bool Succeeded, int? HResult, string? Error);

internal sealed class IconResult
{
    public int Order { get; set; }
    public string Label { get; set; } = "";
    public string Id { get; set; } = "";
    public bool ReferencePresent { get; set; }
    public bool OpenSucceeded { get; set; }
    public bool ReadSucceeded { get; set; }
    public bool DecodeSucceeded { get; set; }
    public bool Succeeded { get; set; }
    public string? ContentType { get; set; }
    public ulong DeclaredSize { get; set; }
    public int ActualSize { get; set; }
    public string? SignatureHex { get; set; }
    public string? Sha256 { get; set; }
    public string? Decoder { get; set; }
    public string? ContainerFormat { get; set; }
    public int FrameCount { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public string? PixelFormat { get; set; }
    public double DpiX { get; set; }
    public double DpiY { get; set; }
    public string? RawPath { get; set; }
    public string? PngPath { get; set; }
    public int? HResult { get; set; }
    public string? Error { get; set; }
}
