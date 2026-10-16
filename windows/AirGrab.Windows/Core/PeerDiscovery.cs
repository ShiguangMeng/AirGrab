using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AirGrab.Core;

public sealed record Peer(
    string Id,
    string Name,
    string Platform,
    IPAddress Address,
    DateTimeOffset LastSeen)
{
    /// <summary>Segoe Fluent Icons 字形，用于列表图标。</summary>
    public string Glyph => Platform.ToLowerInvariant() switch
    {
        "windows" => "\uE7F8",
        "android" => "\uE8EA",
        "ipados" => "\uE70A",
        "ios" => "\uE8EA",
        _ => "\uE8B7"
    };

    /// <summary>信号强度档位 1..4，由最近一次续期间隔换算。</summary>
    public int SignalBars
    {
        get
        {
            double age = (DateTimeOffset.UtcNow - LastSeen).TotalSeconds;
            if (age < 2.5) return 4;
            if (age < 4.5) return 3;
            if (age < 6.5) return 2;
            return 1;
        }
    }
}

/// <summary>
/// 设备发现：UDP 广播信标 + 超时淘汰。
///
/// 信标为单行文本 <c>AGG-DISCOVER|&lt;id&gt;|&lt;name&gt;|&lt;platform&gt;|&lt;tcpPort&gt;</c>，
/// 每 <see cref="BeaconInterval"/> 向**每一块活动网卡的子网定向广播地址**各发一次；
/// 超过 <see cref="Ttl"/> 未再收到即视为离线。
///
/// 只发受限广播（255.255.255.255）在本机有多块网卡时不可靠 ——
/// 系统会按默认路由挑一块网卡送出，其余网段（Wi-Fi 与有线分处不同子网、
/// 存在 Hyper-V / VMware 虚拟网卡）永远收不到信标。因此这里枚举所有
/// 状态为 Up 的 IPv4 接口，用「地址 | ~掩码」算出各自的广播地址逐个发送。
/// </summary>
public sealed class PeerDiscovery : IDisposable
{
    public const int UdpPort = 25585;
    public const string BeaconPrefix = "AGG-DISCOVER";

    public string DisplayName { get; set; } = Environment.MachineName;
    public string Platform { get; set; } = "Windows";
    public TimeSpan BeaconInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(10);

    public event Action<IReadOnlyList<Peer>>? PeersChanged;

    private readonly string _selfId = Guid.NewGuid().ToString("N")[..8];
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly UdpClient _listener;
    private readonly UdpClient _sender;
    private readonly List<Task> _workers = new();

    /// <summary>UI 刷新节流：信号强度会随时间衰减，但不必每收到一次信标就重建列表。</summary>
    private DateTimeOffset _lastRaise = DateTimeOffset.MinValue;
    private static readonly TimeSpan RaiseThrottle = TimeSpan.FromSeconds(3);

    public PeerDiscovery()
    {
        // 接收端绑定固定端口；Windows 上同一端口需要 ReuseAddress 才能与发送端共存。
        _listener = new UdpClient { EnableBroadcast = true };
        _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort));

        _sender = new UdpClient { EnableBroadcast = true };
    }

    public void Start()
    {
        _workers.Add(Task.Run(() => ListenLoopAsync(_cts.Token)));
        _workers.Add(Task.Run(() => BeaconLoopAsync(_cts.Token)));
        _workers.Add(Task.Run(() => SweepLoopAsync(_cts.Token)));
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _listener.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }

            HandleBeacon(Encoding.UTF8.GetString(result.Buffer), result.RemoteEndPoint.Address);
        }
    }

    private void HandleBeacon(string text, IPAddress address)
    {
        string[] fields = text.Trim().Split('|');
        if (fields.Length < 4 || fields[0] != BeaconPrefix) return;

        string id = fields[1];
        if (string.Equals(id, _selfId, StringComparison.OrdinalIgnoreCase)) return; // 自己的广播丢弃

        var peer = new Peer(id, fields[2], fields[3], address, DateTimeOffset.UtcNow);

        // 只有设备身份或地址变化才强制刷新；其余情况走节流，
        // 避免每 2 秒重建一次列表导致卡片闪烁。
        bool identityChanged = !_peers.TryGetValue(id, out Peer? existing)
                            || !string.Equals(existing.Name, peer.Name, StringComparison.Ordinal)
                            || !string.Equals(existing.Platform, peer.Platform, StringComparison.Ordinal)
                            || !existing.Address.Equals(peer.Address);

        _peers[id] = peer;
        RaiseChanged(identityChanged);
    }

    /// <summary>
    /// 枚举所有活动 IPv4 接口的广播地址。
    /// 受限广播地址（255.255.255.255）一并保留，作为单网卡环境的兜底。
    /// </summary>
    private static List<IPEndPoint> BuildBroadcastTargets()
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, UdpPort) };

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (unicast.IPv4Mask is null) continue;

                byte[] ip = unicast.Address.GetAddressBytes();
                byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                if (ip.Length != 4 || mask.Length != 4) continue;

                byte[] broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                {
                    broadcast[i] = (byte)(ip[i] | (mask[i] ^ 0xFF));
                }

                var candidate = new IPAddress(broadcast);
                if (!targets.Exists(t => t.Address.Equals(candidate)))
                {
                    targets.Add(new IPEndPoint(candidate, UdpPort));
                }
            }
        }

        return targets;
    }

    private async Task BeaconLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(BeaconInterval);
        string line = string.Empty;
        List<IPEndPoint> targets = new();

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException) { return; }

            // 网卡可能随时增减（插拔网线、切换 Wi-Fi、虚拟网卡启停），每轮重算目标。
            targets = BuildBroadcastTargets();

            line = string.Concat(
                BeaconPrefix, "|", _selfId, "|", DisplayName, "|", Platform, "|", FileTransfer.TcpPort, "\n");
            byte[] bytes = Encoding.UTF8.GetBytes(line);

            foreach (IPEndPoint endpoint in targets)
            {
                try
                {
                    await _sender.SendAsync(bytes, endpoint, token).ConfigureAwait(false);
                }
                catch (SocketException) { /* 个别网段不可达，其余网段照常发 */ }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task SweepLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Ttl / 2);

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException) { return; }

            DateTimeOffset deadline = DateTimeOffset.UtcNow - Ttl;
            bool removed = false;

            foreach (var pair in _peers.ToArray())
            {
                if (pair.Value.LastSeen < deadline && _peers.TryRemove(pair.Key, out _))
                {
                    removed = true;
                }
            }

            if (removed) RaiseChanged(force: true);
        }
    }

    private void RaiseChanged(bool force)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!force && now - _lastRaise < RaiseThrottle) return;
        _lastRaise = now;

        PeersChanged?.Invoke(Snapshot());
    }

    public IReadOnlyList<Peer> Snapshot() => _peers.Values
        .OrderByDescending(p => p.SignalBars)
        .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll(_workers.ToArray(), TimeSpan.FromMilliseconds(600)); }
        catch (AggregateException) { /* 关闭期的取消异常无需处理 */ }

        _listener.Dispose();
        _sender.Dispose();
        _cts.Dispose();
    }
}
