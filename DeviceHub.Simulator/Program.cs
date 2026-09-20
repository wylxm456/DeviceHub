using DeviceHub.Simulator;

Console.WriteLine("DeviceHub S7 从站模拟器（基于 snap7 Server API）");
Console.WriteLine("监听 127.0.0.1:102 | CPU 315（Rack 0 / Slot 2）| DB1 布局见 S7SimServer 源码");

using var simulator = new S7SimServer();
simulator.Start();

Console.WriteLine("运行中——上位机可随时连接，按 Ctrl+C 退出。");
await Task.Delay(Timeout.Infinite);
