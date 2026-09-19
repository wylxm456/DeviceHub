using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;
using DeviceHub.Drivers.Simulated;
using Xunit;

namespace DeviceHub.Tests;

public class SimulatedDriverTests
{
    private static readonly PointDefinition Temperature =
        new("温度", SimulatedDriver.AddressTemperature, PointDataType.Real);

    private static readonly PointDefinition SetPoint =
        new("设定值", SimulatedDriver.AddressSetPoint, PointDataType.Real);

    [Fact]
    public async Task ConnectAsync_ShouldMarkConnected()
    {
        await using var driver = new SimulatedDriver();

        Assert.False(driver.IsConnected);
        await driver.ConnectAsync();

        Assert.True(driver.IsConnected);
    }

    [Fact]
    public async Task ReadAsync_BeforeConnect_ShouldThrow()
    {
        await using var driver = new SimulatedDriver();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.ReadAsync(SimulatedDriver.DefaultPoints));
    }

    [Fact]
    public async Task ReadAsync_KnownAddresses_ShouldReturnGoodQuality()
    {
        await using var driver = new SimulatedDriver();
        await driver.ConnectAsync();

        var values = await driver.ReadAsync(SimulatedDriver.DefaultPoints);

        Assert.Equal(SimulatedDriver.DefaultPoints.Count, values.Count);
        Assert.All(values, v => Assert.Equal(PointQuality.Good, v.Quality));
        Assert.All(values, v => Assert.NotNull(v.Value));
    }

    [Fact]
    public async Task ReadAsync_UnknownAddress_ShouldReturnBadQuality()
    {
        await using var driver = new SimulatedDriver();
        await driver.ConnectAsync();
        var unknown = new PointDefinition("不存在", "SIM.NOPE", PointDataType.Real);

        var values = await driver.ReadAsync([unknown]);

        Assert.Equal(PointQuality.Bad, values[0].Quality);
        Assert.Null(values[0].Value);
    }

    [Fact]
    public async Task WriteSetPoint_ShouldPullTemperatureUp()
    {
        await using var driver = new SimulatedDriver();
        await driver.ConnectAsync();

        await driver.WriteAsync(SetPoint, 90);
        var first = (await driver.ReadAsync([Temperature]))[0].Value!.Value;

        double last = first;
        for (var i = 0; i < 20; i++)
        {
            last = (await driver.ReadAsync([Temperature]))[0].Value!.Value;
        }

        Assert.True(last > first + 10, $"写设定值后温度应明显上升：first={first}, last={last}");
    }

    [Fact]
    public async Task WriteReadOnlyAddress_ShouldThrowNotSupported()
    {
        await using var driver = new SimulatedDriver();
        await driver.ConnectAsync();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => driver.WriteAsync(Temperature, 1));
    }
}
