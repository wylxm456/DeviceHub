using DeviceHub.Core.Alarming;
using DeviceHub.Core.Models;
using DeviceHub.Core.Security;

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

    /// <summary>S7 / Modbus TCP 连接参数（Simulated 无需填写）。</summary>
    public string? Ip { get; set; }

    /// <summary>Modbus TCP 端口，默认 502。</summary>
    public int Port { get; set; } = 502;

    /// <summary>Modbus 从站地址（Unit ID），默认 1。</summary>
    public byte SlaveId { get; set; } = 1;

    public short Rack { get; set; }

    public short Slot { get; set; }

    /// <summary>CPU 型号：S7300 / S7400 / S71200 / S71500。</summary>
    public string? Cpu { get; set; }

    /// <summary>Modbus RTU 串口名（如 COM3），仅 ModbusRtu 驱动需要。</summary>
    public string? SerialPort { get; set; }

    /// <summary>串口波特率，默认 9600。</summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>校验位：None/Odd/Even。Modbus 串行链路规范默认 Even。</summary>
    public string Parity { get; set; } = "Even";

    /// <summary>数据位，默认 8。</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>停止位：One/OnePointFive/Two，默认 One。</summary>
    public string StopBits { get; set; } = "One";

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

/// <summary>运动控制轴配置。</summary>
public sealed class MotionAxisConfig
{
    public int Id { get; set; }

    /// <summary>轴显示名，如 "X轴"。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>正软限位（mm）。指令目标超出即拒绝。</summary>
    public double SoftLimitPositive { get; set; } = 300;

    /// <summary>负软限位（mm）。</summary>
    public double SoftLimitNegative { get; set; } = -300;

    /// <summary>默认速度（mm/s），界面定位运动的预填值。</summary>
    public double DefaultSpeed { get; set; } = 50;
}

/// <summary>运动控制卡配置（绑定 appsettings.json 的 Motion 节）。</summary>
public sealed class MotionConfig
{
    /// <summary>控制卡显示名。</summary>
    public string Name { get; set; } = "运动控制";

    /// <summary>实现类型：SimMotion（模拟卡；雷赛等真卡在后续里程碑加入）。</summary>
    public string DriverType { get; set; } = "SimMotion";

    public List<MotionAxisConfig> Axes { get; set; } = [];
}

/// <summary>北向服务配置（绑定 appsettings.json 的 Northbound 节）。</summary>
public sealed class NorthboundConfig
{
    /// <summary>OPC UA Server 监听端口（默认 4840，OPC UA 标准端口）。</summary>
    public int OpcUaPort { get; set; } = 4840;

    /// <summary>MQTT 嵌入式 Broker 监听端口（默认 1883，MQTT 标准端口）。</summary>
    public int MqttPort { get; set; } = 1883;
}

/// <summary>一条登录账号的配置（JSON 反序列化目标）。</summary>
public sealed class AuthUserConfig
{
    public string Username { get; set; } = string.Empty;

    /// <summary>明文，或 "sha256:十六进制哈希"（AuthService 按前缀自动识别）。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>角色：Operator / Engineer。</summary>
    public string Role { get; set; } = "Operator";

    public AuthUser ToUser() => new(
        Username,
        Password,
        Role.Trim().ToLowerInvariant() switch
        {
            "operator" => UserRole.Operator,
            "engineer" => UserRole.Engineer,
            _ => throw new FormatException($"账号“{Username}”的角色无法识别：{Role}（支持 Operator/Engineer）"),
        });
}

/// <summary>登录账号配置（绑定 appsettings.json 的 Auth 节）。</summary>
public sealed class AuthConfig
{
    public List<AuthUserConfig> Users { get; set; } = [];
}

/// <summary>一条报警规则的配置（JSON 反序列化目标），字符串字段在 ToRule 时解析并校验。</summary>
public sealed class AlarmRuleConfig
{
    /// <summary>绑定的点位显示名。</summary>
    public string PointName { get; set; } = string.Empty;

    /// <summary>条件：HighLimit / LowLimit / BadQuality。</summary>
    public string Type { get; set; } = "HighLimit";

    /// <summary>限值阈值（BadQuality 条件忽略）。</summary>
    public double Threshold { get; set; }

