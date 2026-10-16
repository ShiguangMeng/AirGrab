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

    public ObservableCollection<Peer> Peers { get; } = new();
    public ObservableCollection<Payload> Outbox { get; } = new();
    public ObservableCollection<Payload> Inbox { get; } = new();

    public AppModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        Discovery = new PeerDiscovery
        {
            DisplayName = Environment.MachineName,
            Platform = "Windows"
        };

        SeedSampleContent();
        Wire();
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
            Peers.Clear();
            foreach (Peer peer in snapshot) Peers.Add(peer);
            Raise(nameof(PeerCount));
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
    }

    // ---------------------------------------------------------------- 操作

    /// <summary>把拖拽会话映射成与摄像头通道完全一致的状态机输入。</summary>
    public void BeginSimulatedDrag(Payload payload)
    {
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

    /// <summary>从剪贴板抓取内容，生成一条真实可投送的载荷。</summary>
    public async Task<bool> CaptureFromClipboardAsync()
    {
        try
        {
            var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();

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

            StatusLine = "剪贴板中没有可抓取的文本";
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
        Discovery.Dispose();
        Transfer.Dispose();
    }
}
