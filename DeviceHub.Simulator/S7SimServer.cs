using System.Runtime.InteropServices;
using DeviceHub.Core.Models;
using DeviceHub.Drivers.S7;

namespace DeviceHub.Simulator;

/// <summary>
/// snap7.dll Server API 的 P/Invoke 封装（仅本项目用到的最小函数集）。
/// </summary>
internal static class Snap7Native
{
    private const string Lib = "snap7";

    [DllImport(Lib)]
    internal static extern IntPtr Srv_Create();

    [DllImport(Lib)]
    internal static extern void Srv_Destroy(ref IntPtr server);

    [DllImport(Lib)]
    internal static extern int Srv_StartTo(IntPtr server, string address);

    [DllImport(Lib)]
    internal static extern int Srv_Stop(IntPtr server);

    [DllImport(Lib)]
    internal static extern int Srv_RegisterArea(
        IntPtr server, int areaCode, int index, IntPtr buffer, int size);

    [DllImport(Lib)]
    internal static extern int Srv_UnregisterArea(IntPtr server, int areaCode, int index);
}

/// <summary>
/// 自研 S7 从站模拟器：基于 snap7 Server API 在本机模拟一台 S7-300 CPU，
/// 客户端（S7Driver）用 CpuType.S7300 / Rack 0 / Slot 2 连接即可。
///
/// DB1 布局（标准访问偏移，与 TIA 非优化 DB 的绝对寻址一致）：
///   DBD0    Temperature  Real   只读（物理模型驱动）
///   DBD4    Pressure     Real   只读（物理模型驱动）
///   DBD8    SetPoint     Real   客户端可写，温度随之趋近
///   DBX12.0 Running      Bool
///   DBD16   Phase        Real   只读（正弦相位，调试用）
///
/// 物理模型与 SimulatedDriver 一致：每 250ms 一帧，温度以 5%/帧 趋近设定值。
/// 关键机制：DB 数据区是本进程的托管数组（固定 pin 住），客户端的读写都直接
/// 落在这块内存上——客户端写 SetPoint，物理模型下一帧从内存里读回并响应。
/// </summary>
public sealed class S7SimServer : IDisposable
{
    // snap7 Server API 用内部区域编号（PE=0…DB=5），不是 S7 报文层的 0x84
    private const int AreaDb = 5;
    public const int DbNumber = 1;
    public const int DbSize = 64;

    private const int OffsetTemperature = 0;
    private const int OffsetPressure = 4;
    private const int OffsetSetPoint = 8;
    private const int OffsetRunning = 12;
    private const int OffsetPhase = 16;

    private readonly byte[] _data = new byte[DbSize];
    private readonly object _gate = new();
    private readonly Random _random = new();
    private GCHandle _pin;
    private IntPtr _server = IntPtr.Zero;
    private Timer? _physicsTimer;
    private double _temperature = 25.0;
    private double _phase;

    public bool IsRunning { get; private set; }

    /// <summary>模拟设备的点位表（与上方 DB1 布局一一对应）。</summary>
    public static IReadOnlyList<PointDefinition> DefaultPoints { get; } =
    [
        new PointDefinition("温度", "DB1.DBD0", PointDataType.Real),
        new PointDefinition("压力", "DB1.DBD4", PointDataType.Real),
        new PointDefinition("设定值", "DB1.DBD8", PointDataType.Real),
        new PointDefinition("运行状态", "DB1.DBX12.0", PointDataType.Bool),
    ];

    /// <summary>在指定地址启动模拟 PLC。默认 127.0.0.1。</summary>
    public void Start(string ip = "127.0.0.1")
    {
        if (IsRunning)
        {
            return;
        }

        _server = Snap7Native.Srv_Create();
        if (_server == IntPtr.Zero)
        {
            throw new InvalidOperationException("snap7 服务器创建失败（检查 snap7.dll 是否在输出目录）。");
        }

        _pin = GCHandle.Alloc(_data, GCHandleType.Pinned);
        try
        {
            Check(Snap7Native.Srv_RegisterArea(
                _server, AreaDb, DbNumber, _pin.AddrOfPinnedObject(), _data.Length), "RegisterArea(DB1)");

            lock (_gate)
            {
                // SetPoint 初值 50.0；Running 置位
                S7BitConverter.WriteSingle(_data.AsSpan(OffsetSetPoint, 4), 50.0f);
                _data[OffsetRunning] = 1;
            }

            // 物理模型每 250ms 推进一帧；温度趋近"内存里的 SetPoint"（即客户端写的值）
            _physicsTimer = new Timer(_ => AdvancePhysics(), null, 0, 250);
            Check(Snap7Native.Srv_StartTo(_server, ip), $"StartTo({ip})");
            IsRunning = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void AdvancePhysics()
    {
        try
        {
            lock (_gate)
            {
                var setPoint = S7BitConverter.ToSingle(_data.AsSpan(OffsetSetPoint, 4));
                _temperature += (setPoint - _temperature) * 0.05 + (_random.NextDouble() - 0.5);
                _phase += 0.25;

                S7BitConverter.WriteSingle(_data.AsSpan(OffsetTemperature, 4), (float)_temperature);
                S7BitConverter.WriteSingle(
                    _data.AsSpan(OffsetPressure, 4), (float)(0.4 + 0.05 * Math.Sin(_phase)));
                S7BitConverter.WriteSingle(_data.AsSpan(OffsetPhase, 4), (float)_phase);
            }
        }
        catch
        {
            // 定时器回调不允许异常逃逸，否则整个进程直接崩溃
        }
    }

    public void Dispose()
    {
        _physicsTimer?.Dispose();
        _physicsTimer = null;

        if (_server != IntPtr.Zero)
        {
            Snap7Native.Srv_Stop(_server);
            Snap7Native.Srv_UnregisterArea(_server, AreaDb, DbNumber);
            Snap7Native.Srv_Destroy(ref _server);
            _server = IntPtr.Zero;
        }

        if (_pin.IsAllocated)
        {
            _pin.Free();
        }

        IsRunning = false;
    }

    private static void Check(int code, string what)
    {
        if (code != 0)
        {
            throw new InvalidOperationException($"snap7 {what} 失败，错误码 0x{code:X8}");
        }
    }
}
