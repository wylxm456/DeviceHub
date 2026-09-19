using DeviceHub.Core.Models;

namespace DeviceHub.Core.DeviceDriver;

/// <summary>
/// 设备驱动抽象：屏蔽具体协议差异，向上层提供统一的"连接—读写点位"能力。
/// 这是本项目最核心的抽象——新增协议（S7/Modbus/OPC UA…）时只需新增实现，
/// 采集引擎与界面零改动。
/// </summary>
public interface IDeviceDriver : IAsyncDisposable
{
    /// <summary>驱动名称，用于界面与日志标识，如 "Simulated"、"Siemens S7"。</summary>
    string Name { get; }

    /// <summary>当前是否处于已连接状态。</summary>
    bool IsConnected { get; }

    /// <summary>建立与设备的连接。重复调用应幂等。</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>断开连接并释放通信资源。未连接时调用应安全返回。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>批量读取点位。实现方保证返回结果与请求点位一一对应（读不到的给 Bad 质量）。</summary>
    Task<IReadOnlyList<PointValue>> ReadAsync(
        IReadOnlyList<PointDefinition> points,
        CancellationToken cancellationToken = default);

    /// <summary>写入单个数值点位。地址不可写时抛 NotSupportedException。</summary>
    Task WriteAsync(
        PointDefinition point,
        double value,
        CancellationToken cancellationToken = default);
}
