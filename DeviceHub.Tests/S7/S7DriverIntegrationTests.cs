using DeviceHub.Core.Models;
using DeviceHub.Drivers.S7;
using DeviceHub.Simulator;
using Xunit;

namespace DeviceHub.Tests.S7;

/// <summary>
/// S7 驱动端到端集成测试：测试进程内直接启动 snap7 模拟服务器，
/// 走真实的 S7 协议栈（ISO-on-TCP → S7 PDU）验证读写。
/// 注意：xUnit 同一测试类内串行执行，避免两个服务器抢占 102 端口。
/// </summary>
public class S7DriverIntegrationTests
{
    private static readonly PointDefinition Temperature = S7SimServer.DefaultPoints[0];
    private static readonly PointDefinition SetPoint = S7SimServer.DefaultPoints[2];
    private static readonly PointDefinition Running = S7SimServer.DefaultPoints[3];

    [Fact]
    public async Task ReadAsync_AgainstSimulatedPlc_ShouldReturnGoodValues()
    {
        using var server = new S7SimServer();
        server.Start();
        await using var driver = new S7Driver("127.0.0.1");
        await driver.ConnectAsync();

        var values = await driver.ReadAsync(S7SimServer.DefaultPoints);

        Assert.Equal(S7SimServer.DefaultPoints.Count, values.Count);
        Assert.All(values, v => Assert.Equal(PointQuality.Good, v.Quality));

        var temperature = values.First(v => v.Name == "温度");
        Assert.NotNull(temperature.Value);
        Assert.InRange(temperature.Value!.Value, 0, 100);

        var running = values.First(v => v.Name == "运行状态");
        Assert.Equal(1.0, running.Value);
    }

    [Fact]
    public async Task WriteSetPoint_ShouldPullTemperatureUp_OverRealProtocol()
    {
        using var server = new S7SimServer();
        server.Start();
        await using var driver = new S7Driver("127.0.0.1");
        await driver.ConnectAsync();

        await driver.WriteAsync(SetPoint, 90.0);
        var first = (await driver.ReadAsync([Temperature]))[0].Value!.Value;

        // 物理模型 250ms/帧，温度以 5%/帧 趋近 90；3.2s 足够看到明显上升
        double last = first;
        for (var i = 0; i < 16; i++)
        {
            await Task.Delay(200);
            last = (await driver.ReadAsync([Temperature]))[0].Value!.Value;
        }

        Assert.True(last > first + 10, $"温度应明显上升：first={first}, last={last}");
    }

    [Fact]
    public async Task ConnectAsync_Twice_ShouldBeIdempotent()
    {
        using var server = new S7SimServer();
        server.Start();
        await using var driver = new S7Driver("127.0.0.1");

        await driver.ConnectAsync();
        await driver.ConnectAsync();

        Assert.True(driver.IsConnected);
    }
}
