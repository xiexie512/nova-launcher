using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NovaNet.Engine;

/// <summary>NovaNet 协议常量。服务端与客户端共用同一份实现，避免协议漂移。</summary>
public static class NnConst
{
    /// <summary>帧魔数。</summary>
    public const string Magic = "NNT1";
    public const byte Version = 1;

    /// <summary>Hub 默认端口（TCP 信令/中继 + UDP STUN 同号端口）。</summary>
    public const int DefaultPort = 8535;

    /// <summary>二进制帧头长度：magic4+ver1+type1+room16+from16+to16+flags1+seq4+len4 = 63。</summary>
    public const int HeaderSize = 63;
    public const int MaxPayload = 1 << 16; // 64 KiB

    /// <summary>虚拟网段前缀，房间内虚拟 IP 形如 10.55.0.x。</summary>
    public const string VirtualPrefix = "10.55.0.";

    /// <summary>本地联机映射端口基数：成员虚拟 IP 10.55.0.3 → 127.0.0.1:20003。</summary>
    public const int LocalBasePort = 20000;

    /// <summary>房间码字符集（去掉易混淆的 0/O/1/I）。</summary>
    public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes(Magic);

    /// <summary>单条 DATA 载荷中的流内序号窗口（乱序缓冲大小）。</summary>
    public const int StreamReorderWindow = 64;
}

/// <summary>帧类型。</summary>
public enum NnType : byte
{
    /// <summary>UDP 打洞试探包（无载荷）。</summary>
    Connect = 1,
    /// <summary>用户数据，载荷 = sid(4)+sseq(4)+chunk（明文层），可再被 AES-GCM 加密。</summary>
    Data = 2,
    /// <summary>P2P 保活包（无载荷）。</summary>
    KeepAlive = 3,
    /// <summary>流关闭，载荷 = sid(4)+sseq(4)。</summary>
    Close = 4,
}

/// <summary>二进制帧（UDP 直连与 WS 中继共用）。</summary>
public readonly record struct NnFrame(
    NnType Type,
    string RoomId,
    string From,
    string To,
    bool Encrypted,
    uint Seq,
    byte[] Payload)
{
    public static NnFrame Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < NnConst.HeaderSize)
            throw new InvalidDataException("帧长度不足");
        if (!b[..4].SequenceEqual(NnConst.MagicBytes))
            throw new InvalidDataException("帧魔数错误");
        if (b[4] != NnConst.Version)
            throw new InvalidDataException("协议版本不匹配");
        var type = (NnType)b[5];
        var room = Encoding.ASCII.GetString(b[6..22]).TrimEnd('\0');
        var from = Encoding.ASCII.GetString(b[22..38]).TrimEnd('\0');
        var to = Encoding.ASCII.GetString(b[38..54]).TrimEnd('\0');
        var enc = (b[54] & 1) != 0;
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(b[55..59]);
        var len = BinaryPrimitives.ReadUInt32LittleEndian(b[59..63]);
        if (len > NnConst.MaxPayload || b.Length < NnConst.HeaderSize + len)
            throw new InvalidDataException($"帧长度异常: len={len}, buf={b.Length}");
        var payload = b.Slice(NnConst.HeaderSize, (int)len).ToArray();
        return new NnFrame(type, room, from, to, enc, seq, payload);
    }

    public byte[] Encode()
    {
        if (RoomId.Length > 16 || From.Length > 16 || To.Length > 16)
            throw new InvalidDataException("room/from/to 超过 16 字节");
        var buf = new byte[NnConst.HeaderSize + Payload.Length];
        Encoding.ASCII.GetBytes(NnConst.Magic, buf.AsSpan(0, 4));
        buf[4] = NnConst.Version;
        buf[5] = (byte)Type;
        Encoding.ASCII.GetBytes(RoomId.PadRight(16, '\0'), buf.AsSpan(6, 16));
        Encoding.ASCII.GetBytes(From.PadRight(16, '\0'), buf.AsSpan(22, 16));
        Encoding.ASCII.GetBytes(To.PadRight(16, '\0'), buf.AsSpan(38, 16));
        buf[54] = Encrypted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(55), Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(59), (uint)Payload.Length);
        Payload.CopyTo(buf, NnConst.HeaderSize);
        return buf;
    }
}

/// <summary>
/// 端到端 AES-GCM 加密：key = SHA256("NovaNet:" + 房间码 + ":" + 房间密码)。
/// nonce = 发送方 memberId 前 8 字节 + seq(4B LE)，seq 在本客户端全局单调递增，保证 nonce 不重复。
/// 中继服务器只看得见路由头，看不到游戏数据。
/// </summary>
public sealed class NnCrypto
{
    private readonly byte[] _key;

    public NnCrypto(string roomCode, string password)
    {
        _key = SHA256.HashData(Encoding.UTF8.GetBytes($"NovaNet:{roomCode}:{password}"));
    }

    public byte[] Encrypt(string fromId, uint seq, byte[] plain)
    {
        var nonce = BuildNonce(fromId, seq);
        var outBuf = new byte[plain.Length + 16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, outBuf.AsSpan(0, plain.Length), outBuf.AsSpan(plain.Length, 16));
        return outBuf;
    }

    public byte[] Decrypt(string fromId, uint seq, byte[] enc)
    {
        if (enc.Length < 16) throw new InvalidDataException("密文长度不足");
        var nonce = BuildNonce(fromId, seq);
        var plain = new byte[enc.Length - 16];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, enc.AsSpan(0, enc.Length - 16), enc.AsSpan(enc.Length - 16), plain);
        return plain;
    }

    private static byte[] BuildNonce(string fromId, uint seq)
    {
        var nonce = new byte[12];
        var n = Math.Min(8, fromId.Length);
        Encoding.ASCII.GetBytes(fromId, 0, n, nonce, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), seq);
        return nonce;
    }
}

/// <summary>"ip:port" 端点解析。</summary>
public static class NnNet
{
    public static bool TryParse(string? s, out string host, out int port)
    {
        host = ""; port = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var i = s.LastIndexOf(':');
        if (i <= 0 || i == s.Length - 1) return false;
        host = s[..i];
        return int.TryParse(s[(i + 1)..], out port) && port > 0 && port < 65536;
    }

    public static IPEndPoint ToEndPoint(string? s)
    {
        if (TryParse(s, out var host, out var port) && IPAddress.TryParse(host, out var ip))
            return new IPEndPoint(ip, port);
        throw new FormatException($"无效端点: {s}");
    }
}
