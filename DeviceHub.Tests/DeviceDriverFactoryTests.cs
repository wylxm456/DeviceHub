using DeviceHub.Core.Configuration;
using DeviceHub.Core.DeviceDriver;
using DeviceHub.Drivers;
using DeviceHub.Drivers.Simulated;
using DeviceHub.Drivers.S7;
using Xunit;

namespace DeviceHub.Tests;

public class DeviceDriverFactoryTests
{
    [Fact]
    public void Create_Simulated_ShouldReturnSimulatedDriver()
    {
        var config = new DeviceConfig { Name = "模拟", DriverType = "Simulated" };

        var driver = DeviceDriverFactory.Create(config);

        Assert.IsType<SimulatedDriver>(driver);
    }

    [Fact]
    public void Create_S7_ShouldReturnS7Driver()
    {
        var config = new DeviceConfig
        {
            Name = "虚拟PLC",
            DriverType = "s7", // 大小写不敏感
            Ip = "127.0.0.1",
            Rack = 0,
            Slot = 2,
            Cpu = "S7300",
        };

        var driver = DeviceDriverFactory.Create(config);

        Assert.IsType<S7Driver>(driver);
    }

    [Fact]
    public void Create_S7WithoutIp_ShouldThrowArgument()
    {
        var config = new DeviceConfig { Name = "缺IP的S7", DriverType = "S7" };

        Assert.Throws<ArgumentException>(() => DeviceDriverFactory.Create(config));
    }

    [Fact]
    public void Create_S7WithUnknownCpu_ShouldThrowFormat()
    {
        var config = new DeviceConfig { Name = "怪CPU", DriverType = "S7", Ip = "127.0.0.1", Cpu = "S9999" };

        Assert.Throws<FormatException>(() => DeviceDriverFactory.Create(config));
    }

    [Fact]
    public void Create_UnknownDriverType_ShouldThrowNotSupported()
    {
        var config = new DeviceConfig { Name = "外星设备", DriverType = "AlienBus" };

        Assert.Throws<NotSupportedException>(() => DeviceDriverFactory.Create(config));
    }
}
