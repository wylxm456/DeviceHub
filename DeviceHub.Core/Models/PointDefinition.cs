namespace DeviceHub.Core.Models;

/// <summary>点位数据类型。随驱动扩展，M1 起与 S7/Modbus 的具体类型建立映射。</summary>
public enum PointDataType
{
    Bool,
    Int,
    Real,
}

/// <summary>
/// 点位定义：一个采集点的静态描述。
/// M1 起该结构由 JSON 配置文件生成，实现"加设备只改配置、不改代码"。
/// </summary>
/// <param name="Name">点位显示名，如"炉温"。</param>
/// <param name="Address">设备侧地址，含义由具体驱动解释（S7 的 DB 偏移 / Modbus 的寄存器号）。</param>
/// <param name="DataType">数据类型。</param>
public sealed record PointDefinition(string Name, string Address, PointDataType DataType);
