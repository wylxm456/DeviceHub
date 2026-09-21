using DeviceHub.Core.Configuration;
using DeviceHub.Core.Motion;

namespace DeviceHub.Drivers.SimulatedMotion;

/// <summary>
/// 模拟运动控制卡：不依赖任何硬件，用 20ms 定时Tick推进轴位置，
/// 完整实现安全联锁（未回零禁动 / 运动中禁新指令 / 软限位拒绝）与
/// 回零、Jog、绝对/相对定位、直线插补（按位移比例分配速度同时到达）、急停。
/// 用途：无控制卡的开发与演示环境；也是真卡实现（雷赛 SDK）的行为参照。
/// </summary>
public sealed class SimMotionControl : IMotionControl
{
    private const double TickSeconds = 0.02;

    private sealed class SimAxis
    {
        public required MotionAxisConfig Config;
        public double Position;
        public double? Target;          // 定位/回零目标，null = 无定位运动
        public double ActiveSpeed;
        public bool Homing;             // 回零进行中（到位后置 IsHomed）
        public bool Jogging;
        public int JogDirection;
        public bool IsHomed;
        public bool PositiveLimitTriggered;
        public bool NegativeLimitTriggered;
        public bool Alarm;

        public bool IsMoving => Target is not null || Jogging;
    }

    private readonly MotionConfig _config;
    private readonly Dictionary<int, SimAxis> _axes;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private bool _connected;
    private IReadOnlyList<int>? _axisIds;

    public string Name => _config.Name;

    public IReadOnlyList<int> AxisIds =>
        _axisIds ??= _axes.Keys.OrderBy(id => id).ToList();

    public SimMotionControl(MotionConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.Axes.Count == 0)
        {
            throw new ArgumentException("运动控制配置中没有轴。", nameof(config));
        }

