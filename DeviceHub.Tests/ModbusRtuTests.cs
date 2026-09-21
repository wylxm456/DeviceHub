using System.Buffers.Binary;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using DeviceHub.Core.Configuration;
using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;
using DeviceHub.Drivers;
using DeviceHub.Drivers.Modbus;
using DeviceHub.Drivers.S7;
using NModbus;
using NModbus.Data;
using NModbus.IO;
using NModbus.Serial;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Modbus RTU 测试分三层，从"零依赖"到"最接近真机"：
/// 1. 台架自检（常开）：NModbus 从站 ↔ NModbus 主站在进程内走 TCP 环回，
///    验证"从站注入数据 → 主站读出"的数据存储语义与浮点布局——这段从站
///    台架代码也是后面两层联调的从站侧，提前验证让联调只剩传输层一个变量。
/// 2. 虚拟串口线回环（常开）：把 RTU 驱动的传输层换成内存线，与 RTU 从站
///    进程内对跑——帧定界、CRC16 校验全走真协议栈，但不经过操作系统串口栈，
///    零驱动零安装。驱动侧仅覆写"打开传输层"这一个缝（OpenTransport），
///    协议逻辑与真实串口完全共用。
/// 3. RTU 回环（需要一对真实串口）：两根 USB-TTL/485 转换器交叉对接（TX↔RX / A↔B）
///    后自动运行；没有串口的机器直接跳过。注：com0com 虚拟串口驱动在 Win11 上
///    因旧式交叉签名证书被吊销而无法加载（内核错误 577），虚拟口路线在本机走不通。
/// </summary>
public class ModbusRtuTests
{
    private const float TestTemperature = 26.5f;
    private const float TestPressure = 0.4f;

