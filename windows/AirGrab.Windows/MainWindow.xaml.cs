using AirGrab.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinRT.Interop;

namespace AirGrab;

public sealed partial class MainWindow : Window
{
    public AppModel Model { get; }

    private Payload? _dragPayload;
    private string _currentTag = "send";

    public MainWindow()
    {
        Model = new AppModel(DispatcherQueue);

        InitializeComponent();

        Title = "AirGrab";
        ConfigureBackdrop();
        ConfigureTitleBar();

        Model.Start();
        ApplyNavState();
        ShowPage(_currentTag);
        SyncUi();

        // 设备数、收发记录变化时同步各页面的动态文案
        Model.PropertyChanged += (_, _) => SyncUi();

        // 摄像头预览帧：由检测器在 UI 线程派发，这里直接换成可绑定的位图源。
        // 订阅回调是 async void —— 异常必须在这里就地捕获，否则会逃逸到线程池。
        Model.Detector.PreviewFrame += async bitmap =>
        {
            try
            {
                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(bitmap);
                CameraPreviewImage.Source = source;
            }
            catch
            {
                // 预览渲染失败时保留上一帧，不打断手势识别。
            }
            finally
            {
                bitmap.Dispose();
            }
        };

        UpdateChannelUi();

        ConfigureTray();

        Closed += (_, _) =>
        {
            _tray?.Dispose();
            Model.Dispose();
        };
    }

    private TrayIcon? _tray;
    private bool _allowClose;

    /// <summary>
    /// 托盘常驻：点关闭按钮不退出进程，只把窗口藏进通知区域。
    ///
    /// 设备发现、接收服务、摄像头采集都挂在进程上 —— 一旦关闭就退出，
    /// 一次误点就会让整条投送链路直接断掉。因此把「关闭」重新定义为「最小化到托盘」，
    /// 真正的退出收进托盘右键菜单。
    /// </summary>
    private void ConfigureTray()
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(this);

        _tray = new TrayIcon(hwnd);
        _tray.Install("AirGrab · 隔空手势抓取投送");

        _tray.ShowRequested += () => _tray?.SetTooltip("AirGrab · 隔空手势抓取投送");

        _tray.QuitRequested += () =>
        {
            _allowClose = true;
            _tray?.Dispose();
            Close();
        };

