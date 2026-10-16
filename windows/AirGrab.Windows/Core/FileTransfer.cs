using System.Net;
using System.Net.Sockets;

namespace AirGrab.Core;

/// <summary>
/// 投递层：TCP 分片传输，逐片 CRC32 校验。
///
/// 报文结构（全部小端）：
/// <code>
/// [4B 净荷总长][4B 片偏移][4B 片长][4B 片 CRC32][片数据]
/// </code>
/// 接收端逐片校验，任一片失败即丢弃该片 —— 不把损坏数据写进结果集。
/// 偏移量使接收端能够按序重组，并为断点续传预留。
/// </summary>
public sealed class FileTransfer : IDisposable
{
    public const int TcpPort = 25586;
    public const int ChunkSize = 64 * 1024;
    private const int HeaderSize = 16;

    public bool IsServing { get; private set; }
    public string LastLog { get; private set; } = "空闲";

    public event Action<Payload>? PayloadReceived;
    public event Action<Payload, string>? PayloadSent;
    public event Action<double>? ProgressChanged;
    public event Action<string>? LogChanged;

    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptTask;

    public void StartServer()
    {
        _listener = new TcpListener(IPAddress.Any, TcpPort);
        _listener.Start();
        IsServing = true;
        SetLog($"接收服务已就绪 :{TcpPort}");
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => ReceiveAsync(client, token), token);
        }
    }

    // ---------------------------------------------------------------- 接收

    private async Task ReceiveAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            NetworkStream stream = client.GetStream();
            var buffer = new byte[ChunkSize + HeaderSize];
            int filled = 0;
            var assembly = new MemoryStream();
            int declaredTotal = -1;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), token)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    filled += read;

                    int consumed = 0;
                    while (filled - consumed >= HeaderSize)
                    {
                        int total = ReadInt32(buffer, consumed);
                        int offset = ReadInt32(buffer, consumed + 4);
                        int length = ReadInt32(buffer, consumed + 8);
                        uint expected = ReadUInt32(buffer, consumed + 12);

                        if (length < 0 || length > ChunkSize) { filled = 0; consumed = 0; goto nextRead; }
                        if (filled - consumed < HeaderSize + length) break;

                        if (declaredTotal < 0) declaredTotal = total;

                        if (Crc32.Compute(buffer.AsSpan(consumed + HeaderSize, length)) != expected)
                        {
                            SetLog($"校验失败，丢弃该片 @{offset}");
                            consumed += HeaderSize + length;
                            continue;
                        }

                        assembly.Write(buffer, consumed + HeaderSize, length);
                        consumed += HeaderSize + length;

                        if (declaredTotal > 0 && assembly.Length >= declaredTotal)
                        {
                            Deliver(assembly, declaredTotal);
                            assembly.SetLength(0);
                            declaredTotal = -1;
                        }
                    }

                    if (consumed > 0)
                    {
                        Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed);
                        filled -= consumed;
                    }

                nextRead: ;
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException ex) { SetLog($"接收中断：{ex.Message}"); }
            catch (SocketException ex) { SetLog($"接收中断：{ex.Message}"); }
        }
    }

    private void Deliver(MemoryStream assembly, int length)
    {
        byte[] frame = assembly.GetBuffer();
        try
        {
            Payload payload = AggCodec.Decode(frame, length);
            SetLog($"已接收 {payload.Name}（{payload.SizeText}）");
            PayloadReceived?.Invoke(payload);
        }
        catch (AggFormatException ex)
        {
            SetLog($"载荷解析失败：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 发送

    public async Task<bool> SendAsync(Payload payload, Peer peer)
    {
        byte[] wire = AggCodec.Encode(payload);
        var chunks = Fragment(wire);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(peer.Address, TcpPort).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();

            for (int i = 0; i < chunks.Count; i++)
            {
                await stream.WriteAsync(chunks[i]).ConfigureAwait(false);
                ProgressChanged?.Invoke((double)(i + 1) / chunks.Count);
            }

            await stream.FlushAsync().ConfigureAwait(false);
            ProgressChanged?.Invoke(1);

            SetLog($"已投送 {payload.Name} → {peer.Name}");
            PayloadSent?.Invoke(payload, peer.Name);
            return true;
        }
        catch (SocketException ex)
        {
            SetLog($"传输中断：{ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            SetLog($"传输中断：{ex.Message}");
            return false;
        }
    }

    public static List<byte[]> Fragment(byte[] wire)
    {
        var chunks = new List<byte[]>();
        int offset = 0;

        while (offset < wire.Length)
        {
            int length = Math.Min(ChunkSize, wire.Length - offset);
            var chunk = new byte[HeaderSize + length];

            WriteInt32(chunk, 0, wire.Length);
            WriteInt32(chunk, 4, offset);
            WriteInt32(chunk, 8, length);
            WriteUInt32(chunk, 12, Crc32.Compute(wire.AsSpan(offset, length)));
            Buffer.BlockCopy(wire, offset, chunk, HeaderSize, length);

            chunks.Add(chunk);
            offset += length;
        }

        return chunks;
    }

    // ---------------------------------------------------------------- 小端读写

    private static void WriteInt32(byte[] target, int offset, int value) => WriteUInt32(target, offset, (uint)value);

    private static void WriteUInt32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value & 0xFF);
        target[offset + 1] = (byte)((value >> 8) & 0xFF);
        target[offset + 2] = (byte)((value >> 16) & 0xFF);
        target[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static int ReadInt32(byte[] source, int offset) => (int)ReadUInt32(source, offset);

    private static uint ReadUInt32(byte[] source, int offset) =>
        (uint)(source[offset]
             | (source[offset + 1] << 8)
             | (source[offset + 2] << 16)
             | (source[offset + 3] << 24));

    private void SetLog(string message)
    {
        LastLog = message;
        LogChanged?.Invoke(message);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        try { _acceptTask?.Wait(TimeSpan.FromMilliseconds(400)); } catch (AggregateException) { }
        _cts.Dispose();
        IsServing = false;
    }
}
