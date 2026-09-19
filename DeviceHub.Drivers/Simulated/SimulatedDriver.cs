using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;

namespace DeviceHub.Drivers.Simulated;

/// <summary>
/// 模拟驱动：不依赖任何硬件，内置一个简单的"物理模型"——
/// 温度以 10%/帧 的速度趋近设定值并叠加噪声，压力做正弦波动。
/// 用途：在没有真实 PLC/仪表的机器上跑通整条采集链路，
/// 同时作为后续 S7/Modbus 驱动的参考实现（看它如何实现 IDeviceDriver 即可）。
/// </summary>
public sealed class SimulatedDriver : IDeviceDriver
{
    public const string AddressTemperature = "SIM.TEMP";
    public const string AddressPressure = "SIM.PRESS";
    public const string AddressSetPoint = "SIM.SET_POINT";

    private readonly Random _random = new();
    private double _temperature = 25.0;
    private double _setPoint = 50.0;
    private double _elapsedFrames;

    public string Name => "Simulated";
    public bool IsConnected { get; private set; }

    /// <summary>模拟设备自带的点位表。M1 起由配置文件取代。</summary>
    public static IReadOnlyList<PointDefinition> DefaultPoints { get; } =
    [
        new PointDefinition("温度", AddressTemperature, PointDataType.Real),
        new PointDefinition("压力", AddressPressure, PointDataType.Real),
        new PointDefinition("设定值", AddressSetPoint, PointDataType.Real),
    ];

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // 模拟真实握手耗时，让 UI 上的"连接过程"可感知
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        IsConnected = true;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PointValue>> ReadAsync(
        IReadOnlyList<PointDefinition> points,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("驱动未连接，请先调用 ConnectAsync。");
        }

        // 推进一帧"物理世界"。帧间隔取固定 0.5s（与采集周期同量级即可，模拟器不必精确）。
        _elapsedFrames++;
        _temperature += (_setPoint - _temperature) * 0.1 + (_random.NextDouble() - 0.5);
        var pressure = 0.40 + 0.05 * Math.Sin(_elapsedFrames / 5.0);

        var frame = new Dictionary<string, double>
        {
            [AddressTemperature] = Math.Round(_temperature, 2),
            [AddressPressure] = Math.Round(pressure, 3),
            [AddressSetPoint] = _setPoint,
        };

        var now = DateTime.Now;
        IReadOnlyList<PointValue> results = points
            .Select(p => frame.TryGetValue(p.Address, out var v)
                ? new PointValue(p.Name, v, PointQuality.Good, now)
                : new PointValue(p.Name, null, PointQuality.Bad, now))
            .ToList();

        return Task.FromResult(results);
    }

    public Task WriteAsync(
        PointDefinition point,
        double value,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("驱动未连接，请先调用 ConnectAsync。");
        }

        if (point.Address != AddressSetPoint)
        {
            throw new NotSupportedException($"模拟设备只有 {AddressSetPoint} 可写。");
        }

        _setPoint = Math.Clamp(value, 0, 100);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