    /// <summary>回差：防阈值附近震荡（BadQuality 条件忽略）。</summary>
    public double Hysteresis { get; set; }

    /// <summary>级别：Warning / Critical。</summary>
    public string Level { get; set; } = "Warning";

    public AlarmRule ToRule() => AlarmRule.Create(
        PointName,
        Type.Trim().ToLowerInvariant() switch
        {
            "highlimit" => AlarmCondition.HighLimit,
            "lowlimit" => AlarmCondition.LowLimit,
            "badquality" => AlarmCondition.BadQuality,
            _ => throw new FormatException($"报警条件无法识别：{Type}（支持 HighLimit/LowLimit/BadQuality）"),
        },
        Threshold,
        Hysteresis,
        Level.Trim().ToLowerInvariant() switch
        {
            "warning" => AlarmLevel.Warning,
            "critical" => AlarmLevel.Critical,
            _ => throw new FormatException($"报警级别无法识别：{Level}（支持 Warning/Critical）"),
        });
}

/// <summary>历史存储配置（绑定 appsettings.json 的 Storage 节）。</summary>
public sealed class StorageConfig
{
    /// <summary>SQLite 库文件路径（相对路径锚定 exe 目录）。</summary>
    public string DatabasePath { get; set; } = "data/devicehub.db";

    /// <summary>后台批量冲刷周期（毫秒）：界面只进队列，磁盘写由它节流。</summary>
    public int FlushIntervalMs { get; set; } = 2000;

    /// <summary>历史查询单次最大返回行数。</summary>
    public int MaxQueryRows { get; set; } = 2000;
}

/// <summary>报警引擎配置（绑定 appsettings.json 的 Alarms 节）。</summary>
public sealed class AlarmConfig
{
    public List<AlarmRuleConfig> Rules { get; set; } = [];

    /// <summary>历史事件容量上限。</summary>
    public int HistoryCapacity { get; set; } = 500;
}

/// <summary>断线重连策略配置（绑定 appsettings.json 的 Reconnect 节）。</summary>
public sealed class ReconnectConfig
{
    /// <summary>连续失败多少个采集周期才触发重连：单次网络抖动不值得惊动重连。</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>首次重试等待（毫秒），之后按指数退避翻倍。</summary>
    public int BaseDelayMs { get; set; } = 1000;

    /// <summary>重试等待上限（毫秒）：退避必须封顶，恢复后的最大感知延迟才有界。</summary>
    public int MaxDelayMs { get; set; } = 15000;
}

/// <summary>实时曲线页配置（绑定 appsettings.json 的 Curve 节）。</summary>
public sealed class CurveConfig
{
    /// <summary>
    /// 每个点位保留的最大采样数（滚动窗口长度）。
    /// 500ms 采集周期 × 300 点 ≈ 2.5 分钟的趋势，长挂不涨内存。
    /// </summary>
    public int MaxPoints { get; set; } = 300;
}

/// <summary>合成相机配置（视觉引导的仿真取流来源）。</summary>
public sealed class VisionConfig
{
    /// <summary>图像宽（像素）。</summary>
    public int ImageWidth { get; set; } = 640;

    /// <summary>图像高（像素）。</summary>
    public int ImageHeight { get; set; } = 480;

    /// <summary>像素当量：1mm 对应多少像素（即相机的放大倍率）。</summary>
    public double ScalePxPerMm { get; set; } = 10;

    /// <summary>视场左上角对应的机台世界坐标 X（mm）。</summary>
    public double WorldOriginX { get; set; } = 0;

    /// <summary>视场左上角对应的机台世界坐标 Y（mm）。</summary>
    public double WorldOriginY { get; set; } = 0;

    /// <summary>工件尺寸（mm），矩形长边。</summary>
    public double WorkpieceLengthMm { get; set; } = 4.0;

    /// <summary>工件尺寸（mm），矩形短边。</summary>
    public double WorkpieceWidthMm { get; set; } = 3.0;

    /// <summary>定位器实现：OpenCv（阈值+轮廓，默认）/ Halcon（形状模板匹配）。</summary>
    public string Locator { get; set; } = "OpenCv";
}
