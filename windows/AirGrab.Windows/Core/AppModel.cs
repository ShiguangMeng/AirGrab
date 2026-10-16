using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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

        Transfer.PayloadReceived += payload => _dispatcher.TryEnqueue(() =>
        {
            Inbox.Insert(0, payload);
            StatusLine = $"已接收 {payload.Name}（{payload.SizeText}）";
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
        if (target is null)
        {
            StatusLine = "没有可接收的设备，请确认对方已打开 AirGrab";
            Raise(nameof(StatusLine));
            return false;
        }

        bool ok = await Transfer.SendAsync(payload, target).ConfigureAwait(true);
        Raise(nameof(TransferLog));
        return ok;
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
