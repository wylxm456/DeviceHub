using DeviceHub.Core.Models;
using Microsoft.Extensions.Hosting;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace DeviceHub.Northbound.OpcUa;

/// <summary>
/// OPC UA 北向服务（Generic Host 托管）：把采集数据以 OPC UA 变量节点形式
/// 暴露给 MES/SCADA 订阅——设备采集页连什么设备、采哪些点位，北向就自动有什么。
///
/// 配置取舍（演示/内网定位）：
/// - 端点安全策略仅 None + 匿名令牌——省掉证书分发环节，UaExpert 直连即可看到数据；
///   生产环境应启用 SignAndEncrypt + 用户令牌，SDK 全面支持，只是要管证书；
/// - 应用证书不生成（None 策略下非必需）。
/// </summary>
public sealed class OpcUaNorthboundService : IHostedService, IAsyncDisposable
{
    private readonly int _port;
    private DeviceHubOpcUaServer? _server;

    /// <summary>监听端口（调用方选定；测试里用"临时占位再释放"拿空闲端口）。</summary>
    public int Port => _port;

    public OpcUaNorthboundService(int port) => _port = port;

    /// <summary>证书存储根（用户本地目录，测试与正式运行共用同一份生成的证书）。</summary>
    private static string PkiRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceHub", "opcua-pki");

    private static CertificateTrustList TrustedList(string name) => new()
    {
        StoreType = CertificateStoreType.Directory,
        StorePath = Path.Combine(PkiRoot, name),
    };

    public bool IsRunning => _server is not null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            return;
        }

        var configuration = new ApplicationConfiguration
        {
            ApplicationName = "DeviceHub",
            ApplicationUri = Utils.Format("urn:{0}:DeviceHub", System.Net.Dns.GetHostName()),
            ApplicationType = ApplicationType.Server,
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { $"opc.tcp://127.0.0.1:{_port}" },
                SecurityPolicies =
                {
                    new ServerSecurityPolicy
                    {
                        SecurityMode = MessageSecurityMode.None,
                        SecurityPolicyUri = SecurityPolicies.None,
                    },
                },
                UserTokenPolicies = { new UserTokenPolicy(UserTokenType.Anonymous) },
                MaxSessionCount = 20,
                MaxSubscriptionCount = 100,
                MinSubscriptionLifetime = 5,
                DiagnosticsEnabled = false,
            },
            SecurityConfiguration = new SecurityConfiguration
            {
                // 自签名应用证书：None 策略下 CreateSession 也要出示（实测）；
                // 持久化在用户本地目录，首次生成后复用
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(PkiRoot, "own"),
                    SubjectName = "CN=DeviceHub",
                },
                TrustedIssuerCertificates = TrustedList("issuer"),
                TrustedPeerCertificates = TrustedList("trusted"),
                RejectedCertificateStore = TrustedList("rejected"),
                AutoAcceptUntrustedCertificates = true,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            TraceConfiguration = new TraceConfiguration(),
        };
        configuration.Validate(ApplicationType.Server);

        // 1.5.x 的公开启动入口：ApplicationInstance 持配置驱动 ServerBase 的完整启动流程。
        // 即便端点是 None 安全策略，CreateSession 阶段服务器仍要出示应用实例证书——
        // 不生成证书会话握手会被拒（BadUnexpectedError，实测）；首次自动生成自签名证书
        var application = new ApplicationInstance
        {
            ApplicationName = "DeviceHub",
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = configuration,
        };
        await application.CheckApplicationInstanceCertificatesAsync(
            silent: true, lifeTimeInMonths: null, cancellationToken).ConfigureAwait(false);

        var server = new DeviceHubOpcUaServer();
        await application.StartAsync(server).ConfigureAwait(false);
        _server = server;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is { } server)
        {
            await server.StopAsync(cancellationToken).ConfigureAwait(false);
            _server = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        (_server as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>按点位表建节点（采集开始时调用）。</summary>
    public void EnsurePoints(IEnumerable<string> pointNames)
    {
        if (_server is null)
        {
            return;
        }

        foreach (var name in pointNames)
        {
            _server.Nodes.EnsurePoint(name);
        }
    }

    /// <summary>喂一帧读数（值+质量）进北向节点。</summary>
    public void UpdateFrom(PointValue value)
    {
        if (_server is null)
        {
            return;
        }

        _server.Nodes.UpdateValue(value.Name, value.Value, value.Quality == PointQuality.Good);
    }
}
