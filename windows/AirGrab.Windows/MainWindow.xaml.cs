using AirGrab.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

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
        ApplyNavState();
        UpdateHeader(_currentTag);

        Model.Start();

        Closed += (_, _) => Model.Dispose();
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
        UpdateHeader(_currentTag);
        ApplyNavState();
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

    private void UpdateHeader(string tag)
    {
        switch (tag)
        {
            case "devices":
                PageTitle.Text = "设备";
                PageSubtitle.Text = $"{Model.PeerCount} 台设备在线 · UDP :25585 广播发现";
                TitleBarSubtitle.Text = "设备";
                break;
            case "history":
                PageTitle.Text = "记录";
                PageSubtitle.Text = $"已接收 {Model.Inbox.Count} 项 · 已投送 {Model.Outbox.Count} 项";
                TitleBarSubtitle.Text = "记录";
                break;
            case "settings":
                PageTitle.Text = "设置";
                PageSubtitle.Text = $"手势通道：模拟输入 · 接收服务 :{FileTransfer.TcpPort}";
                TitleBarSubtitle.Text = "设置";
                break;
            default:
                PageTitle.Text = "投送";
                PageSubtitle.Text = $"{Model.PeerCount} 台设备在附近 · 隔空手势已开启";
                TitleBarSubtitle.Text = "投送";
                break;
        }
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
        DragGhostHint.Text = Model.Peers.Count > 0 ? "松开投送至最近的设备" : "附近暂无设备";
        DragGhost.Visibility = Visibility.Visible;
        MoveGhost(position.X, position.Y);

        Model.BeginSimulatedDrag(payload);

        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPayloadPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPayload is null) return;
        var position = e.GetCurrentPoint(RootGrid).Position;
        MoveGhost(position.X, position.Y);
    }

    private void OnPayloadPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPayload is null) return;

        if (sender is UIElement element && element.PointerCaptures.Count > 0)
        {
            element.ReleasePointerCapture(e.Pointer);
        }

        DragGhost.Visibility = Visibility.Collapsed;

        Peer? target = Model.Peers.Count > 0 ? Model.Peers[0] : null;
        Model.EndSimulatedDrag(target, _dragPayload);
        UpdateHeader(_currentTag);

        _dragPayload = null;
        e.Handled = true;
    }

    private void MoveGhost(double x, double y)
    {
        Canvas.SetLeft(DragGhost, x + 14);
        Canvas.SetTop(DragGhost, y + 14);
    }
}
