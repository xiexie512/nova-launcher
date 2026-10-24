using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NovaLauncher.Services;

/// <summary>
/// 轻量 Minecraft 服务器列表状态探测（Server List Ping, 1.7+ 协议）。
/// 用于主页「固定的服务器」展示 X/N 人在线。
/// </summary>
public static class ServerPingService
{
    public static async Task<(int Online, int Max)?> PingAsync(string address, int timeoutMs = 2500)
    {
        try
        {
            var (host, port) = Split(address);
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token);

            var stream = client.GetStream();

            // 握手包：协议 764（1.20.4+ 通用，服务端一般向下兼容）
            using var hs = new MemoryStream();
            WriteVarInt(hs, 764);                     // protocol version
            WriteString(hs, host);
            WriteUShort(hs, port);
            WriteVarInt(hs, 1);                       // next state: status
            await WritePacketAsync(stream, 0x00, hs.ToArray(), cts.Token);

            // 状态请求包
            await WritePacketAsync(stream, 0x00, Array.Empty<byte>(), cts.Token);

            // 读响应
            var packetLen = await ReadVarIntAsync(stream, cts.Token);
            var body = await ReadExactAsync(stream, packetLen, cts.Token);
            var reader = new MemoryStream(body);
            ReadVarInt(reader);                       // packet id (0x00)
            var jsonLen = ReadVarInt(reader);
            var json = Encoding.UTF8.GetString(ReadExactSync(reader, jsonLen));

            using var doc = JsonDocument.Parse(json);
            var players = doc.RootElement.GetProperty("players");
            var online = players.TryGetProperty("online", out var o) ? o.GetInt32() : 0;
            var max = players.TryGetProperty("max", out var m) ? m.GetInt32() : 0;
            return (online, max);
        }
        catch
        {
            return null; // 服务器未开 / 超时 / 协议不兼容
        }
    }

    private static (string Host, int Port) Split(string address)
    {
        var host = address; var port = 25565;
        var idx = address.LastIndexOf(':');
        if (idx > 0 && int.TryParse(address[(idx + 1)..], out var p))
        { host = address[..idx]; port = p; }
        return (host, port);
    }

    private static void WriteVarInt(Stream s, int v)
    {
        uint u = (uint)v;
        while ((u & ~0x7F) != 0) { s.WriteByte((byte)((u & 0x7F) | 0x80)); u >>= 7; }
        s.WriteByte((byte)u);
    }

    private static int ReadVarInt(Stream s)
    {
        int result = 0, shift = 0;
        while (true)
        {
            var b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 35) throw new InvalidDataException("varint too big");
        }
        return result;
    }

    private static void WriteString(Stream s, string str)
    {
        var bytes = Encoding.UTF8.GetBytes(str);
        WriteVarInt(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteUShort(Stream s, int v)
    {
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)(v & 0xFF));
    }

    private static async Task WritePacketAsync(NetworkStream stream, int id, byte[] payload, CancellationToken ct)
    {
        using var pkt = new MemoryStream();
        WriteVarInt(pkt, id);
        pkt.Write(payload, 0, payload.Length);
        var data = pkt.ToArray();

        using var framed = new MemoryStream();
        WriteVarInt(framed, data.Length);
        framed.Write(data, 0, data.Length);
        var outBytes = framed.ToArray();
        await stream.WriteAsync(outBytes, 0, outBytes.Length, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken ct)
    {
        int result = 0, shift = 0;
        while (true)
        {
            var buf = await ReadExactAsync(stream, 1, ct);
            result |= (buf[0] & 0x7F) << shift;
            if ((buf[0] & 0x80) == 0) break;
            shift += 7;
            if (shift > 35) throw new InvalidDataException("varint too big");
        }
        return result;
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = await stream.ReadAsync(buf, got, count - got, ct);
            if (n <= 0) throw new EndOfStreamException();
            got += n;
        }
        return buf;
    }

    private static byte[] ReadExactSync(Stream s, int count)
    {
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = s.Read(buf, got, count - got);
            if (n <= 0) throw new EndOfStreamException();
            got += n;
        }
        return buf;
    }
}
