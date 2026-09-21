namespace DeviceHub.Core.Motion;

/// <summary>单轴状态快照。</summary>
/// <param name="AxisId">轴编号。</param>
/// <param name="Position">当前位置（用户单位，mm）。</param>
/// <param name="IsHomed">是否已完成回零（未回零的轴禁止运动——安全联锁）。</param>
/// <param name="IsMoving">是否正在运动（定位中 / Jog 中 / 回零中）。</param>
/// <param name="PositiveLimitTriggered">正软限位触发。</param>
/// <param name="NegativeLimitTriggered">负软限位触发。</param>
/// <param name="Alarm">轴报警。</param>
public sealed record AxisStatus(
    int AxisId,
    double Position,
    bool IsHomed,
    bool IsMoving,
    bool PositiveLimitTriggered,
    bool NegativeLimitTriggered,
    bool Alarm);

/// <summary>
/// 运动控制抽象：屏蔽具体控制卡差异（雷赛/固高/正运动/仿真），向上层提供统一的轴控能力。
/// 与 IDeviceDriver 同一设计哲学——运动实现是插件，界面与业务零改动。
///
/// 语义约定（模拟实现与将来的真卡实现都遵守）：
///   所有运动指令为"启动即返回"（非阻塞），进度通过 GetAxisStatusAsync 轮询；
///   未回零的轴禁止运动；运动中的轴禁止新指令（仅允许停止类操作）；
///   目标超出软限位直接拒绝（防呆在指令层，不靠硬件撞限位）。
/// </summary>
public interface IMotionControl : IAsyncDisposable
{
    /// <summary>控制卡名称。</summary>
    string Name { get; }

    /// <summary>可用轴编号列表。</summary>
    IReadOnlyList<int> AxisIds { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>读取单轴状态快照。</summary>
    Task<AxisStatus> GetAxisStatusAsync(int axis, CancellationToken cancellationToken = default);

    /// <summary>启动回零（向零点运动，到位后自动置 IsHomed）。</summary>
    Task HomeAsync(int axis, CancellationToken cancellationToken = default);

    /// <summary>绝对定位。</summary>
    Task MoveAbsoluteAsync(int axis, double position, double speed, CancellationToken cancellationToken = default);

    /// <summary>相对定位。</summary>
    Task MoveRelativeAsync(int axis, double distance, double speed, CancellationToken cancellationToken = default);

    /// <summary>
    /// 多轴直线插补：按各轴位移比例分配速度，保证所有轴同时到达、合成轨迹为直线。
    /// </summary>
    Task MoveLinearAsync(int[] axes, double[] targets, double speed, CancellationToken cancellationToken = default);

    /// <summary>启动 Jog 连续运动（direction：+1 / -1）。</summary>
    Task JogStartAsync(int axis, double speed, int direction, CancellationToken cancellationToken = default);

    /// <summary>停止 Jog。</summary>
    Task JogStopAsync(int axis, CancellationToken cancellationToken = default);

    /// <summary>单轴减速停止。</summary>
    Task StopAsync(int axis, CancellationToken cancellationToken = default);

    /// <summary>急停：所有轴立即停止。</summary>
    Task EmergencyStopAsync(CancellationToken cancellationToken = default);

    /// <summary>清除轴的报警与限位触发标志。</summary>
    Task ClearStatusAsync(int axis, CancellationToken cancellationToken = default);
}