        _axes = config.Axes.ToDictionary(a => a.Id, a => new SimAxis { Config = a });
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connected)
        {
            return Task.CompletedTask;
        }

        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(TickSeconds));
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _connected = false;
        return Task.CompletedTask;
    }

    public Task<AxisStatus> GetAxisStatusAsync(int axis, CancellationToken cancellationToken = default)
    {
        var a = RequireAxis(axis);
        lock (_gate)
        {
            return Task.FromResult(new AxisStatus(
                axis,
                a.Position,
                a.IsHomed,
                a.IsMoving,
                a.PositiveLimitTriggered,
                a.NegativeLimitTriggered,
                a.Alarm));
        }
    }

    public Task HomeAsync(int axis, CancellationToken cancellationToken = default)
    {
        var a = RequireAxis(axis);
        lock (_gate)
        {
            EnsureConnectedAndIdle(a, "回零");
            a.Homing = true;
            a.Target = 0;
            a.ActiveSpeed = Math.Max(a.Config.DefaultSpeed, 1);
        }

        return Task.CompletedTask;
    }

    public Task MoveAbsoluteAsync(int axis, double position, double speed, CancellationToken cancellationToken = default)
    {
        var a = RequireAxis(axis);
        lock (_gate)
        {
            EnsureConnectedAndIdle(a, "定位运动");
            EnsureHomed(a);
            EnsureSpeed(speed);
            EnsureWithinSoftLimit(a, position);

            a.Homing = false;
            a.Target = position;
            a.ActiveSpeed = speed;
        }

        return Task.CompletedTask;
    }

    public Task MoveRelativeAsync(int axis, double distance, double speed, CancellationToken cancellationToken = default)
    {
        var a = RequireAxis(axis);
        lock (_gate)
        {
            EnsureConnectedAndIdle(a, "相对运动");
            EnsureHomed(a);
            EnsureSpeed(speed);
            var target = a.Position + distance;
            EnsureWithinSoftLimit(a, target);

            a.Homing = false;
            a.Target = target;
            a.ActiveSpeed = speed;
        }

        return Task.CompletedTask;
    }

    public Task MoveLinearAsync(int[] axes, double[] targets, double speed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(axes);
        ArgumentNullException.ThrowIfNull(targets);
        if (axes.Length != targets.Length || axes.Length == 0)
        {
            throw new ArgumentException("插补的轴与目标数量必须一致且不为空。");
        }

        EnsureSpeed(speed);
        lock (_gate)
        {
            var simAxes = axes.Select(RequireAxis).ToArray();
            if (simAxes.Any(a => a.IsMoving))
            {
                throw new InvalidOperationException("插补被拒绝：存在运动中的轴。");
            }

            if (simAxes.Any(a => !a.IsHomed))
            {
                throw new InvalidOperationException("插补被拒绝：存在未回零的轴。");
            }

            // 直线插补的核心：各轴按位移比例分配速度，位移最大的轴用指令速度，
            // 其余等比缩放——所有轴同时到达，合成轨迹才是直线
            var distances = targets.Zip(simAxes, (t, a) => Math.Abs(t - a.Position)).ToArray();
            var maxDistance = distances.Max();
            if (maxDistance <= 0)
            {
                return Task.CompletedTask;
            }

            for (var i = 0; i < simAxes.Length; i++)
            {
                EnsureWithinSoftLimit(simAxes[i], targets[i]);
                var axisSpeed = speed * distances[i] / maxDistance;
                simAxes[i].Target = targets[i];
                simAxes[i].ActiveSpeed = Math.Max(axisSpeed, 0.001);
            }
        }

        return Task.CompletedTask;
    }

    public Task JogStartAsync(int axis, double speed, int direction, CancellationToken cancellationToken = default)
    {
        var a = RequireAxis(axis);
        if (direction is not 1 and not -1)
        {
            throw new ArgumentException("Jog 方向只能是 +1 或 -1。", nameof(direction));
        }

        EnsureSpeed(speed);
        lock (_gate)
        {
            EnsureConnectedAndIdle(a, "Jog");
            EnsureHomed(a);

            a.Jogging = true;
            a.JogDirection = direction;
            a.ActiveSpeed = speed;
        }

        return Task.CompletedTask;
    }

    public Task JogStopAsync(int axis, CancellationToken cancellationToken = default)
    {
        var a = RequireAxis(axis);
        lock (_gate)
        {
            a.Jogging = false;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(int axis, CancellationToken cancellationToken = default)
    {
        var a = RequireAxis(axis);
        lock (_gate)
        {
            a.Target = null;
            a.Jogging = false;
            a.Homing = false;
        }

        return Task.CompletedTask;
    }

    public Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var a in _axes.Values)
            {
                a.Target = null;
                a.Jogging = false;
                a.Homing = false;
            }
        }

        return Task.CompletedTask;
    }

    public Task ClearStatusAsync(int axis, CancellationToken cancellationToken = default)
    {
        var a = RequireAxis(axis);
        lock (_gate)
        {
            a.PositiveLimitTriggered = false;
            a.NegativeLimitTriggered = false;
            a.Alarm = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>20ms 一帧推进所有运动中的轴；Tick 内完成软限位碰撞检测。</summary>
    private void Tick()
    {
        lock (_gate)
        {
            foreach (var a in _axes.Values)
            {
                if (a.Jogging)
                {
                    var next = a.Position + a.JogDirection * a.ActiveSpeed * TickSeconds;
                    if (next > a.Config.SoftLimitPositive)
                    {
                        a.Position = a.Config.SoftLimitPositive;
                        a.PositiveLimitTriggered = true;
                        a.Alarm = true;
                        a.Jogging = false;
                    }
                    else if (next < a.Config.SoftLimitNegative)
                    {
                        a.Position = a.Config.SoftLimitNegative;
                        a.NegativeLimitTriggered = true;
                        a.Alarm = true;
                        a.Jogging = false;
                    }
                    else
                    {
                        a.Position = next;
                    }
                }
                else if (a.Target is { } target)
                {
                    var delta = target - a.Position;
                    var step = Math.Sign(delta) * a.ActiveSpeed * TickSeconds;
                    if (Math.Abs(step) >= Math.Abs(delta))
                    {
                        a.Position = target;
                        a.Target = null;
                        if (a.Homing)
                        {
                            a.IsHomed = true;
                            a.Homing = false;
                        }
                    }
                    else
                    {
                        a.Position += step;
                    }
                }
            }
        }
    }

    private SimAxis RequireAxis(int axis)
    {
        if (_axes.TryGetValue(axis, out var a))
        {
            return a;
        }

        throw new ArgumentException($"轴 {axis} 不存在（可用：{string.Join(',', AxisIds)}）。", nameof(axis));
    }

    private void EnsureConnectedAndIdle(SimAxis a, string operation)
    {
        if (!_connected)
        {
            throw new InvalidOperationException("控制卡未连接，请先连接。");
        }

        if (a.IsMoving)
        {
            throw new InvalidOperationException($"轴 {a.Config.Id} 正在运动，拒绝{operation}指令。");
        }
    }

    private static void EnsureHomed(SimAxis a)
    {
        if (!a.IsHomed)
        {
            throw new InvalidOperationException($"轴 {a.Config.Id} 未回零，禁止运动（请先执行回零）。");
        }
    }

    private static void EnsureSpeed(double speed)
    {
        if (speed <= 0)
        {
            throw new ArgumentException("速度必须大于 0。", nameof(speed));
        }
    }

    private static void EnsureWithinSoftLimit(SimAxis a, double position)
    {
        if (position > a.Config.SoftLimitPositive || position < a.Config.SoftLimitNegative)
        {
            throw new InvalidOperationException(
                $"轴 {a.Config.Id} 目标 {position:0.###} 超出软限位 [{a.Config.SoftLimitNegative}, {a.Config.SoftLimitPositive}]，指令已拒绝。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _timer.DisposeAsync().ConfigureAwait(false);
    }
}
