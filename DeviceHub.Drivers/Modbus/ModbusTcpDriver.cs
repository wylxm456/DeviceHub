using System.Buffers.Binary;
using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;
using DeviceHub.Drivers.S7;
using NModbus;

namespace DeviceHub.Drivers.Modbus;

/// <summary>
/// Modbus TCP 驱动（主站），对接任意 Modbus TCP 从站（Modbus Slave 软件、PLC、网关、自研模拟器）。
///
/// 地址约定（保持寄存器 FC03/FC06/FC16）：
///   Bool → 1 个寄存器（非 0 即真）；Int → 1 个寄存器（带符号）；
///   Real → 2 个连续寄存器，高字在前、字内大端（ABCD 序，工业默认，与 S7 Real 一致，
///   因此浮点转换直接复用 S7BitConverter）。
///
/// 批量读策略与 S7 驱动一致：所有点位取最小覆盖寄存器区间，一次 FC03 往返读回再切片——
/// Modbus 一次事务最多读 125 个寄存器，500ms 周期下这个策略的裕量更大。
/// </summary>
public sealed class ModbusTcpDriver : IDeviceDriver
{
    private readonly string _ip;
    private readonly int _port;
    private readonly byte _slaveId;
    private IModbusMaster? _master;
    private System.Net.Sockets.TcpClient? _tcpClient;

    public string Name => "Modbus TCP";

    public bool IsConnected => _tcpClient?.Connected == true;

    public ModbusTcpDriver(string ip, int port = 502, byte slaveId = 1)
    {
        _ip = ip;
        _port = port;
        _slaveId = slaveId;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        _tcpClient = new System.Net.Sockets.TcpClient();
        await _tcpClient.ConnectAsync(_ip, _port, cancellationToken).ConfigureAwait(false);
        _master = new ModbusFactory().CreateMaster(_tcpClient);

        // 默认读超时是无限等待：从站掉线/响应缺字节会让采集线程永久挂起。
        // 2 秒足够覆盖正常往返，超时抛出的异常交给引擎按"本周期读取失败"处理。
        _master.Transport.ReadTimeout = 2000;
        _master.Transport.WriteTimeout = 2000;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _master?.Dispose();
            _tcpClient?.Close();
        }
        catch
        {
            // 连接可能已失效（对端断开/超时），Close 抛错不影响"断开"这个结果
        }

        _master = null;
        _tcpClient = null;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<PointValue>> ReadAsync(
        IReadOnlyList<PointDefinition> points,
        CancellationToken cancellationToken = default)
    {
        var master = _master ?? throw new InvalidOperationException("驱动未连接，请先调用 ConnectAsync。");

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
            () => master.ReadHoldingRegisters(_slaveId, min, (ushort)(end - min)),
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
        var master = _master ?? throw new InvalidOperationException("驱动未连接，请先调用 ConnectAsync。");
        var register = ModbusAddress.Parse(point.Address);

        await Task.Run(() =>
        {
            switch (point.DataType)
            {
                case PointDataType.Bool:
                    master.WriteSingleRegister(_slaveId, register, value != 0 ? (ushort)1 : (ushort)0);
                    break;
                case PointDataType.Int:
                    master.WriteSingleRegister(_slaveId, register, unchecked((ushort)(short)value));
                    break;
                case PointDataType.Real:
                    var words = FloatToWords((float)value);
                    master.WriteMultipleRegisters(_slaveId, register, new[] { words.Hi, words.Lo });
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
