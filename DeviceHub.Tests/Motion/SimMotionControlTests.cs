using System.Diagnostics;
using DeviceHub.Core.Configuration;
using DeviceHub.Core.Motion;
using DeviceHub.Drivers.SimulatedMotion;
using Xunit;

namespace DeviceHub.Tests.Motion;

/// <summary>
/// 模拟运动控制卡测试：重点验证安全联锁（未回零禁动/运动中禁新指令/软限位）
/// 与运动学行为（定位到位/插补同时到达/急停）。 Tick 周期 20ms，速度 100mm/s
/// 时 10mm 行程约 100ms 走完，断言统一用 5 秒等待上限避免慢机抖动。
/// </summary>
public class SimMotionControlTests
{
    private static MotionConfig CreateConfig() => new()
    {
        Name = "测试模拟卡",
        DriverType = "SimMotion",
        Axes =
        [
            new MotionAxisConfig { Id = 0, Name = "X轴", SoftLimitPositive = 300, SoftLimitNegative = -300, DefaultSpeed = 100 },
            new MotionAxisConfig { Id = 1, Name = "Y轴", SoftLimitPositive = 300, SoftLimitNegative = -300, DefaultSpeed = 100 },
        ],
    };

    private static async Task<SimMotionControl> CreateConnectedAsync()
    {
        var control = new SimMotionControl(CreateConfig());
        await control.ConnectAsync();
        return control;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), message);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message, int timeoutMs = 5000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!await condition().ConfigureAwait(false) && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }

        Assert.True(await condition().ConfigureAwait(false), message);
    }

    private static async Task<AxisStatus> WaitUntilAsync(
        IMotionControl control, int axis, Func<AxisStatus, bool> condition, string message, int timeoutMs = 5000)
    {
        var stopwatch = Stopwatch.StartNew();
        var status = await control.GetAxisStatusAsync(axis);
        while (!condition(status) && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
            status = await control.GetAxisStatusAsync(axis);
        }

        Assert.True(condition(status), $"{message}（最后状态：{status}）");
        return status;
    }

    /// <summary>回零 + 走到指定位置的公共前置。</summary>
    private static async Task HomeAndMoveAsync(SimMotionControl control, int axis, double position)
    {
        await control.HomeAsync(axis);
        await WaitUntilAsync(control, axis, s => s.IsHomed && !s.IsMoving, "回零应完成");
        await control.MoveAbsoluteAsync(axis, position, 100);
        await WaitUntilAsync(control, axis, s => !s.IsMoving, "定位应完成");
    }

    [Fact]
    public async Task MoveAsync_BeforeHome_ShouldBeRejected()
    {
        var control = await CreateConnectedAsync();
        await using var _ = control;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => control.MoveAbsoluteAsync(0, 50, 100));
        Assert.Contains("未回零", ex.Message);
    }

    [Fact]
    public async Task MoveAsync_OutOfSoftLimit_ShouldBeRejected()
    {
        var control = await CreateConnectedAsync();
        await using var _ = control;
        await control.HomeAsync(0);
        await WaitUntilAsync(control, 0, s => s.IsHomed && !s.IsMoving, "回零应完成");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => control.MoveAbsoluteAsync(0, 500, 100)); // 软限位 +300
    }

    [Fact]
    public async Task HomeAsync_ShouldMarkHomed_AndSecondMoveWorks()
    {
        var control = await CreateConnectedAsync();
        await using var _ = control;

        await control.HomeAsync(0);
        var status = await WaitUntilAsync(control, 0, s => s.IsHomed && !s.IsMoving, "回零应完成");
        Assert.Equal(0, status.Position, 3);

        await control.MoveAbsoluteAsync(0, 25, 100);
        status = await WaitUntilAsync(control, 0, s => !s.IsMoving, "定位应完成");
        Assert.Equal(25, status.Position, 3);
    }

    [Fact]
    public async Task NewCommand_WhileMoving_ShouldBeRejected()
    {
        var control = await CreateConnectedAsync();
        await using var _ = control;
        await HomeAndMoveSetup(control);

        await control.MoveAbsoluteAsync(0, 200, 50); // 长行程
        await WaitUntilAsync(control, 0, s => s.IsMoving, "应进入运动状态");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => control.MoveAbsoluteAsync(0, 10, 100));

        await control.EmergencyStopAsync();
    }

    private static async Task HomeAndMoveSetup(SimMotionControl control)
    {
        await control.HomeAsync(0);
        await WaitUntilAsync(control, 0, s => s.IsHomed && !s.IsMoving, "回零应完成");
    }

    [Fact]
    public async Task StopAsync_ShouldFreezeAxisBeforeTarget()
    {
        var control = await CreateConnectedAsync();
        await using var _ = control;
        await HomeAndMoveSetup(control);

        await control.MoveAbsoluteAsync(0, 200, 20); // 10 秒才走完的长行程
        await WaitUntilAsync(control, 0, s => s.IsMoving, "应进入运动状态");
        await control.StopAsync(0);

        var status = await WaitUntilAsync(control, 0, s => !s.IsMoving, "停止后不应再运动");
        Assert.True(status.Position < 200, $"停止位置应小于目标：{status.Position}");
    }

    [Fact]
    public async Task EmergencyStop_ShouldStopAllAxes()
    {
        var control = await CreateConnectedAsync();
        await using var _ = control;
        await control.HomeAsync(0);
        await control.HomeAsync(1);
        await WaitUntilAsync(control, 0, s => s.IsHomed && !s.IsMoving, "回零应完成");
        await WaitUntilAsync(control, 1, s => s.IsHomed && !s.IsMoving, "回零应完成");

        await control.MoveAbsoluteAsync(0, 200, 20);
        await control.MoveAbsoluteAsync(1, 200, 20);
        await control.EmergencyStopAsync();

        var s0 = await WaitUntilAsync(control, 0, s => !s.IsMoving, "轴 0 应停止");
        var s1 = await WaitUntilAsync(control, 1, s => !s.IsMoving, "轴 1 应停止");
        Assert.True(s0.Position < 200 && s1.Position < 200);
    }

    [Fact]
    public async Task Jog_ShouldMovePosition_AndStopWorks()
    {
        var control = await CreateConnectedAsync();
        await using var _ = control;
        await HomeAndMoveSetup(control);

        var before = (await control.GetAxisStatusAsync(0)).Position;
        await control.JogStartAsync(0, 200, +1);
        await WaitUntilAsync(async () =>
        {
            var s = await control.GetAxisStatusAsync(0);
            return s.Position > before + 5;
        }, "Jog 应使位置增大");

        await control.JogStopAsync(0);
        await WaitUntilAsync(control, 0, s => !s.IsMoving, "JogStop 后应停止");
    }

    [Fact]
    public async Task JogIntoSoftLimit_ShouldTriggerLimit_AndClearStatusResets()
    {
        var control = await CreateConnectedAsync();
        await using var _ = control;
        await HomeAndMoveSetup(control);

        await control.JogStartAsync(0, 500, +1); // 从 0 向 +300 全速撞软限位
        var status = await WaitUntilAsync(control, 0, s => s.PositiveLimitTriggered, "应触发正软限位");

        Assert.Equal(300, status.Position, 3);
        Assert.False(status.IsMoving, "撞限位后应自动停");

        await control.ClearStatusAsync(0);
        status = await control.GetAxisStatusAsync(0);
        Assert.False(status.PositiveLimitTriggered);
        Assert.False(status.Alarm);
    }

    [Fact]
    public async Task MoveLinear_ShouldArriveAtBothTargets()
    {
        var control = await CreateConnectedAsync();
        await using var _ = control;
        await control.HomeAsync(0);
        await control.HomeAsync(1);
        await WaitUntilAsync(control, 0, s => s.IsHomed && !s.IsMoving, "回零应完成");
        await WaitUntilAsync(control, 1, s => s.IsHomed && !s.IsMoving, "回零应完成");

        await control.MoveLinearAsync([0, 1], [100, 50], 100);
        var s0 = await WaitUntilAsync(control, 0, s => !s.IsMoving, "X 轴应到位");
        var s1 = await WaitUntilAsync(control, 1, s => !s.IsMoving, "Y 轴应到位");

        Assert.Equal(100, s0.Position, 3);
        Assert.Equal(50, s1.Position, 3);
    }

    [Fact]
    public async Task Disconnect_ShouldRejectMotionCommands()
    {
        var control = await CreateConnectedAsync();
        await control.DisconnectAsync();
        await using var _ = control;

        await Assert.ThrowsAsync<InvalidOperationException>(() => control.HomeAsync(0));
    }
}
