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
using NModbus.Serial;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Modbus RTU 测试分两层：
/// 1. 台架自检（总是运行）：NModbus 从站 ↔ NModbus 主站在进程内走 TCP 环回，
///    验证"从站注入数据 → 主站读出"的数据存储语义与浮点布局——这段从站
///    台架代码也是串口联调测试的从站侧，提前验证让串口测试只剩传输层一个变量。
/// 2. RTU 回环（需要一对真实串口）：两根 USB-TTL/485 转换器交叉对接（TX↔RX / A↔B）
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

    /// <summary>收尾守卫：取消/停监听时，NModbus 的 Accept 循环会以取消或套接字中止退出——都是正常收尾。</summary>
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
        // 从站占第一个口、主站驱动占第二个口——com0com 对内互联，等于一根虚拟 485 线
        var slavePort = new SerialPort(portNames[0], 9600, Parity.Even, 8, StopBits.One)
        {
            ReadTimeout = 2000,
            WriteTimeout = 2000,
        };
        slavePort.Open();
        var slaveNetwork = factory.CreateRtuSlaveNetwork(new SerialPortAdapter(slavePort));
        slaveNetwork.AddSlave(factory.CreateSlave(1, store));
        using var cts = new CancellationTokenSource();
        _ = ListenUntilCancelledAsync(slaveNetwork, cts.Token);

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
