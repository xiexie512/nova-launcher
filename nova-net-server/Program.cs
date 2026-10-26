using NovaNet.Server;

// NovaNet Hub —— 自研虚拟局域网协调服务器
// 用法: dotnet run -- --port 8535 --bind 0.0.0.0
// 端口: TCP 8535 (WS 信令+中继+HTTP 状态) / UDP 8535 (STUN 公网端点探测)
// 部署: 见 Dockerfile 与 deploy.sh

public static class Program
{
    public static async Task Main(string[] args)
    {
        var port = 8535;
        var udpPort = 8535;
        var bind = "0.0.0.0";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--udp-port" when i + 1 < args.Length: udpPort = int.Parse(args[++i]); break;
                case "--bind" when i + 1 < args.Length: bind = args[++i]; break;
            }
        }

        Console.WriteLine("NovaNet Hub 1.0.0");
        Console.WriteLine($"  TCP(信令/中继) {bind}:{port}");
        Console.WriteLine($"  UDP(STUN)      {bind}:{udpPort}");
        Console.WriteLine($"  请确保防火墙/安全组放行 TCP {port} 与 UDP {udpPort}");

        await HubServer.RunAsync(port, udpPort, bind);
    }
}