        AppWindow.Closing += (_, args) =>
        {
            if (_allowClose) return;

            args.Cancel = true;
            _tray?.HideToTray();
            _tray?.SetTooltip("AirGrab · 后台运行中（点击恢复窗口）");
        };
    }

    /// <summary>Mica：让自绘标题栏与内容区共享同一层桌面材质。</summary>
    private void ConfigureBackdrop()
    {
        SystemBackdrop = new MicaBackdrop();
    }

    /// <summary>
    /// 标题栏整体交给 XAML 自绘：内容区延伸到标题栏，
    /// 系统按钮保留原生的悬停 / 按下反馈，仅把底色改为透明以融入 Mica。
    /// </summary>
    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(20, 0, 0, 0);
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(38, 0, 0, 0);

        AppWindow.Resize(new SizeInt32(1180, 780));
    }

    // ---------------------------------------------------------------- 导航

    private void OnNavTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        _currentTag = element.Tag?.ToString() ?? "send";
        ApplyNavState();
        ShowPage(_currentTag);
        SyncUi();
    }

    /// <summary>切换页面可见性 —— 四个页面各自持有独立内容，不存在「只改标题」的假切换。</summary>
    private void ShowPage(string tag)
    {
        PageSend.Visibility = tag == "send" ? Visibility.Visible : Visibility.Collapsed;
        PageDevices.Visibility = tag == "devices" ? Visibility.Visible : Visibility.Collapsed;
        PageHistory.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        TitleBarSubtitle.Text = tag switch
        {
            "devices" => "设备",
            "history" => "记录",
            "settings" => "设置",
            _ => "投送"
        };
    }

    private void ApplyNavState()
    {
        foreach (Border item in new[] { NavSend, NavDevices, NavHistory, NavSettings })
        {
            bool active = string.Equals(item.Tag?.ToString(), _currentTag, StringComparison.Ordinal);

            item.Background = active
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x00, 0x67, 0xC0))
                : new SolidColorBrush(Colors.Transparent);

            if (item.Child is TextBlock label)
            {
                label.Foreground = active
                    ? new SolidColorBrush(Colors.White)
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2B, 0x2B, 0x2B));
            }
        }
    }

    /// <summary>把模型状态映射到各页面的动态文案。</summary>
    private void SyncUi()
    {
        SendSubtitle.Text = Model.PeerCount > 0
            ? $"{Model.PeerCount} 台设备在附近 · 松手投送至最近设备"
            : "附近暂无设备 · 松手投送至本机回环，内容会真实落地";

        DevicesSubtitle.Text = Model.ManualPeerCount > 0
            ? $"{Model.PeerCount} 台设备（{Model.ManualPeerCount} 台手动） · UDP :25585 广播发现"
            : $"{Model.PeerCount} 台局域网设备 · UDP :25585 广播发现";

        HistorySubtitle.Text = $"已接收 {Model.Inbox.Count} 项 · 已投送 {Model.Outbox.Count} 项";
        SettingsSubtitle.Text = $"接收服务 :{FileTransfer.TcpPort} · 发现端口 :25585";

        // 没有设备时才显示空状态占位，避免列表为空时页面看起来像坏掉了。
        DevicesEmpty.Visibility = Model.PeerCount == 0 ? Visibility.Visible : Visibility.Collapsed;

        ManualHint.Text = Model.ManualPeerCount > 0
            ? $"已手动添加 {Model.ManualPeerCount} 台设备，投送时优先使用。"
            : "手动添加的设备不受广播与超时淘汰影响，长期保留。";

        CameraStatusText.Text = Model.CameraStatus;
        CameraDiagnosticsText.Text = Model.CameraDiagnostics;

        ServiceLine.Text =
            $"接收服务：{(Model.IsServing ? "已监听 :25586" : "未启动")}\n" +
            $"状态：{Model.StatusLine}\n" +
            $"日志：{Model.TransferLog}";
    }

    private void OnAddManualPeerClicked(object sender, RoutedEventArgs e)
    {
        if (Model.AddManualPeer(ManualHostBox.Text))
        {
            ManualHostBox.Text = string.Empty;
        }
        SyncUi();
    }

    private void OnGestureMasterToggled(object sender, RoutedEventArgs e)
    {
        Model.SetGestureMaster(GestureMasterSwitch.IsOn);
        SyncUi();
    }

    private void OnClearManualPeersClicked(object sender, RoutedEventArgs e)
    {
        Model.ClearManualPeers();
        SyncUi();
    }

    // ---------------------------------------------------------------- 操作

    private async void OnCaptureClicked(object sender, RoutedEventArgs e)
    {
        await Model.CaptureFromClipboardAsync();
        SyncUi();
    }

    private async void OnSendFirstClicked(object sender, RoutedEventArgs e)
    {
        Payload? payload = Model.FirstOutboxItem;
        if (payload is null) return;

        await Model.SendAsync(payload, Model.ResolveTarget());
        SyncUi();
    }

    private void OnClearHistoryClicked(object sender, RoutedEventArgs e)
    {
        Model.Inbox.Clear();
        SyncUi();
    }

    private async void OnChannelSimClicked(object sender, RoutedEventArgs e)
    {
        await Model.SetChannelAsync(AppModel.GestureChannel.Simulated);
        UpdateChannelUi();
        SyncUi();
    }

    private async void OnChannelCamClicked(object sender, RoutedEventArgs e)
    {
        await Model.SetChannelAsync(AppModel.GestureChannel.Camera);
        UpdateChannelUi();
        SyncUi();
    }

    /// <summary>通道相关的界面状态：预览面板可见性与说明文案。</summary>
    private void UpdateChannelUi()
    {
        bool camera = Model.Channel == AppModel.GestureChannel.Camera;

        CameraPanel.Visibility = camera ? Visibility.Visible : Visibility.Collapsed;

        ChannelDetail.Text = camera
            ? "当前通道：摄像头识别。前置摄像头采帧 → palm_detection 在整帧定位手掌 → " +
              "handpose 出 21 关键点 → 张开度与掌心位置喂给同一套状态机。"
            : "当前通道：模拟输入。按住内容卡片即握拳抓取，移动为搬运，松手为张开投送。" +
              "该通道与摄像头通道共用同一套状态机与四道防误触闸门。";
    }

    // ---------------------------------------------------------------- 抓取投送

    private void OnPayloadPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (element.DataContext is not Payload payload) return;
        if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;

        _dragPayload = payload;

        var position = e.GetCurrentPoint(RootGrid).Position;
        DragGhostName.Text = payload.Name;
        DragGhostHint.Text = Model.PeerCount > 0 ? "松开投送至最近的设备" : "松开投送至本机回环";
        DragGhost.Visibility = Visibility.Visible;
        MoveGhost(position.X, position.Y);

        Model.BeginSimulatedDrag(payload);
        SyncUi();

        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPayloadPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPayload is null) return;
        var position = e.GetCurrentPoint(RootGrid).Position;
        MoveGhost(position.X, position.Y);
    }

    private async void OnPayloadPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPayload is null) return;

        if (sender is UIElement element && element.PointerCaptures.Count > 0)
        {
            element.ReleasePointerCapture(e.Pointer);
        }

        DragGhost.Visibility = Visibility.Collapsed;

        Payload payload = _dragPayload;
        _dragPayload = null;

        await Model.SendAsync(payload, Model.ResolveTarget());
        SyncUi();

        e.Handled = true;
    }

    private void MoveGhost(double x, double y)
    {
        Canvas.SetLeft(DragGhost, x + 14);
        Canvas.SetTop(DragGhost, y + 14);
    }
}
