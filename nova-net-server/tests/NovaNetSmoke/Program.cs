using System.Net;
using System.Net.Sockets;
using System.Text;
using NovaNet.Engine;
using NovaNet.Server;

// NovaNet 端到端冒烟测试：
// 1. 本地起 Hub（TCP+UDP 同端口）
// 2. 两个引擎分别创建/加入房间（带密码）
// 3. 等待双方互见 → 打洞建立 P2P（或回退中继）
// 4. 通过「本地映射端口 → 隧道 → 对端游戏端口」回显测试验证数据链路
// 5. 再用 DisableP2P 验证纯中继链路

var port = 18535;
Console.WriteLine("== NovaNet 冒烟测试 ==");

// ---- 启动 Hub ----
_ = Task.Run(() => HubServer.RunAsync(port, port, "127.0.0.1"));
await WaitHubAsync(port);

var failures = 0;
failures += await RunPairAsync("P2P 直连", p2p: true, port, idA: "AAAAAAAAAAAAAAAA", idB: "BBBBBBBBBBBBBBBB");
failures += await RunPairAsync("中继转发", p2p: false, port, idA: "CCCCCCCCCCCCCCCC", idB: "DDDDDDDDDDDDDDDD");

Console.WriteLine(failures == 0 ? "\n✅ 全部测试通过" : $"\n❌ 共 {failures} 项失败");
return failures == 0 ? 0 : 1;

// ================= 测试一对客户端 =================

static async Task<int> RunPairAsync(string label, bool p2p, int port, string idA, string idB)
{
    Console.WriteLine($"\n--- 场景: {label} ---");
    var errors = 0;

    // 房主 A 的「游戏服务器」端口上起回显服务（模拟 MC 服务端）
    const int gamePortA = 26555;
    const int gamePortB = 26556;
    var echoServer = new TcpListener(IPAddress.Loopback, gamePortB);
    echoServer.Start();
    _ = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                var c = await echoServer.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using var s = c.GetStream();
                    var buf = new byte[8192];
                    int n;
                    while ((n = await s.ReadAsync(buf)) > 0)
                        await s.WriteAsync(buf.AsMemory(0, n));
                });
            }
        }
        catch { }
    });

    var engineA = NewEngine(idA, "房主A", gamePortA, p2p, port);
    var engineB = NewEngine(idB, "玩家B", gamePortB, p2p, port);

    engineA.Log += m => Console.WriteLine($"    [A] {m}");
    engineB.Log += m => Console.WriteLine($"    [B] {m}");

    engineA.Start();
    engineB.Start();

    try
    {
        // 等待双方在线
        await WaitUntil(15000, () => engineA.State == NnState.Online && engineB.State == NnState.Online,
            "双方连接 Hub");
        errors += Check(engineA.State == NnState.Online && engineB.State == NnState.Online, "双方在线");

        // 创建房间 + 加入
        var code = await engineA.CreateRoomAsync("冒烟测试房", "pw123");
        errors += Check(!string.IsNullOrEmpty(code) && code.Length == 6, $"房主创建房间，房间码 {code}");
        await engineB.JoinRoomAsync(code, "pw123");
        errors += Check(engineB.Room?.Code == code, "玩家加入房间");

        // 双方互见
        await WaitUntil(15000, () => engineA.Members.Count == 1 && engineB.Members.Count == 1, "双方互见成员");
        errors += Check(engineA.Members.Count == 1 && engineB.Members.Count == 1, "双方互见成员");
        errors += Check(engineA.Members[0].VirtualIp.StartsWith("10.55.0."), $"房主看到玩家虚拟IP {engineA.Members[0].VirtualIp}");

        // 等待隧道路径就绪（P2P 或中继）
        await WaitUntil(20000, () => engineA.Members.Count == 1 && engineA.Members[0].PathText != "连接中", "隧道路径就绪");
        var path = engineA.Members.Count > 0 ? engineA.Members[0].PathText : "无";
        errors += Check(path != "连接中", $"隧道路径: {path}");

        // 数据回显测试：玩家A 经「本地映射端口 → 隧道 → 房主B 游戏端口」发数据
        var portA = engineA.ConnectPortFor(engineA.Members[0].MemberId);
        errors += Check(portA != null && portA > 0, $"玩家侧映射端口: 127.0.0.1:{portA}");

        var payload = $"nova-net-{label}-{Guid.NewGuid():N}";
        var echo = await EchoAsync("127.0.0.1", portA!.Value, payload);
        errors += Check(echo == payload, $"数据经隧道往返一致 ({payload.Length} 字节)");

        // 离开房间后成员清空
        await engineB.LeaveRoomAsync();
        await WaitUntil(10000, () => engineA.Members.Count == 0, "玩家离开后房主成员列表清空");
        errors += Check(engineA.Members.Count == 0, "玩家离开后成员列表清空");
    }
    catch (Exception e)
    {
        Console.WriteLine($"  异常: {e.Message}");
        errors++;
    }
    finally
    {
        await engineA.StopAsync();
        await engineB.StopAsync();
        echoServer.Stop();
    }
    return errors;
}

static NnEngine NewEngine(string clientId, string name, int gamePort, bool p2p, int port, string? suffix = null)
    => new(new NovaNetConfig
    {
        ServerUri = $"ws://127.0.0.1:{port}",
        ClientId = clientId,
        Name = name,
        LocalGamePort = gamePort,
        DisableP2P = !p2p,
    });

static async Task<string?> EchoAsync(string host, int port, string payload)
{
    using var tcp = new TcpClient();
    using var ct = new CancellationTokenSource(10000);
    try
    {
        await tcp.ConnectAsync(host, port, ct.Token);
        using var s = tcp.GetStream();
        var data = Encoding.UTF8.GetBytes(payload);
        await s.WriteAsync(data, ct.Token);
        var buf = new byte[4096];
        var n = await s.ReadAsync(buf, ct.Token);
        return Encoding.UTF8.GetString(buf, 0, n);
    }
    catch (Exception e)
    {
        Console.WriteLine($"  Echo 失败: {e.Message}");
        return null;
    }
}

static async Task WaitHubAsync(int port)
{
    using var http = new HttpClient();
    for (var i = 0; i < 30; i++)
    {
        try
        {
            var r = await http.GetStringAsync($"http://127.0.0.1:{port}/healthz");
            if (r == "ok") { Console.WriteLine($"Hub 已就绪 (端口 {port})"); return; }
        }
        catch { }
        await Task.Delay(300);
    }
    throw new Exception("Hub 启动超时");
}

static async Task WaitUntil(int ms, Func<bool> cond, string what)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms)
    {
        if (cond()) { Console.WriteLine($"  ✓ {what}"); return; }
        await Task.Delay(200);
    }
    Console.WriteLine($"  ✗ 等待超时: {what}");
}

static int Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "✅" : "❌")} {what}");
    return ok ? 0 : 1;
}