    /// <summary>按 ABCD 序（高字在前、字内大端）把浮点注入从站保持寄存器——与驱动解码约定一致。
    /// 注意 IPointSource 是"起点+数组"批量写语义，没有逐点索引器。</summary>
    private static void InjectFloat(ISlaveDataStore store, int register, float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        S7BitConverter.WriteSingle(bytes, value);
        store.HoldingRegisters.WritePoints(
            (ushort)register,
            [
                BinaryPrimitives.ReadUInt16BigEndian(bytes[..2]),
                BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(2, 2)),
            ]);
    }

    /// <summary>收尾守卫：取消/停监听时，NModbus 的 Accept/读取循环会以取消或套接字中止退出——都是正常收尾。
    /// 注意：RTU 从站的 ListenAsync 内部是同步阻塞读（同步 IStreamResource），调用方必须把它
    /// 扔到线程池（Task.Run），否则调用线程会被堵死在第一次读取里——本测试踩过，转储实锤。</summary>
    private static async Task ListenUntilCancelledAsync(IModbusSlaveNetwork network, CancellationToken ct)
    {
        try
        {
            await network.ListenAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
    }

    [Fact]
    public async Task SlaveHarness_OverTcpLoopback_PreservesFloatLayout()
    {
        var store = new DefaultSlaveDataStore();
        InjectFloat(store, 0, TestTemperature);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var factory = new ModbusFactory();
        var slaveNetwork = factory.CreateSlaveNetwork(listener);
        slaveNetwork.AddSlave(factory.CreateSlave(1, store));
        using var cts = new CancellationTokenSource();
        var listenTask = ListenUntilCancelledAsync(slaveNetwork, cts.Token);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var master = factory.CreateMaster(client);
            master.Transport.ReadTimeout = 2000;
            master.Transport.WriteTimeout = 2000;

            var registers = await Task.Run(() => master.ReadHoldingRegisters(1, 0, 2));

            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(bytes[..2], registers[0]);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.Slice(2, 2), registers[1]);
            Assert.Equal(TestTemperature, S7BitConverter.ToSingle(bytes), 3);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
        }
    }

    /// <summary>
    /// 全链路 RTU 协议回环（零依赖，常开）：我们的 RTU 驱动 ↔ NModbus RTU 从站，
    /// 中间是内存虚拟串口线。帧定界 + CRC16 校验全走真协议栈——
    /// 与真实串口环境的唯一差别是字节来自内存而非 UART 芯片。
    /// </summary>
    [Fact]
    public async Task RtuDriver_OverVirtualWire_CompletesFullProtocolLoop()
    {
        var store = new DefaultSlaveDataStore();
        InjectFloat(store, 0, TestTemperature);
        InjectFloat(store, 2, TestPressure);

        using var wire = new VirtualSerialWire();
        var factory = new ModbusFactory();
        var slaveNetwork = factory.CreateRtuSlaveNetwork(wire.SlaveEnd);
        slaveNetwork.AddSlave(factory.CreateSlave(1, store));
        using var cts = new CancellationTokenSource();
        // ListenAsync 会同步阻塞在读上，必须在线程池上跑——详见方法注释
        _ = Task.Run(() => ListenUntilCancelledAsync(slaveNetwork, cts.Token));

        var driver = new VirtualWireRtuDriver(wire.MasterEnd, slaveId: 1);
        try
        {
            await driver.ConnectAsync();

            var values = await driver.ReadAsync(
            [
                new PointDefinition("温度", "40001", PointDataType.Real),
                new PointDefinition("压力", "40003", PointDataType.Real),
            ]);

            Assert.Equal(PointQuality.Good, values[0].Quality);
            Assert.Equal((double)TestTemperature, values[0].Value!.Value, 3);
            Assert.Equal((double)TestPressure, values[1].Value!.Value, 3);
        }
        finally
        {
            await driver.DisposeAsync();
            cts.Cancel();
        }
    }

    /// <summary>把 RTU 驱动的传输层换成虚拟串口线——驱动协议栈零改动，只换字节来源。</summary>
    private sealed class VirtualWireRtuDriver : ModbusRtuDriver
    {
        private readonly IStreamResource _wireEnd;

        public VirtualWireRtuDriver(IStreamResource wireEnd, byte slaveId)
            : base("VIRTUAL", 9600, Parity.None, 8, StopBits.One, slaveId)
        {
            _wireEnd = wireEnd;
        }

        protected override IModbusMaster OpenTransport() =>
            new ModbusFactory().CreateRtuMaster(_wireEnd);

        protected override bool IsTransportAlive() => true;
    }

    /// <summary>
    /// 内存里的一对交叉单工缓冲，等效一根零延迟虚拟串口线：
    /// 一端写入的字节原样出现在另一端的读出侧。
    /// </summary>
    private sealed class VirtualSerialWire : IDisposable
    {
        private readonly SimplexBuffer _masterToSlave = new();
        private readonly SimplexBuffer _slaveToMaster = new();

        public IStreamResource MasterEnd { get; }
        public IStreamResource SlaveEnd { get; }

        public VirtualSerialWire()
        {
            MasterEnd = new WireEnd(readFrom: _slaveToMaster, writeTo: _masterToSlave);
            SlaveEnd = new WireEnd(readFrom: _masterToSlave, writeTo: _slaveToMaster);
        }

        public void Dispose()
        {
            MasterEnd.Dispose();
            SlaveEnd.Dispose();
        }

        /// <summary>一端的 IStreamResource 视图：从自己的收线读、往对方的收线写。</summary>
        private sealed class WireEnd : IStreamResource
        {
            private readonly SimplexBuffer _readFrom;
            private readonly SimplexBuffer _writeTo;

            public WireEnd(SimplexBuffer readFrom, SimplexBuffer writeTo)
            {
                _readFrom = readFrom;
                _writeTo = writeTo;
            }

            public int InfiniteTimeout => Timeout.Infinite;

            public int ReadTimeout { get; set; } = Timeout.Infinite;

            public int WriteTimeout { get; set; } = Timeout.Infinite;

            public int Read(byte[] buffer, int offset, int count) =>
                _readFrom.Read(buffer, offset, count, CancellationToken.None);

            public void Write(byte[] buffer, int offset, int count) =>
                _writeTo.Write(buffer, offset, count);

            public void DiscardInBuffer() => _readFrom.Clear();

            public void Dispose() => _readFrom.Clear();
        }
    }

    /// <summary>单工字节缓冲：一端整块写入，另一端按需精确读取任意字节数。</summary>
    private sealed class SimplexBuffer
    {
        private readonly object _gate = new();
        private readonly MemoryStream _buffer = new();
        private readonly SemaphoreSlim _chunkArrived = new(0);

        public void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                _buffer.Write(buffer, offset, count);
            }

            _chunkArrived.Release();
        }

        public int Read(byte[] dest, int offset, int count, CancellationToken ct)
        {
            // 内存线零延迟，不做读超时——联调用不到
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_buffer.Length > 0)
                    {
                        break;
                    }
                }

                _chunkArrived.Wait(ct);
            }

            lock (_gate)
            {
                var take = (int)Math.Min(count, _buffer.Length);
                var raw = _buffer.GetBuffer();
                Array.Copy(raw, 0, dest, offset, take);
                Array.Copy(raw, take, raw, 0, (int)_buffer.Length - take);
                _buffer.SetLength(_buffer.Length - take);
                return take;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _buffer.SetLength(0);
            }
        }
    }

    [Fact]
    public async Task RtuDriver_OverSerialPair_ReadsFloatFromRtuSlave()
    {
        var portNames = SerialPort.GetPortNames()
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (portNames.Length < 2)
        {
            // 没有成对串口：跳过（接一对真实串口后本测试自动生效）
            return;
        }

        var store = new DefaultSlaveDataStore();
        InjectFloat(store, 0, TestTemperature);
        InjectFloat(store, 2, TestPressure);

        var factory = new ModbusFactory();
        // 从站占第一个口、主站驱动占第二个口——两口交叉对接，等于一根 485 线
        var slavePort = new SerialPort(portNames[0], 9600, Parity.Even, 8, StopBits.One)
        {
            ReadTimeout = 2000,
            WriteTimeout = 2000,
        };
        slavePort.Open();
        var slaveNetwork = factory.CreateRtuSlaveNetwork(new SerialPortAdapter(slavePort));
        slaveNetwork.AddSlave(factory.CreateSlave(1, store));
        using var cts = new CancellationTokenSource();
        // ListenAsync 会同步阻塞在读上，必须在线程池上跑——详见方法注释
        _ = Task.Run(() => ListenUntilCancelledAsync(slaveNetwork, cts.Token));

        var driver = new ModbusRtuDriver(portNames[1], 9600, Parity.Even, 8, StopBits.One, slaveId: 1);
        try
        {
            await driver.ConnectAsync();

            var values = await driver.ReadAsync(
            [
                new PointDefinition("温度", "40001", PointDataType.Real),
                new PointDefinition("压力", "40003", PointDataType.Real),
            ]);

            Assert.Equal(PointQuality.Good, values[0].Quality);
            Assert.Equal((double)TestTemperature, values[0].Value!.Value, 3);
            Assert.Equal((double)TestPressure, values[1].Value!.Value, 3);
        }
        finally
        {
            await driver.DisposeAsync();
            cts.Cancel();
            slavePort.Close();
        }
    }
}

