using DeviceHub.Simulator;

Console.WriteLine("DeviceHub S7 从站模拟器（基于 snap7 Server API）");
Console.WriteLine("监听 127.0.0.1:102 | CPU 315（Rack 0 / Slot 2）| DB1 布局见 S7SimServer 源码");

using var simulator = new S7SimServer();
simulator.Start();
Console.WriteLine("运行中——上位机可随时连接。按 Q 或 Ctrl+C 退出，关闭窗口亦可。");

// 退出处理覆盖两条路径：
// 1. Ctrl+C 显式接管（默认终止行为不可依赖——Windows Terminal 键位映射/原生库
//    注册自己的控制台处理程序都可能吞掉信号，实测失效过）；
// 2. Q 键轮询兜底（Console.KeyAvailable 非阻塞探测，输入被重定向时跳过）。
// 无论哪条路径：先优雅停机（Dispose 释放 snap7 资源），再 Environment.Exit 强制兜底——
// snap7 原生线程或仍连接的上位机都不允许卡住退出
using var exitSignal = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exitSignal.Set();
};

while (!exitSignal.Wait(200))
{
    if (Console.IsInputRedirected || !Console.KeyAvailable)
    {
        continue;
    }

    if (Console.ReadKey(intercept: true).Key == ConsoleKey.Q)
    {
        break;
    }
}

try
{
    simulator.Dispose();
}
catch
{
    // 上位机还连着时 snap7 停止可能报错——模拟器退出不要求它成功
}

Environment.Exit(0);
