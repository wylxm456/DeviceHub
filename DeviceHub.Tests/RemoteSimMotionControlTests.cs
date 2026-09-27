#if HAS_DELTA_DEVICE
using System.Diagnostics;
using DeviceHub.Core.Configuration;
using DeviceHub.Core.Motion;
using DeviceHub.Drivers.RemoteMotion;
using Delta.Control;
using Delta.Device;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// 虚拟调试端到端测试（真实双端）：DeviceHub 的 RemoteSimMotionControl（Modbus 主站）
/// 对接 Delta 仿真设备的 MockRobotController + ModbusRobotAdapter（从站）——
/// 两个真实项目按协议合同对话，不 mock 任何一端的协议栈。
/// 仅在本机存在并列 Delta 仓库时编译（见 DeviceHub.Tests.csproj 的 Exists 条件）。
/// </summary>
public class RemoteSimMotionControlTests : IDisposable
{
    private const double HomeX = 90, HomeY = 0, HomeZ = -380; // Mock 拍照位（mm）

    private readonly MockRobotController _mock = new();
    private readonly ModbusRobotAdapter _adapter;
    private readonly RemoteSimMotionControl _control;

    public RemoteSimMotionControlTests()
    {
        _adapter = new ModbusRobotAdapter(_mock, port: 0);
        // 先连 Mock（设备侧"上电"）：置 Idle 状态并初始化位置寄存器——漏了这步，
        // 设备侧所有指令都会回错误码 4（未连接），表现为"远程设备未连接"
        _mock.ConnectAsync().GetAwaiter().GetResult();
        _adapter.Start();
        _control = new RemoteSimMotionControl(new MotionConfig { Ip = "127.0.0.1", Port = _adapter.Port });
        _control.ConnectAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _control.DisposeAsync().GetAwaiter().GetResult();
        _adapter.Dispose();
        _mock.Dispose();
    }

    [Fact]
    public async Task Home_MarksAxesHomed_AndMovesToHomePosition()
    {
        await _control.HomeAsync(0);

        var status = await PollAxisAsync(0, s => s.IsHomed && !s.IsMoving);
        Assert.True(status.IsHomed);
        Assert.InRange(status.Position, HomeX - 1, HomeX + 1);

        // Delta 回原点是整机动作：三轴同时置位、Y/Z 也在拍照位
        Assert.True((await _control.GetAxisStatusAsync(1)).IsHomed);
        Assert.True((await _control.GetAxisStatusAsync(2)).IsHomed);
        var y = await _control.GetAxisStatusAsync(1);
        var z = await _control.GetAxisStatusAsync(2);
        Assert.InRange(y.Position, HomeY - 1, HomeY + 1);
        Assert.InRange(z.Position, HomeZ - 1, HomeZ + 1);
    }

    [Fact]
    public async Task MoveAbsolute_SingleAxis_OthersHoldPosition()
    {
        await HomeIfRequiredAsync();
        await _control.MoveAbsoluteAsync(0, 70, 80);

        var x = await PollAxisAsync(0, s => Math.Abs(s.Position - 70) < 1 && !s.IsMoving);
        Assert.InRange(x.Position, 69, 71);
        var y = await _control.GetAxisStatusAsync(1);
        var z = await _control.GetAxisStatusAsync(2);
        Assert.InRange(y.Position, HomeY - 1, HomeY + 1);
        Assert.InRange(z.Position, HomeZ - 1, HomeZ + 1);
    }

    [Fact]
    public async Task MoveLinear_XY_BothAxesConverge()
    {
        await HomeIfRequiredAsync();
        await _control.MoveLinearAsync([0, 1], [60, -30], 80);

        var x = await PollAxisAsync(0, s => Math.Abs(s.Position - 60) < 1 && !s.IsMoving);
        var y = await _control.GetAxisStatusAsync(1);
        Assert.InRange(x.Position, 59, 61);
        Assert.InRange(y.Position, -31, -29);
    }

    [Fact]
    public async Task MoveAbsolute_Unreachable_ThrowsWithFriendlyMessage()
    {
        await HomeIfRequiredAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _control.MoveAbsoluteAsync(2, 50, 80)); // 基座平面之上，Delta 必不可达

        Assert.Contains("不可达", ex.Message);
    }

    [Fact]
    public async Task MoveAbsolute_WhileMoving_ThrowsBusy_ThenEmergencyStopWorks()
    {
        await HomeIfRequiredAsync();

        // 低速远距运动（时长 ≥0.25s），启动后立刻再发指令 → 设备忙
        var first = _control.MoveAbsoluteAsync(0, -80, 30);
        await Task.Delay(60);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _control.MoveAbsoluteAsync(0, 70, 30));
        Assert.Contains("忙", ex.Message);

        await _control.EmergencyStopAsync();
        await first; // 急停后第一笔运动以取消收尾，不应有未观察异常

        var status = await PollAxisAsync(0, s => !s.IsMoving);
        Assert.False(status.IsMoving);
    }

    [Fact]
    public async Task MoveWithoutHome_IsRejectedByInterlock()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _control.MoveAbsoluteAsync(0, 70, 80));

        Assert.Contains("回零", ex.Message);
    }

    [Fact]
    public async Task Factory_CreatesRemoteSim_FromConfig()
    {
        var control = DeviceHub.Drivers.MotionControlFactory.Create(
            new MotionConfig { DriverType = "RemoteSim", Ip = "127.0.0.1", Port = _adapter.Port });
        await control.ConnectAsync();
        try
        {
            var status = await control.GetAxisStatusAsync(0);
            Assert.False(status.IsMoving);
        }
        finally
        {
            await control.DisposeAsync();
        }
    }

    // ==== 辅助 ====

    /// <summary>回原点并等运动结束（RemoteSim 启动即返回，回原点后设备还有 ≤0.5s 的收尾运动）。</summary>
    private async Task HomeIfRequiredAsync()
    {
        var status = await _control.GetAxisStatusAsync(0);
        if (!status.IsHomed)
        {
            await _control.HomeAsync(0);
        }

        await PollAxisAsync(0, s => !s.IsMoving);
    }

    /// <summary>轮询直到条件满足（或超时返回最后一次快照）。</summary>
    private async Task<AxisStatus> PollAxisAsync(
        int axis, Func<AxisStatus, bool> predicate, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        var last = await _control.GetAxisStatusAsync(axis);
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            last = await _control.GetAxisStatusAsync(axis);
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(30);
        }

        return last;
    }
}
#endif
