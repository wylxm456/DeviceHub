using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;
using S7.Net;

namespace DeviceHub.Drivers.S7;

/// <summary>
/// 西门子 S7 驱动（支持 S7-300/400/1200/1500 及兼容仿真器）。
/// 连接参数由构造给出：真机/PLCSIM 按实际 CPU 型号、机架、槽位配置；
/// 自研模拟器（DeviceHub.Simulator）用 CpuType.S7300 + Rack 0 + Slot 2。
///
/// 批量读策略：把所有点位的最小覆盖区间用一次网络往返读回，再按地址切片解码——
/// 将 N 次往返合并为 1 次，这是采集软件吞吐的关键（500ms 周期内省出的时间就是裕量）。
/// 当前版本限制：一次批量读的所有点位须在同一 DB 块。
/// </summary>
public sealed class S7Driver : IDeviceDriver
{
    private readonly string _ip;
    private readonly short _rack;
    private readonly short _slot;
    private readonly CpuType _cpu;
    private Plc? _plc;

    public string Name => "Siemens S7";

    public bool IsConnected => _plc?.IsConnected == true;

    public S7Driver(string ip, short rack = 0, short slot = 2, CpuType cpu = CpuType.S7300)
    {
        _ip = ip;
        _rack = rack;
        _slot = slot;
        _cpu = cpu;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        _plc = new Plc(_cpu, _ip, _rack, _slot);
        await _plc.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _plc?.Close();
        }
        catch
        {
            // 连接可能已失效（对端断开/超时），Close 抛错不影响"断开"这个结果
        }

        _plc = null;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<PointValue>> ReadAsync(
        IReadOnlyList<PointDefinition> points,
        CancellationToken cancellationToken = default)
    {
        var plc = _plc ?? throw new InvalidOperationException("驱动未连接，请先调用 ConnectAsync。");

        var mapped = points
            .Select(p => (Point: p, Loc: S7Address.Parse(p.Address)))
            .ToArray();
        var db = mapped[0].Loc.Db;
        if (mapped.Any(m => m.Loc.Db != db))
        {
            throw new NotSupportedException("当前版本一次批量读只支持同一 DB 块的点位。");
        }

        var min = mapped.Min(m => m.Loc.ByteOffset);
        var end = mapped.Max(m => m.Loc.ByteOffset + SizeOf(m.Point.DataType));
        var buffer = await Task.Run(
            () => plc.ReadBytes(DataType.DataBlock, db, min, end - min),
            cancellationToken).ConfigureAwait(false);

        var now = DateTime.Now;
        var results = new List<PointValue>(points.Count);
        foreach (var (point, loc) in mapped)
        {
            var offset = loc.ByteOffset - min;
            results.Add(point.DataType switch
            {
                PointDataType.Real => new PointValue(
                    point.Name, S7BitConverter.ToSingle(buffer.AsSpan(offset, 4)), PointQuality.Good, now),
                PointDataType.Int => new PointValue(
                    point.Name, S7BitConverter.ToInt16(buffer.AsSpan(offset, 2)), PointQuality.Good, now),
                PointDataType.Bool => new PointValue(
                    point.Name,
                    S7BitConverter.ToBool(buffer[offset], loc.Bit ?? 0) ? 1.0 : 0.0,
                    PointQuality.Good, now),
                _ => new PointValue(point.Name, null, PointQuality.Bad, now),
            });
        }

        return results;
    }

    public async Task WriteAsync(
        PointDefinition point,
        double value,
        CancellationToken cancellationToken = default)
    {
        var plc = _plc ?? throw new InvalidOperationException("驱动未连接，请先调用 ConnectAsync。");
        var loc = S7Address.Parse(point.Address);

        await Task.Run(() =>
        {
            switch (point.DataType)
            {
                case PointDataType.Real:
                    plc.Write(DataType.DataBlock, loc.Db, loc.ByteOffset, (float)value);
                    break;
                case PointDataType.Int:
                    plc.Write(DataType.DataBlock, loc.Db, loc.ByteOffset, (short)value);
                    break;
                case PointDataType.Bool:
                    plc.Write(DataType.DataBlock, loc.Db, loc.ByteOffset, loc.Bit ?? 0, value != 0 ? 1 : 0);
                    break;
                default:
                    throw new NotSupportedException($"不支持写入的数据类型：{point.DataType}");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    private static int SizeOf(PointDataType type) => type switch
    {
        PointDataType.Bool => 1,
        PointDataType.Int => 2,
        PointDataType.Real => 4,
        _ => 1,
    };
}
