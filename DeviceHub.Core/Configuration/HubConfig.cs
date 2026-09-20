using DeviceHub.Core.Models;

namespace DeviceHub.Core.Configuration;

/// <summary>
/// 设备点位配置（JSON 反序列化目标）。
/// 设计目标："新设备接入只改配置、不改代码"——加设备就是在配置文件里加一段。
/// </summary>
public sealed class PointConfig
{
    /// <summary>点位显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>设备侧地址，格式由驱动解释（Simulated：SIM.TEMP；S7：DB1.DBD0）。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>数据类型：Bool / Int / Real。</summary>
    public string DataType { get; set; } = "Real";
}

/// <summary>一台被采集设备的配置。</summary>
public sealed class DeviceConfig
{
    /// <summary>设备显示名（界面的设备下拉框用它）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>驱动类型标识：Simulated / S7（Modbus 在 M1 后续加入）。</summary>
    public string DriverType { get; set; } = string.Empty;

    /// <summary>采集周期（毫秒）。</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>S7 连接参数（Simulated 无需填写）。</summary>
    public string? Ip { get; set; }

    public short Rack { get; set; }

    public short Slot { get; set; }

    /// <summary>CPU 型号：S7300 / S7400 / S71200 / S71500。</summary>
    public string? Cpu { get; set; }

    public List<PointConfig> Points { get; set; } = [];

    /// <summary>转换为引擎使用的强类型点位表，同时校验数据类型拼写。</summary>
    public IReadOnlyList<PointDefinition> GetPoints() =>
        Points.Select(p => new PointDefinition(p.Name, p.Address, ParseDataType(p.DataType))).ToList();

    private static PointDataType ParseDataType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "bool" => PointDataType.Bool,
        "int" => PointDataType.Int,
        "real" => PointDataType.Real,
        _ => throw new FormatException($"配置中无法识别的数据类型：{value}（支持 Bool/Int/Real）"),
    };
}

/// <summary>整个采集程序的配置根。</summary>
public sealed class HubOptions
{
    public List<DeviceConfig> Devices { get; set; } = [];
}
