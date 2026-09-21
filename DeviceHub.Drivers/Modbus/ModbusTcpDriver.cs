using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;
using NModbus;

namespace DeviceHub.Drivers.Modbus;

/// <summary>
/// Modbus TCP 驱动（主站），对接任意 Modbus TCP 从站（Modbus Slave 软件、PLC、网关、自研模拟器）。
/// 协议逻辑（批量读窗口/编解码）在 ModbusDriverBase，本类只负责传输层：
/// TcpClient + MBAP 封装（NModbus 处理）与连接生命周期。
/// </summary>
public sealed class ModbusTcpDriver : ModbusDriverBase
{
    private readonly string _ip;
    private readonly int _port;
    private System.Net.Sockets.TcpClient? _tcpClient;

    public override string Name => "Modbus TCP";

    public override bool IsConnected => _tcpClient?.Connected == true;

    public ModbusTcpDriver(string ip, int port = 502, byte slaveId = 1)
        : base(slaveId)
    {
        _ip = ip;
        _port = port;
    }

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        _tcpClient = new System.Net.Sockets.TcpClient();
        await _tcpClient.ConnectAsync(_ip, _port, cancellationToken).ConfigureAwait(false);
        Master = new ModbusFactory().CreateMaster(_tcpClient);
        ApplyTimeouts(Master);
    }

    public override Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Master?.Dispose();
            _tcpClient?.Close();
        }
        catch
        {
            // 连接可能已失效（对端断开/超时），Close 抛错不影响"断开"这个结果
        }

        Master = null;
        _tcpClient = null;
        return Task.CompletedTask;
    }
}
