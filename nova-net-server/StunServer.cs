using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NovaNet.Server;

/// <summary>
/// UDP 监听：对客户端的 STUN 探测回显其观察到的公网端点（打洞所需的公网 UDP 地址）。
/// 协议：客户端发 "STUN" + 16 字节 clientId，服务端回 "ip:port"。
/// 与 TCP 信令共用同一端口号（UDP/TCP 可同号）。
/// </summary>
public static class StunServer
{
    public static async Task RunAsync(int port, string bind)
    {
        var ep = new IPEndPoint(IPAddress.Parse(bind), port);
        using var udp = new UdpClient(ep);
        Console.WriteLine($"[NovaNet] UDP STUN 监听 {bind}:{port}");
        while (true)
        {
            try
            {
                var res = await udp.ReceiveAsync();
                if (res.Buffer.Length < 4) continue;
                var head = Encoding.ASCII.GetString(res.Buffer, 0, Math.Min(4, res.Buffer.Length));
                if (head != "STUN") continue;
                var reply = Encoding.UTF8.GetBytes(res.RemoteEndPoint.ToString());
                await udp.SendAsync(reply, res.RemoteEndPoint);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[NovaNet] UDP 异常: {e.Message}");
                await Task.Delay(1000);
            }
        }
    }
}
