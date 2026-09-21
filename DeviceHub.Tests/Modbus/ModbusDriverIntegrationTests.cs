using DeviceHub.Core.Models;
using DeviceHub.Drivers.Modbus;
using DeviceHub.Simulator;
using NModbus;
using Xunit;

namespace DeviceHub.Tests.Modbus;

/// <summary>
/// Modbus TCP 驱动端到端集成测试：进程内启动迷你从站模拟器，
/// 走真实 Modbus TCP 协议（MBAP + FC03/FC06/FC16）验证读写。
/// 模拟器用系统分配端口，不与手动开着的 Modbus Slave 抢 502。
/// </summary>
public class ModbusDriverIntegrationTests
{
    private static readonly PointDefinition Temperature =
        new("温度", "HR0", PointDataType.Real);

    private static readonly PointDefinition SetPoint =
        new("设定值", "HR4", PointDataType.Real);

    private static readonly PointDefinition Running =
        new("运行状态", "HR6", PointDataType.Bool);

    [Fact]
    public async Task ReadAsync_AgainstSimulatedSlave_ShouldReturnGoodValues()
    {
        using var slave = new ModbusTcpSlaveSimulator();
        slave.WriteFloat(0, 42.5f);
        slave.HoldingRegisters[6] = 1;
        slave.Start();

        await using var driver = new ModbusTcpDriver("127.0.0.1", slave.Port);
        await driver.ConnectAsync();

        var values = await driver.ReadAsync([Temperature, SetPoint, Running]);

        Assert.All(values, v => Assert.Equal(PointQuality.Good, v.Quality));
        Assert.Equal(42.5, values[0].Value);
        Assert.Equal(0.0, values[1].Value);
        Assert.Equal(1.0, values[2].Value);
    }

    [Fact]
    public async Task WriteSetPoint_ShouldLandInSlaveRegisters_OverRealProtocol()
    {
        using var slave = new ModbusTcpSlaveSimulator();
        slave.Start();

        await using var driver = new ModbusTcpDriver("127.0.0.1", slave.Port);
        await driver.ConnectAsync();

        await driver.WriteAsync(SetPoint, 77.5);

        // 从站寄存器里应出现 77.5 的 ABCD 大端布局
        Assert.Equal(77.5f, slave.ReadFloat(4));
        var readBack = (await driver.ReadAsync([SetPoint]))[0];
        Assert.Equal(77.5, readBack.Value);
    }

    [Fact]
    public async Task ReadAsync_OutOfRangeRegister_ShouldThrow()
    {
        using var slave = new ModbusTcpSlaveSimulator(registerCount: 8);
        slave.Start();

        await using var driver = new ModbusTcpDriver("127.0.0.1", slave.Port);
        await driver.ConnectAsync();

        var beyond = new PointDefinition("越界", "HR100", PointDataType.Int);
        await Assert.ThrowsAsync<SlaveException>(
            () => driver.ReadAsync([beyond]));
    }
}
