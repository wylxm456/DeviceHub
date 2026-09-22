using Opc.Ua;
using Opc.Ua.Server;

namespace DeviceHub.Northbound.OpcUa;

/// <summary>
/// DeviceHub 的 OPC UA 服务器：StandardServer 的最小定制——
/// 只换节点管理器（地址空间内容），安全/会话/订阅等服务器基础设施全用官方实现。
/// </summary>
public sealed class DeviceHubOpcUaServer : StandardServer
{
    private DeviceHubNodeManager? _nodeManager;

    /// <summary>地址空间访问入口（挂点位节点、更新值都走它）。</summary>
    public DeviceHubNodeManager Nodes =>
        _nodeManager ?? throw new InvalidOperationException("服务器尚未启动。");

    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        _nodeManager = new DeviceHubNodeManager(server, configuration);
        return new MasterNodeManager(server, configuration, "DeviceHub", new[] { _nodeManager });
    }
}
