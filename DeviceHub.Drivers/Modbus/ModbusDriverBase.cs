using System.Buffers.Binary;
using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;
using DeviceHub.Drivers.S7;
using NModbus;

namespace DeviceHub.Drivers.Modbus;

/// <summary>
/// Modbus 驱动公共基类：地址解析、批量读窗口、寄存器编解码这些"协议层"逻辑
/// 与传输方式无关——RTU 和 TCP 的差别只在字节怎么搬（串口+CRC16 vs 以太网+MBAP），
/// 搬完之后的协议行为一模一样。新增 Modbus 变体（如 RTU over TCP）只需要补传输层。
/// </summary>
public abstract class ModbusDriverBase : IDeviceDriver
{
    /// <summary>从站地址（Unit ID）。TCP 走应用层字段，RTU 就是串行链路上的站号。</summary>
    protected readonly byte SlaveId;

    /// <summary>子类建立连接后设置，断开时必须置 null。</summary>
    protected IModbusMaster? Master;

    protected ModbusDriverBase(byte slaveId) => SlaveId = slaveId;

    public abstract string Name { get; }

    public abstract bool IsConnected { get; }

    public abstract Task ConnectAsync(CancellationToken cancellationToken = default);

    public abstract Task DisconnectAsync(CancellationToken cancellationToken = default);

    public async Task<IReadOnlyList<PointValue>> ReadAsync(
        IReadOnlyList<PointDefinition> points,
        CancellationToken cancellationToken = default)
    {
        var master = RequireMaster();

        var mapped = points
            .Select(p => (Point: p, Reg: ModbusAddress.Parse(p.Address)))
            .ToArray();
        var min = mapped.Min(m => m.Reg);
        var end = mapped.Max(m => m.Reg + RegistersOf(m.Point.DataType));
        if (end - min > 125)
        {
            throw new NotSupportedException("一次批量读最多覆盖 125 个保持寄存器（Modbus 协议上限）。");
        }

        var registers = await Task.Run(
            () => master.ReadHoldingRegisters(SlaveId, min, (ushort)(end - min)),
            cancellationToken).ConfigureAwait(false);

        var now = DateTime.Now;
        var results = new List<PointValue>(points.Count);
        foreach (var (point, register) in mapped)
        {
            var offset = register - min;
            results.Add(point.DataType switch
            {
                PointDataType.Bool => new PointValue(
                    point.Name, registers[offset] != 0 ? 1.0 : 0.0, PointQuality.Good, now),
                PointDataType.Int => new PointValue(
                    point.Name, unchecked((short)registers[offset]), PointQuality.Good, now),
                PointDataType.Real => new PointValue(
                    point.Name, ReadFloat(registers, offset), PointQuality.Good, now),
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
        var master = RequireMaster();
        var register = ModbusAddress.Parse(point.Address);

        await Task.Run(() =>
        {
            switch (point.DataType)
            {
                case PointDataType.Bool:
                    master.WriteSingleRegister(SlaveId, register, value != 0 ? (ushort)1 : (ushort)0);
                    break;
                case PointDataType.Int:
                    master.WriteSingleRegister(SlaveId, register, unchecked((ushort)(short)value));
                    break;
                case PointDataType.Real:
                    var words = FloatToWords((float)value);
                    master.WriteMultipleRegisters(SlaveId, register, new[] { words.Hi, words.Lo });
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

    /// <summary>未连接就抛出——与 S7 驱动等其它实现的行为保持一致。</summary>
    protected IModbusMaster RequireMaster() =>
        Master ?? throw new InvalidOperationException("驱动未连接，请先调用 ConnectAsync。");

    /// <summary>
    /// 传输层统一 2 秒超时：TCP 是网络往返超时，RTU 是字节间隔超时，
    /// 都必须有界——默认无限等会让采集线程永久挂起（本项目踩过的坑）。
    /// </summary>
    protected static void ApplyTimeouts(IModbusMaster master)
    {
        master.Transport.ReadTimeout = 2000;
        master.Transport.WriteTimeout = 2000;
    }

    /// <summary>寄存器占用数：Bool/Int 各 1 个，Real 2 个。</summary>
    private static int RegistersOf(PointDataType type) => type switch
    {
        PointDataType.Real => 2,
        _ => 1,
    };

    private static float ReadFloat(ushort[] registers, int offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(bytes[..2], registers[offset]);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.Slice(2, 2), registers[offset + 1]);
        return S7BitConverter.ToSingle(bytes);
    }

    private static (ushort Hi, ushort Lo) FloatToWords(float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        S7BitConverter.WriteSingle(bytes, value);
        return (
            BinaryPrimitives.ReadUInt16BigEndian(bytes[..2]),
            BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(2, 2)));
    }
}
