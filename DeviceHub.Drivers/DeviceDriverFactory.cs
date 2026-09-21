using DeviceHub.Core.Configuration;
using DeviceHub.Core.DeviceDriver;
using DeviceHub.Drivers.Modbus;
using DeviceHub.Drivers.S7;
using DeviceHub.Drivers.Simulated;
using S7.Net;

namespace DeviceHub.Drivers;

/// <summary>
/// 驱动工厂：把配置里的驱动类型标识翻译成具体 IDeviceDriver 实例。
/// 新增协议 = 这里加一个 case + 配置类加对应字段，Core 与界面零改动。
/// </summary>
public static class DeviceDriverFactory
{
    public static IDeviceDriver Create(DeviceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.DriverType.Trim().ToLowerInvariant() switch
        {
            "simulated" => new SimulatedDriver(),
            "s7" => new S7Driver(
                config.Ip ?? throw new ArgumentException($"S7 设备“{config.Name}”缺少 Ip 配置。"),
                config.Rack,
                config.Slot,
                ParseCpu(config)),
            "modbustcp" => new ModbusTcpDriver(
                config.Ip ?? throw new ArgumentException($"Modbus TCP 设备“{config.Name}”缺少 Ip 配置。"),
                config.Port,
                config.SlaveId),
            _ => throw new NotSupportedException($"未知驱动类型：{config.DriverType}（当前支持 Simulated/S7/ModbusTcp）"),
        };
    }

    private static CpuType ParseCpu(DeviceConfig config) => (config.Cpu ?? "S7300").Trim().ToUpperInvariant() switch
    {
        "S7300" => CpuType.S7300,
        "S7400" => CpuType.S7400,
        "S71200" => CpuType.S71200,
        "S71500" => CpuType.S71500,
        _ => throw new FormatException($"设备“{config.Name}”的 CPU 型号无法识别：{config.Cpu}"),
    };
}
