using System.Net;
using System.Net.Sockets;
using DeviceHub.Core.Models;
using DeviceHub.Northbound.OpcUa;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// OPC UA 北向服务端到端测试：进程内启动真服务器（随机端口），
/// 用官方 SDK 客户端走完整协议栈（发现端点/建会话/读节点）——
/// 与 S7/Modbus 模拟器测试同一哲学：真协议栈端到端，不 mock 传输。
/// </summary>
public class OpcUaNorthboundTests
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ApplicationConfiguration ClientConfiguration()
    {
        var config = new ApplicationConfiguration
        {
            ApplicationName = "DeviceHub.Tests.Client",
            ApplicationType = ApplicationType.Client,
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier(),
                AutoAcceptUntrustedCertificates = true,
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            TraceConfiguration = new TraceConfiguration(),
        };
        config.Validate(ApplicationType.Client);
        return config;
    }

    private static async Task<Session> ConnectAsync(ApplicationConfiguration config, int port)
    {
        var endpointDescription = await CoreClientUtils.SelectEndpointAsync(
            config, $"opc.tcp://127.0.0.1:{port}", useSecurity: false, 5000, null, CancellationToken.None)
            .ConfigureAwait(false);
        var endpoint = new ConfiguredEndpoint(
            null, endpointDescription, EndpointConfiguration.Create(config));
        return await Session.Create(
            config, endpoint, updateBeforeConnect: false, "DeviceHub.Tests",
            60000, new UserIdentity(new AnonymousIdentityToken()), preferredLocales: null)
            .ConfigureAwait(false);
    }

    /// <summary>读节点前先按命名空间 URI 找它的运行期索引（不猜 ns=2）。</summary>
    private static async Task<ushort> PointsNamespaceIndexAsync(Session session)
    {
        await session.FetchNamespaceTablesAsync(CancellationToken.None).ConfigureAwait(false);
        var uris = session.NamespaceUris.ToArray();
        var index = Array.IndexOf(uris, DeviceHubNodeManager.PointsNamespaceUri);
        Assert.True(index > 0, $"服务器命名空间数组里应包含 {DeviceHubNodeManager.PointsNamespaceUri}，实际：[{string.Join(", ", uris)}]");
        return (ushort)index;
    }

    /// <summary>原始 Read 服务读值：不因 Bad 状态码抛异常，把 StatusCode 原样带回（比 ReadValue 便捷方法更贴近协议语义）。</summary>
    private static DataValue ReadRaw(Session session, NodeId id)
    {
        var items = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = id, AttributeId = Attributes.Value },
        };
        session.Read(
            null, 0, TimestampsToReturn.Both, items,
            out var results, out _);
        return results[0];
    }

    [Fact]
    public async Task Server_ExposesPoints_ClientReadsValuesAndQuality()
    {
        var service = new OpcUaNorthboundService(GetFreePort());
        await service.StartAsync(CancellationToken.None);
        try
        {
            service.EnsurePoints(["温度", "压力"]);
            service.UpdateFrom(new PointValue("温度", 45.5, PointQuality.Good, DateTime.Now));
            service.UpdateFrom(new PointValue("压力", 0.42, PointQuality.Good, DateTime.Now));

            var config = ClientConfiguration();
            using var session = await ConnectAsync(config, service.Port).ConfigureAwait(false);
            var ns = await PointsNamespaceIndexAsync(session).ConfigureAwait(false);

            // 值与 Good 质量经完整协议栈读回
            var temperature = ReadRaw(session, new NodeId("温度", ns));
            Assert.Equal(45.5, (double)temperature.Value, 3);
            Assert.Equal(StatusCodes.Good, temperature.StatusCode.Code);

            // 质量戳北向兑现：断线周期喂 Bad，客户端读到的 StatusCode 跟着变
            service.UpdateFrom(new PointValue("温度", null, PointQuality.Bad, DateTime.Now));
            var bad = ReadRaw(session, new NodeId("温度", ns));
            Assert.Equal(StatusCodes.BadCommunicationError, bad.StatusCode.Code);

            // 未注册的点位：标准 BadNodeIdUnknown（客户端能拿到明确的协议层错误）
            var unknown = ReadRaw(session, new NodeId("不存在", ns));
            Assert.Equal(StatusCodes.BadNodeIdUnknown, unknown.StatusCode.Code);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task EnsurePoint_IsIdempotent_AndValueUpdateIgnoresUnknownPoints()
    {
        var service = new OpcUaNorthboundService(GetFreePort());
        await service.StartAsync(CancellationToken.None);
        try
        {
            service.EnsurePoints(["温度"]);
            service.EnsurePoints(["温度"]); // 重复建节点不应抛
            service.UpdateFrom(new PointValue("未知点位", 1.0, PointQuality.Good, DateTime.Now)); // 未建点位静默忽略
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await service.DisposeAsync();
        }
    }
}
