using System.Globalization;

namespace DeviceHub.Drivers.Modbus;

/// <summary>
/// 解析 Modbus 保持寄存器地址，两种写法都支持：
/// HR10 → 寄存器 10（工程写法，无歧义）；
/// 40011 → 寄存器 10（4xxxx 约定：40001 = 保持寄存器 0，电气图纸上的通用写法）。
/// </summary>
public static class ModbusAddress
{
    public static ushort Parse(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var trimmed = address.Trim();

        if (trimmed.StartsWith("HR", StringComparison.OrdinalIgnoreCase))
        {
            var number = int.Parse(trimmed.AsSpan(2), CultureInfo.InvariantCulture);
            if (number is < 0 or > ushort.MaxValue)
            {
                throw new FormatException($"寄存器号超出范围（0~{ushort.MaxValue}）：{address}");
            }

            return (ushort)number;
        }

        if (trimmed.Length == 5 && trimmed[0] == '4' && int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var fieldNotation))
        {
            var offset = fieldNotation - 40001;
            if (offset < 0)
            {
                throw new FormatException($"4xxxx 地址必须在 40001~49999 之间：{address}");
            }

            return (ushort)offset;
        }

        throw new FormatException($"无法识别的 Modbus 地址（支持 HRn 或 40001~49999）：{address}");
    }
}
