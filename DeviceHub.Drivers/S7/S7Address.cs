using System.Globalization;

namespace DeviceHub.Drivers.S7;

/// <summary>S7 地址解析结果。</summary>
/// <param name="Db">DB 块编号。</param>
/// <param name="ByteOffset">字节偏移。</param>
/// <param name="Bit">位偏移，仅 Bool 类型地址有值。</param>
public readonly record struct S7Location(int Db, int ByteOffset, int? Bit);

/// <summary>
/// 解析 S7 绝对地址（对应 TIA 标准访问 DB 的绝对寻址写法）：
/// DB1.DBD0 → Real（双字）、DB1.DBW2 → Int（字）、DB1.DBX12.0 → Bool（位）。
/// </summary>
public static class S7Address
{
    public static S7Location Parse(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var segments = address.Split('.');
        if (segments is [{ } head, { } location, ..] && head.StartsWith("DB", StringComparison.OrdinalIgnoreCase))
        {
            var db = int.Parse(head.AsSpan(2), CultureInfo.InvariantCulture);

            if (location.Length >= 4)
            {
                var kind = location[..3].ToUpperInvariant();
                var number = int.Parse(location.AsSpan(3), CultureInfo.InvariantCulture);

                switch (kind)
                {
                    case "DBD":
                        return new S7Location(db, number, null);
                    case "DBW":
                        return new S7Location(db, number, null);
                    case "DBX" when segments.Length == 3:
                        var bit = int.Parse(segments[2], CultureInfo.InvariantCulture);
                        if (bit is < 0 or > 7)
                        {
                            throw new FormatException($"位偏移必须在 0~7 之间：{address}");
                        }

                        return new S7Location(db, number, bit);
                }
            }
        }

        throw new FormatException($"无法识别的 S7 地址（支持 DBn.DBDx / DBn.DBWx / DBn.DBXx.y）：{address}");
    }
}
