using System.Text;

namespace AirGrab.Core;

public enum PayloadKind { Text, Link, Image, File }

public static class PayloadKindExtensions
{
    public static string ToWire(this PayloadKind kind) => kind switch
    {
        PayloadKind.Text => "text",
        PayloadKind.Link => "link",
        PayloadKind.Image => "image",
        _ => "file"
    };

    public static string Title(this PayloadKind kind) => kind switch
    {
        PayloadKind.Text => "文本",
        PayloadKind.Link => "链接",
        PayloadKind.Image => "图片",
        _ => "文件"
    };

    /// <summary>接收端落地方式：图片进相册、链接交浏览器、文本进剪贴板、其余落接收目录。</summary>
    public static string Landing(this PayloadKind kind) => kind switch
    {
        PayloadKind.Text => "剪贴板",
        PayloadKind.Link => "默认浏览器",
        PayloadKind.Image => "图片库",
        _ => "接收目录"
    };

    public static bool TryParse(string value, out PayloadKind kind)
    {
        switch (value)
        {
            case "text": kind = PayloadKind.Text; return true;
            case "link": kind = PayloadKind.Link; return true;
            case "image": kind = PayloadKind.Image; return true;
            case "file": kind = PayloadKind.File; return true;
            default: kind = PayloadKind.File; return false;
        }
    }
}

public sealed class Payload
{
    public PayloadKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public int ByteCount { get; init; }
    public byte[] Body { get; init; } = Array.Empty<byte>();

    public string SizeText => FormatSize(ByteCount);

    /// <summary>卡片缩略区的大字标签：取扩展名，无扩展名时退回类型名。</summary>
    public string ExtensionTag
    {
        get
        {
            int dot = Name.LastIndexOf('.');
            if (dot > 0 && dot < Name.Length - 1)
            {
                return Name[(dot + 1)..].ToUpperInvariant();
            }
            return Kind.Title();
        }
    }

    public static string FormatSize(int bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static Payload FromText(string text) => new()
    {
        Kind = PayloadKind.Text,
        Name = text.Length <= 24 ? text : text[..24],
        ByteCount = Encoding.UTF8.GetByteCount(text),
        Body = Encoding.UTF8.GetBytes(text)
    };

    public static Payload FromLink(string url) => new()
    {
        Kind = PayloadKind.Link,
        Name = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url,
        ByteCount = Encoding.UTF8.GetByteCount(url),
        Body = Encoding.UTF8.GetBytes(url)
    };

    /// <summary>图片载荷。字节为图片文件本体（PNG 或原始剪贴板位图流）。</summary>
    public static Payload FromImage(string name, byte[] imageBytes) => new()
    {
        Kind = PayloadKind.Image,
        Name = name,
        ByteCount = imageBytes.Length,
        Body = imageBytes
    };

    public static Payload FromFile(string name, byte[] fileBytes) => new()
    {
        Kind = PayloadKind.File,
        Name = name,
        ByteCount = fileBytes.Length,
        Body = fileBytes
    };
}

public sealed class AggFormatException : Exception
{
    public AggFormatException(string message) : base(message) { }
}

/// <summary>
/// AirGrab 交换协议编解码。
///
/// 明文格式：<c>AGG|&lt;type&gt;|&lt;size&gt;|&lt;name&gt;|&lt;payload&gt;</c>。
/// 净荷允许包含分隔符，因此解码按前四个分隔符切分，第四段之后的全部字节归净荷。
/// 头部长度上限 <see cref="MaxHeaderLength"/>，避免畸形报文触发超大分配。
/// </summary>
public static class AggCodec
{
    public const string Magic = "AGG";
    public const char Separator = '|';
    public const int MaxHeaderLength = 512;

    public static byte[] Encode(Payload payload)
    {
        string head = string.Concat(
            Magic, Separator,
            payload.Kind.ToWire(), Separator,
            payload.Body.Length.ToString(), Separator,
            payload.Name, Separator);

        byte[] headBytes = Encoding.UTF8.GetBytes(head);
        byte[] result = new byte[headBytes.Length + payload.Body.Length];
        Buffer.BlockCopy(headBytes, 0, result, 0, headBytes.Length);
        Buffer.BlockCopy(payload.Body, 0, result, headBytes.Length, payload.Body.Length);
        return result;
    }

    public static Payload Decode(byte[] data, int count)
    {
        int probeLength = Math.Min(count, MaxHeaderLength);
        string probe = Encoding.UTF8.GetString(data, 0, probeLength);

        string[] fields = new string[4];
        int cursor = 0;
        for (int i = 0; i < 4; i++)
        {
            int index = probe.IndexOf(Separator, cursor);
            if (index < 0) throw new AggFormatException("头部缺少分隔符");
            fields[i] = probe[cursor..index];
            cursor = index + 1;
        }

        if (fields[0] != Magic) throw new AggFormatException("magic 不匹配");
        if (!PayloadKindExtensions.TryParse(fields[1], out var kind)) throw new AggFormatException("未知载荷类型");
        if (!int.TryParse(fields[2], out int declared) || declared < 0) throw new AggFormatException("长度字段非法");

        int headerBytes = Encoding.UTF8.GetByteCount(probe[..cursor]);
        if (count - headerBytes != declared) throw new AggFormatException("净荷长度与声明不符");

        byte[] body = new byte[declared];
        Buffer.BlockCopy(data, headerBytes, body, 0, declared);

        return new Payload { Kind = kind, Name = fields[3], ByteCount = declared, Body = body };
    }
}

/// <summary>标准 CRC-32（IEEE 802.3，多项式 0xEDB88320 反射式）。</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }
}
