using System.IO;
using System.Windows;
using Windows.ApplicationModel.Activation;

namespace ChatPCBridge;

public partial class App : Application
{
    private InstanceCoordinator? _instances;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Directory.CreateDirectory(BridgeStore.Root);
        DispatcherUnhandledException += (_, args) =>
        {
            BridgeStore.Log("Unhandled", args.Exception);
            MessageBox.Show("操作未完成。原始文件不会被删除。\n" + args.Exception.Message, "ChatPCBridge");
            args.Handled = true;
        };
        IActivatedEventArgs? activation = null;
        try
        {
            activation = global::Windows.ApplicationModel.AppInstance.GetActivatedEventArgs();
            BridgeStore.Log("Activation: " + activation?.Kind);
        }
        catch (Exception ex) { BridgeStore.Log("普通桌面启动，未取得分享激活参数", ex); }
        try
        {
            _instances = new InstanceCoordinator();
            if (_instances.TryBecomePrimary())
            {
                var window = CreatePrimaryWindow();
                if (activation is ShareTargetActivatedEventArgs share)
                    await window.ReceiveShareAsync(share.ShareOperation);
                else if (e.Args.Length >= 2 && e.Args[0] == "--import")
                    await window.ImportPathsAsync(e.Args.Skip(1).ToArray(), false);
                return;
            }

            BatchRecord? received = null;
            bool handoff = false;
            // The activated process retains the live ShareOperation until the
            // archive is copied, persisted, and Windows is told it is complete.
            if (activation is ShareTargetActivatedEventArgs secondaryShare)
            {
                received = await BridgeStore.ReceiveAsync(secondaryShare.ShareOperation, _ => { });
                handoff = true;
            }
            else if (e.Args.Length >= 2 && e.Args[0] == "--import")
                received = await BridgeStore.ImportAsync(e.Args.Skip(1).ToArray(), _ => { });

            var notice = received is null ? new ActivationNotice("activate") : new ActivationNotice("batch-ready", received.Id, handoff);
            if (await _instances.NotifyAsync(notice)) { Shutdown(); return; }

            // If the main window closed while a large ZIP was copying, safely
            // take over only after obtaining the same primary mutex.
            if (_instances.TryBecomePrimary())
            {
                var window = CreatePrimaryWindow();
                if (received is not null) window.PresentBatch(received, handoff);
                return;
            }
            BridgeStore.Log("Primary notification unavailable; saved history remains on disk.");
            if (received is not null)
                MessageBox.Show("聊天文件已保存。主窗口正在忙，请在已有窗口左侧选择最新记录继续。", "ChatPCBridge");
            Shutdown();
        }
        catch (Exception ex)
        {
            BridgeStore.Log("Activation failed", ex);
            MessageBox.Show("接收或打开窗口未完成。已保存的原始文件会保留。\n" + ex.Message, "ChatPCBridge");
            if (MainWindow is null) Shutdown(1);
        }
    }

    private MainWindow CreatePrimaryWindow()
    {
        var window = new MainWindow();
        MainWindow = window;
        _instances!.StartServer(notice => Dispatcher.InvokeAsync(() =>
        {
            if (notice.Kind == "activate") { window.BringToFront(); return true; }
            var batch = BridgeStore.LoadBatch(notice.BatchId!);
            if (batch is null) return false;
            window.BringToFront();
            window.PresentBatch(batch, notice.Handoff);
            return true;
        }).Task, ex => BridgeStore.Log("Instance notification", ex));
        window.Closed += (_, _) => Shutdown();
        window.Show();
        return window;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instances?.Dispose();
        base.OnExit(e);
    }
}
