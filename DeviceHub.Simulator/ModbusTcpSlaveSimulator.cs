using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DeviceHub.Drivers.S7;

namespace DeviceHub.Simulator;

/// <summary>
/// 迷你 Modbus TCP 从站模拟器：仅实现本框架用到的最小协议集——
/// FC03 读保持寄存器、FC06 写单寄存器、FC16 写多寄存器。
/// 用途：单元/集成测试中替代 Modbus Slave 软件，让"真实 Modbus 协议栈"
/// 的端到端测试不依赖任何外部程序；也是 M4"内置模拟器"的雏形。
///
/// 帧结构：MBAP 头（事务号 2B + 协议号 2B + 长度 2B + 从站号 1B）+ PDU。
/// 浮点布局与 ModbusTcpDriver 相同：高字在前、字内大端（ABCD）。
/// </summary>
public sealed class ModbusTcpSlaveSimulator : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    /// <summary>保持寄存器区（测试可直接注入初值或读回验证）。</summary>
    public ushort[] HoldingRegisters { get; }

    /// <summary>实际监听端口（构造时传 0 则由系统分配，Start 后可读）。</summary>
    public int Port { get; private set; }

    public bool IsRunning { get; private set; }

    public ModbusTcpSlaveSimulator(ushort registerCount = 32, int port = 0)
    {
        HoldingRegisters = new ushort[registerCount];
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        IsRunning = true;
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            var header = new byte[7];
            while (!ct.IsCancellationRequested)
            {
                // MBAP 头 7 字节：事务号(2) + 协议号(2) + 长度(2) + 从站号(1)
                if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
                {
                    break;
                }

                var remaining = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2)) - 1;
                if (remaining is < 1 or > 253)
                {
                    break;
                }

                var pdu = new byte[remaining];
                if (!await ReadExactAsync(stream, pdu, ct).ConfigureAwait(false))
                {
                    break;
                }

                var response = BuildResponse(header, pdu);
                await stream.WriteAsync(response, ct).ConfigureAwait(false);
            }
        }
    }

    private byte[] BuildResponse(byte[] header, byte[] pdu)
    {
        var functionCode = pdu[0];
        lock (_gate)
        {
            return functionCode switch
            {
                3 => ReadHoldingRegisters(header, pdu),
                6 => WriteSingleRegister(header, pdu),
                16 => WriteMultipleRegisters(header, pdu),
                _ => ExceptionResponse(header, pdu[0], 1), // 非法功能
            };
        }
    }

    private byte[] ReadHoldingRegisters(byte[] header, byte[] pdu)
    {
        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));

        if (start + count > HoldingRegisters.Length)
        {
            return ExceptionResponse(header, pdu[0], 2); // 非法数据地址
        }

        var response = new byte[7 + 2 + 1 + count * 2];
        Array.Copy(header, response, 7);
        response[7] = 3;
        response[8] = (byte)(count * 2);
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9 + i * 2, 2), HoldingRegisters[start + i]);
        }

        UpdateLength(response);
        return response;
    }

    private byte[] WriteSingleRegister(byte[] header, byte[] pdu)
    {
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
        var value = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));

        if (address >= HoldingRegisters.Length)
        {
            return ExceptionResponse(header, pdu[0], 2);
        }

        HoldingRegisters[address] = value;

        // FC6 的正常响应 = 请求原样回显
        var response = new byte[7 + pdu.Length];
        Array.Copy(header, response, 7);
        pdu.CopyTo(response, 7);
        return response;
    }

    private byte[] WriteMultipleRegisters(byte[] header, byte[] pdu)
    {
        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
        var byteCount = pdu[5];

        if (start + count > HoldingRegisters.Length || byteCount != count * 2)
        {
            return ExceptionResponse(header, pdu[0], 2);
        }

        for (var i = 0; i < count; i++)
        {
            HoldingRegisters[start + i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + i * 2, 2));
        }

        // FC16 正常响应：回显事务头 + FC + 起始地址 + 数量
        // 注意：MBAP 长度字段必须按"响应"重算，不能沿用请求头（请求 PDU 比 5 字节长，
        // 长度不符会让主站无限等待根本不会到达的字节）
        var response = new byte[7 + 5];
        Array.Copy(header, response, 7);
        pdu.AsSpan(0, 5).CopyTo(response.AsSpan(7));
        UpdateLength(response);
        return response;
    }

    /// <summary>精确读满 buffer.Length 字节；对端断开返回 false。</summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private static byte[] ExceptionResponse(byte[] header, byte functionCode, byte code)
    {
        var response = new byte[9];
        Array.Copy(header, response, 7);
        response[7] = (byte)(functionCode | 0x80);
        response[8] = code;
        UpdateLength(response);
        return response;
    }

    private static void UpdateLength(byte[] response)
    {
        var pduLength = response.Length - 6;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), (ushort)pduLength);
    }

    /// <summary>按 ABCD 序写入一个浮点（高字在前、字内大端），与驱动的 Real 布局一致。</summary>
    public void WriteFloat(int register, float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        S7BitConverter.WriteSingle(bytes, value);
        lock (_gate)
        {
            HoldingRegisters[register] = BinaryPrimitives.ReadUInt16BigEndian(bytes[..2]);
            HoldingRegisters[register + 1] = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(2, 2));
        }
    }

    /// <summary>按 ABCD 序读出一个浮点（测试断言用）。</summary>
    public float ReadFloat(int register)
    {
        lock (_gate)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(bytes[..2], HoldingRegisters[register]);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.Slice(2, 2), HoldingRegisters[register + 1]);
            return S7BitConverter.ToSingle(bytes);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        IsRunning = false;
    }
}
