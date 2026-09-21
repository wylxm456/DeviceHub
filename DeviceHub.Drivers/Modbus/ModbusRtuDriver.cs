using System.IO.Ports;
using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;
using NModbus;
using NModbus.Serial;

namespace DeviceHub.Drivers.Modbus;

/// <summary>
/// Modbus RTU 驱动（主站）：串行链路（RS-485/RS-232）上的 Modbus，
/// 是工业现场仪表/变频器最通用的组合。与 TCP 版共享全部协议逻辑
/// （ModbusDriverBase），差别只在传输层——串口字节流，帧尾由 NModbus
/// 传输层自动附加重校验 CRC16。
///
/// 典型接线：上位机 USB 转 485 → 从站 A/B 端子（半双工一对差分线，站号拨码区分）。
/// 串口参数默认 9600/8/Even/1（Modbus 串行链路规范的惯例默认）。
/// </summary>
public sealed class ModbusRtuDriver : ModbusDriverBase
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly Parity _parity;
    private readonly int _dataBits;
    private readonly StopBits _stopBits;
    private SerialPort? _serialPort;

    public override string Name => "Modbus RTU";

    public override bool IsConnected => _serialPort?.IsOpen == true;

    public ModbusRtuDriver(
        string portName,
        int baudRate,
        Parity parity,
        int dataBits,
        StopBits stopBits,
        byte slaveId)
        : base(slaveId)
    {
        _portName = portName;
        _baudRate = baudRate;
        _parity = parity;
        _dataBits = dataBits;
        _stopBits = stopBits;
    }

    public override Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return Task.CompletedTask;
        }

        // 串口打开是阻塞调用，与 TCP 版一致放到线程池，不占调用方（UI）线程
        return Task.Run(() =>
        {
            var port = new SerialPort(_portName, _baudRate, _parity, _dataBits, _stopBits)
            {
                // 串口自身超时 + NModbus 传输层超时（ApplyTimeouts）双重保险：
                // 从站不回话必须在 2 秒内报错，交给引擎按"本周期读取失败"处理
                ReadTimeout = 2000,
                WriteTimeout = 2000,
            };
            port.Open();
            _serialPort = port;
            Master = new ModbusFactory().CreateRtuMaster(new SerialPortAdapter(port));
            ApplyTimeouts(Master);
        }, cancellationToken);
    }

    public override Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Master?.Dispose();
            _serialPort?.Close();
        }
        catch
        {
            // 端口可能已被拔掉，Close 抛错不影响"断开"这个结果
        }

        Master = null;
        _serialPort = null;
        return Task.CompletedTask;
    }
}
