using System;
using NovaLauncher.PluginApi;
using NovaLauncher.Services;

// 复现 HelloPlugin 的 ConsoleLine 处理器与日志总线构成的递归：
// 处理器在收到含 ERROR 的行时，会调用 LogService.Append 把该行重新打进日志总线，
// 而 Append 又会触发 ConsoleLine，形成无限递归。
//
// 修复前（无重入保护）：LogService.Append("...ERROR...") 会无限递归直至栈溢出、进程 SIGABRT。
// 修复后（重入保护）：嵌套的 Append 不再重放 ConsoleLine，递归被阻断，日志条数有界。

LauncherEvents.ConsoleLine += line =>
{
    if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        LogService.Append("[游戏错误] " + line);   // 与 HelloPlugin.cs 的处理器一致
};

LogService.Append("minecraft launch line contains ERROR here");
LogService.Append("normal line without trigger");
// 再模拟一条游戏报错行
LogService.Append("net.minecraft: [main/ERROR] renderer failed");

// 等待事件同步完成后统计
var count = LogService.Lines.Count;
Console.WriteLine("TOTAL_LINES=" + count);

// 有界 = 修复生效；若仍旧无限递归，进程会在统计前就因栈溢出被 SIGABRT 终止
if (count < 200)
{
    Console.WriteLine("RESULT=PASS re-entrancy guard works, recursion bounded");
}
else
{
    Console.WriteLine("RESULT=FAIL line count exploded: " + count);
}