/// <summary>Modbus RTU 驱动工厂接线：配置字段解析与缺失校验。</summary>
public class ModbusRtuFactoryTests
{
    [Fact]
    public void Create_ModbusRtu_ShouldReturnRtuDriver()
    {
        var config = new DeviceConfig
        {
            Name = "RTU从站",
            DriverType = "modbusrtu", // 大小写不敏感
            SerialPort = "COM3",
        };

        var driver = DeviceDriverFactory.Create(config);

        Assert.IsType<ModbusRtuDriver>(driver);
    }

    [Fact]
    public void Create_ModbusRtuWithoutSerialPort_ShouldThrowArgument()
    {
        var config = new DeviceConfig { Name = "缺串口的RTU", DriverType = "ModbusRtu" };

        Assert.Throws<ArgumentException>(() => DeviceDriverFactory.Create(config));
    }

    [Fact]
    public void Create_ModbusRtuWithBadParity_ShouldThrowFormat()
    {
        var config = new DeviceConfig
        {
            Name = "怪校验位",
            DriverType = "ModbusRtu",
            SerialPort = "COM3",
            Parity = "Mark", // 不支持的支持项
        };

        Assert.Throws<FormatException>(() => DeviceDriverFactory.Create(config));
    }

    [Fact]
    public void Create_ModbusRtuWithBadStopBits_ShouldThrowFormat()
    {
        var config = new DeviceConfig
        {
            Name = "怪停止位",
            DriverType = "ModbusRtu",
            SerialPort = "COM3",
            StopBits = "Three",
        };

        Assert.Throws<FormatException>(() => DeviceDriverFactory.Create(config));
    }
}
