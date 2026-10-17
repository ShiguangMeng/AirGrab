using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.UI.Dispatching;

namespace AirGrab.Core;

/// <summary>
/// 应用状态中枢：把手势、发现、传输三条链路汇聚成界面可直接绑定的单一状态源。
/// 所有集合变更与属性通知都在 UI 线程上发出，界面层不需要自行调度。
/// </summary>
public sealed class AppModel : INotifyPropertyChanged, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private bool _disposed;

    public GestureStateMachine Gesture { get; } = new();
    public PeerDiscovery Discovery { get; }
    public FileTransfer Transfer { get; } = new();
    public HandPoseDetector Detector { get; }

    public ObservableCollection<Peer> Peers { get; } = new();
    public ObservableCollection<Payload> Outbox { get; } = new();
    public ObservableCollection<Payload> Inbox { get; } = new();

    /// <summary>手势输入通道。两条通道写入同一套状态机与防误触闸门。</summary>
    public enum GestureChannel { Simulated, Camera }

    public GestureChannel Channel { get; private set; } = GestureChannel.Simulated;
    public string CameraStatus { get; private set; } = "摄像头通道未启动";
    public string CameraDiagnostics { get; private set; } = "—";

    /// <summary>
    /// 手势主控。
    ///
    /// 同一只手会被所有设备的摄像头同时看到，若每台设备都独立判定抓取，
    /// 就会出现「想从 A 发到 B，结果 B 也判定抓取并抢走这次操作」。
    /// 原版硬件方案靠 UWB 测距判断手离哪台设备更近，四端都没有这个能力，
    /// 因此改为显式指定：只有主控设备响应抓取，其余设备只作为接收方。
    /// </summary>
    public bool GestureMasterEnabled { get; private set; } = true;

    public void SetGestureMaster(bool enabled)
    {
        if (GestureMasterEnabled == enabled) return;

        GestureMasterEnabled = enabled;

        if (!enabled)
        {
            // 关掉主控就立刻放掉正在进行的抓取，避免半途卡在搬运态。
            Gesture.Cancel();
            Gesture.Finish();
        }

        StatusLine = enabled
            ? "手势主控已开启 · 本机可抓取并发送"
            : "手势主控已关闭 · 本机只接收，不响应抓取";

        Raise(nameof(GestureMasterEnabled));
        Raise(nameof(StatusLine));
        RaiseState();
    }

    public AppModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        Discovery = new PeerDiscovery
        {
            DisplayName = Environment.MachineName,
            Platform = "Windows"
        };

        Detector = new HandPoseDetector(dispatcher);

        SeedSampleContent();
        Wire();
    }

    /// <summary>
    /// 切换手势通道。切到摄像头时启动采帧与推理，切回模拟时立即停止 ——
    /// 摄像头是独占资源，闲置时不占用。
    /// </summary>
    public async Task SetChannelAsync(GestureChannel channel)
    {
        if (Channel == channel) return;

        Channel = channel;

        if (channel == GestureChannel.Camera)
        {
            bool ok = await Detector.StartAsync().ConfigureAwait(true);
            StatusLine = ok ? "摄像头手势通道已启动" : $"摄像头启动失败：{CameraStatus}";
        }
        else
        {
            Detector.Stop();
            Gesture.Finish();
            StatusLine = "已切换到模拟输入通道";
        }

        Raise(nameof(Channel));
        Raise(nameof(StatusLine));
        RaiseState();
    }

    // ---------------------------------------------------------------- 可绑定状态

    public string StatusLine { get; private set; } = "就绪";
    public string CarriedName => Gesture.PayloadName;
    public GestureState State => Gesture.State;
    public string StateTitle => Gesture.State.Title();
    public string StateCode => Gesture.State.Code();
    public string StateHint => Gesture.State.Hint();
    public int StateStep => Gesture.State.Step();
    public bool IsServing => Transfer.IsServing;
    public string TransferLog => Transfer.LastLog;
    public double TransferProgress { get; private set; }
    public int PeerCount => Peers.Count;

    /// <summary>
    /// 本机回环目标。局域网内没有其他设备时，界面把内容投送给它 ——
    /// 内容会走完整的落地流程（文本进剪贴板、图片落图片库、文件落下载目录），
    /// 因此单机也能完整验证「抓取 → 搬运 → 投送 → 落地」全链路。
    /// </summary>
    public Peer LoopbackPeer { get; } = new(
        "loopback",
        "本机（回环）",
        "Windows",
        System.Net.IPAddress.Loopback,
        DateTimeOffset.UtcNow);

    /// <summary>投送目标：优先真实设备，没有则回落到本机回环。</summary>
    public Peer ResolveTarget() => Peers.Count > 0 ? Peers[0] : LoopbackPeer;

    // ---------------------------------------------------------------- 手动设备

    /// <summary>自动发现到的设备快照，来自 PeerDiscovery。</summary>
    private IReadOnlyList<Peer> _discovered = Array.Empty<Peer>();

    /// <summary>
    /// 手动添加的设备。自动发现依赖 UDP 广播，跨网段、防火墙拦截、
    /// 或对端（iOS 真机发送广播需 Apple 特批授权）发不出信标时都收不到，
    /// 手动填写 IP 是绕过全部发现环节的兜底路径。
    /// </summary>
    private readonly Dictionary<string, Peer> _manualPeers = new(StringComparer.OrdinalIgnoreCase);

    public int ManualPeerCount => _manualPeers.Count;

    /// <summary>
    /// 手动添加设备。接受 <c>192.168.1.20</c> 或 <c>192.168.1.20:25586</c> 两种写法。
    /// </summary>
    public bool AddManualPeer(string hostOrIp)
    {
        string input = (hostOrIp ?? string.Empty).Trim();
        if (input.Length == 0)
        {
            StatusLine = "请输入对方设备的 IP 地址";
            Raise(nameof(StatusLine));
            return false;
        }

        string host = input;
        int port = FileTransfer.TcpPort;
        int colon = input.LastIndexOf(':');
        if (colon > 0 && int.TryParse(input[(colon + 1)..], out int parsedPort))
        {
            host = input[..colon];
            port = parsedPort;
        }

        if (!System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? address))
        {
            StatusLine = "IP 地址格式不正确，示例：192.168.1.20";
            Raise(nameof(StatusLine));
            return false;
        }

        string id = $"manual:{address}";
        _manualPeers[id] = new Peer(
            id,
            $"手动 · {address}",
            "手动",
            address,
            DateTimeOffset.UtcNow);

        MergePeers();
        StatusLine = $"已手动添加设备 {address}:{port}";
        Raise(nameof(StatusLine));
        return true;
    }

    public void ClearManualPeers()
    {
        if (_manualPeers.Count == 0) return;
        _manualPeers.Clear();
        MergePeers();
        StatusLine = "已清除手动添加的设备";
        Raise(nameof(StatusLine));
    }

    /// <summary>手动设备在前、自动发现设备在后，合并进界面绑定的集合。</summary>
    private void MergePeers()
    {
        Peers.Clear();
        foreach (Peer peer in _manualPeers.Values) Peers.Add(peer);
        foreach (Peer peer in _discovered) Peers.Add(peer);

        Raise(nameof(PeerCount));
        Raise(nameof(ManualPeerCount));
    }

    // ---------------------------------------------------------------- 启动

    public void Start()
    {
        Discovery.Start();
        Transfer.StartServer();
        StatusLine = "正在局域网内发现设备…";
        RaiseAll();
    }

    private void Wire()
    {
        Discovery.PeersChanged += snapshot => _dispatcher.TryEnqueue(() =>
        {
            _discovered = snapshot;
            MergePeers();
        });

        Gesture.StateChanged += _ => _dispatcher.TryEnqueue(() => RaiseState());
        Gesture.GrabConfirmed += _ => _dispatcher.TryEnqueue(() =>
        {
            StatusLine = "抓取已确认，移向目标设备";
            Raise(nameof(StatusLine));
        });
        Gesture.DropConfirmed += _ => _dispatcher.TryEnqueue(() =>
        {
            StatusLine = "松开手掌，投递中…";
            Raise(nameof(StatusLine));
        });
        Gesture.Cancelled += () => _dispatcher.TryEnqueue(() =>
        {
            StatusLine = "本次抓取已取消";
            Raise(nameof(StatusLine));
        });

        Transfer.PayloadReceived += payload => _dispatcher.TryEnqueue(async () =>
        {
            Inbox.Insert(0, payload);
            string? path = await LandPayloadAsync(payload);
            StatusLine = path is null
                ? $"已接收 {payload.Name}（{payload.SizeText}）→ {payload.Kind.Landing()}"
                : $"已接收 {payload.Name}（{payload.SizeText}）→ {path}";
            Raise(nameof(StatusLine));
        });

        Transfer.PayloadSent += (payload, peerName) => _dispatcher.TryEnqueue(() =>
        {
            Outbox.Insert(0, payload);
            StatusLine = $"已投送 {payload.Name} → {peerName}";
            Gesture.Finish();
            Raise(nameof(StatusLine));
        });

        Transfer.LogChanged += _ => _dispatcher.TryEnqueue(() =>
        {
            Raise(nameof(TransferLog));
            Raise(nameof(IsServing));
        });

        Transfer.ProgressChanged += value => _dispatcher.TryEnqueue(() =>
        {
            TransferProgress = value;
            Raise(nameof(TransferProgress));
        });

        // 摄像头通道：推理出的手部姿态直接喂给状态机，与模拟通道等价。
        Detector.PoseReady += pose =>
        {
            if (Channel != GestureChannel.Camera) return;
            // 非主控设备完全不响应抓取 —— 否则同一只手会让所有设备同时抓取。
            if (!GestureMasterEnabled) return;
            Gesture.Ingest(pose, DateTimeOffset.UtcNow);
        };

        Detector.StatusChanged += text => _dispatcher.TryEnqueue(() =>
        {
            CameraStatus = text;
            Raise(nameof(CameraStatus));
        });

        Detector.DiagnosticsChanged += text => _dispatcher.TryEnqueue(() =>
        {
            CameraDiagnostics = text;
            Raise(nameof(CameraDiagnostics));
        });
    }

    // ---------------------------------------------------------------- 操作

    /// <summary>把拖拽会话映射成与摄像头通道完全一致的状态机输入。</summary>
    public void BeginSimulatedDrag(Payload payload)
    {
        if (Channel != GestureChannel.Simulated)
        {
            StatusLine = "当前是摄像头通道 —— 请用手势抓取，或先切回模拟输入";
            Raise(nameof(StatusLine));
            return;
        }

        Gesture.BeginCarrying(payload.Name);
        StatusLine = $"搬运 {payload.Name}，移向目标设备";
        RaiseState();
        Raise(nameof(StatusLine));
    }

    public void EndSimulatedDrag(Peer? target, Payload payload)
    {
        Gesture.ConfirmDrop();
        _ = SendAsync(payload, target);
        Gesture.Finish();
        RaiseState();
    }

    public void CancelSimulatedDrag()
    {
        Gesture.Cancel();
        Gesture.Finish();
        StatusLine = "已取消本次抓取";
        RaiseState();
        Raise(nameof(StatusLine));
    }

    public async Task<bool> SendAsync(Payload payload, Peer? target)
    {
        target ??= ResolveTarget();

        if (target.Id == LoopbackPeer.Id)
        {
            string? path = await LandPayloadAsync(payload).ConfigureAwait(true);
            Inbox.Insert(0, payload);
            StatusLine = path is null
                ? $"已投送至本机回环 · {payload.Name} → {payload.Kind.Landing()}"
                : $"已投送至本机回环 · {payload.Name} → {path}";
            Gesture.Finish();
            RaiseAll();
            return true;
        }

        bool ok = await Transfer.SendAsync(payload, target).ConfigureAwait(true);
        Raise(nameof(TransferLog));
        return ok;
    }

    /// <summary>
    /// 接收端落地：文本与链接进剪贴板，图片落图片库下的 AirGrab 目录，
    /// 其余落下载目录下的 AirGrab 目录。返回实际落盘路径；进剪贴板时返回 null。
    /// </summary>
    private async Task<string?> LandPayloadAsync(Payload payload)
    {
        try
        {
            switch (payload.Kind)
            {
                case PayloadKind.Text:
                case PayloadKind.Link:
                {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(Encoding.UTF8.GetString(payload.Body));
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                    return null;
                }

                default:
                {
                    string root = payload.Kind == PayloadKind.Image
                        ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                        : Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Downloads");

                    string directory = Path.Combine(root, "AirGrab");
                    Directory.CreateDirectory(directory);

                    string safeName = string.Join("_", payload.Name.Split(Path.GetInvalidFileNameChars()));
                    string fullPath = Path.Combine(directory, safeName);

                    await File.WriteAllBytesAsync(fullPath, payload.Body).ConfigureAwait(true);
                    return fullPath;
                }
            }
        }
        catch (Exception ex)
        {
            StatusLine = $"落地失败：{ex.Message}";
            Raise(nameof(StatusLine));
            return null;
        }
    }

    /// <summary>
    /// 从剪贴板抓取内容。抓取优先级：图片 → 文件 → 文本 → 链接。
    /// 图片与文件优先于文本，因为剪贴板里同时存在多种格式时，
    /// 富内容通常是用户真正的抓取意图（例如复制的截图往往还带着来源页面的文本）。
    /// </summary>
    public async Task<bool> CaptureFromClipboardAsync()
    {
        try
        {
            var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();

            if (view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            {
                var reference = await view.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                using var ms = new MemoryStream();
                using (var netStream = stream.AsStreamForRead())
                {
                    await netStream.CopyToAsync(ms).ConfigureAwait(true);
                }

                byte[] imageBytes = ms.ToArray();
                if (imageBytes.Length > 0)
                {
                    Payload image = Payload.FromImage(ClipboardImageName(), imageBytes);
                    Outbox.Insert(0, image);
                    StatusLine = $"已从剪贴板抓取图片：{image.Name}（{image.SizeText}）";
                    Raise(nameof(StatusLine));
                    return true;
                }
            }

            if (view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await view.GetStorageItemsAsync();
                var file = items.OfType<Windows.Storage.StorageFile>().FirstOrDefault();
                if (file is not null)
                {
                    byte[] bytes = File.ReadAllBytes(file.Path);
                    Payload payload = Payload.FromFile(file.Name, bytes);
                    Outbox.Insert(0, payload);
                    StatusLine = $"已从剪贴板抓取文件：{payload.Name}（{payload.SizeText}）";
                    Raise(nameof(StatusLine));
                    return true;
                }
            }

            if (view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                string text = await view.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    bool looksLikeLink =
                        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

                    Payload payload = looksLikeLink
                        ? Payload.FromLink(text.Trim())
                        : Payload.FromText(text.Trim());

                    Outbox.Insert(0, payload);
                    StatusLine = $"已从剪贴板抓取 {payload.Kind.Title()}：{payload.Name}";
                    Raise(nameof(StatusLine));
                    return true;
                }
            }

            StatusLine = "剪贴板中没有可抓取的内容（支持图片、文件、文本、链接）";
            Raise(nameof(StatusLine));
            return false;
        }
        catch (Exception ex)
        {
            StatusLine = $"读取剪贴板失败：{ex.Message}";
            Raise(nameof(StatusLine));
            return false;
        }
    }

    private static string ClipboardImageName()
        => $"剪贴板图片-{DateTime.Now:HHmmss}.png";

    public Payload? FirstOutboxItem => Outbox.Count > 0 ? Outbox[0] : null;

    // ---------------------------------------------------------------- 采样内容

    private void SeedSampleContent()
    {
        Outbox.Add(new Payload
        {
            Kind = PayloadKind.Image,
            Name = "产品渲染图.png",
            ByteCount = 2_400_000,
            Body = new byte[2_400_000]
        });
        Outbox.Add(new Payload
        {
            Kind = PayloadKind.File,
            Name = "Q3 财报.pdf",
            ByteCount = 865_000,
            Body = new byte[865_000]
        });
        Outbox.Add(Payload.FromText("会议纪要：隔空手势抓取投送评审要点"));
        Outbox.Add(new Payload
        {
            Kind = PayloadKind.File,
            Name = "演示视频.mp4",
            ByteCount = 18_700_000,
            Body = new byte[18_700_000]
        });
    }

    // ---------------------------------------------------------------- 通知

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseState()
    {
        Raise(nameof(State));
        Raise(nameof(StateTitle));
        Raise(nameof(StateCode));
        Raise(nameof(StateHint));
        Raise(nameof(StateStep));
        Raise(nameof(CarriedName));
    }

    private void RaiseAll()
    {
        RaiseState();
        Raise(nameof(StatusLine));
        Raise(nameof(IsServing));
        Raise(nameof(TransferLog));
        Raise(nameof(PeerCount));
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detector.Dispose();
        Discovery.Dispose();
        Transfer.Dispose();
    }
}
