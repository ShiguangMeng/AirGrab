using System.Collections.Concurrent;
using System.Net;
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
/// 每 <see cref="BeaconInterval"/> 向受限广播地址发一次；
/// 超过 <see cref="Ttl"/> 未再收到即视为离线。不依赖任何账号体系，局域网内平权发现。
/// </summary>
public sealed class PeerDiscovery : IDisposable
{
    public const int UdpPort = 25585;
    public const string BeaconPrefix = "AGG-DISCOVER";

    public string DisplayName { get; set; } = Environment.MachineName;
    public string Platform { get; set; } = "Windows";
    public TimeSpan BeaconInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(8);

    public event Action<IReadOnlyList<Peer>>? PeersChanged;

    private readonly string _selfId = Guid.NewGuid().ToString("N")[..8];
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly UdpClient _listener;
    private readonly UdpClient _sender;
    private readonly List<Task> _workers = new();

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

        _peers[id] = new Peer(id, fields[2], fields[3], address, DateTimeOffset.UtcNow);
        RaiseChanged();
    }

    private async Task BeaconLoopAsync(CancellationToken token)
    {
        var endpoint = new IPEndPoint(IPAddress.Broadcast, UdpPort);
        using var timer = new PeriodicTimer(BeaconInterval);

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException) { return; }

            string line = string.Concat(
                BeaconPrefix, "|", _selfId, "|", DisplayName, "|", Platform, "|", FileTransfer.TcpPort, "\n");

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                await _sender.SendAsync(bytes, endpoint, token).ConfigureAwait(false);
            }
            catch (SocketException) { /* 广播在部分网卡上会短暂失败，下一轮重试即可 */ }
            catch (OperationCanceledException) { return; }
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

            if (removed) RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        var snapshot = _peers.Values
            .OrderByDescending(p => p.SignalBars)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        PeersChanged?.Invoke(snapshot);
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
