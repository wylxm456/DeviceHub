using Opc.Ua;
using Opc.Ua.Server;

namespace DeviceHub.Northbound.OpcUa;

/// <summary>
/// 节点管理器：在 OPC UA 地址空间里维护 "DeviceHub" 文件夹，
/// 每个采集点位一个 Double 变量节点（NodeId = ns:{NamespaceIndex};s:{点位名}）。
///
/// 两个设计点：
/// 1. 质量戳的第三次兑现——PointQuality 映射为 OPC UA StatusCode
///    （Good / BadCommunicationError / BadWaitingForInitialData），北向客户端订阅到的
///    每个值自带可信度，与界面表格、趋势曲线同一套哲学：读不到是信息，不是 0；
/// 2. 节点动态创建——点位表在采集开始时才知道（配置驱动、多设备切换），
///    EnsurePoint 在运行期把节点挂到根文件夹的通知链上，客户端即可发现并订阅。
/// </summary>
public sealed class DeviceHubNodeManager : CustomNodeManager2
{
    /// <summary>本服务器的命名空间 URI（客户端按它找 NamespaceIndex）。</summary>
    public const string PointsNamespaceUri = "http://devicehub.local/points/";

    private readonly object _updateGate = new();
    private readonly Dictionary<string, BaseDataVariableState<double>> _nodes = new();
    private FolderState? _root;

    public DeviceHubNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration, PointsNamespaceUri)
    {
    }

    /// <summary>地址空间根（Objects/DeviceHub）。在服务器启动流程里被调用。</summary>
    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
            {
                references = new List<IReference>();
                externalReferences[ObjectIds.ObjectsFolder] = references;
            }

            var root = new FolderState(null)
            {
                NodeId = new NodeId("DeviceHub", NamespaceIndex),
                BrowseName = new QualifiedName("DeviceHub", NamespaceIndex),
                DisplayName = new LocalizedText("DeviceHub"),
                ReferenceTypeId = ReferenceTypeIds.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                EventNotifier = EventNotifiers.SubscribeToEvents,
            };

            // 把根文件夹挂到 OPC UA 标准的 Objects 文件夹下（客户端浏览的起点）
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, root.NodeId));
            AddRootNotifier(root);
            _root = root;
        }
    }

    /// <summary>确保点位节点存在（采集开始时按点位表批量建）。幂等。</summary>
    public void EnsurePoint(string name)
    {
        lock (Lock)
        {
            if (_nodes.ContainsKey(name) || _root is null)
            {
                return;
            }

            var node = new BaseDataVariableState<double>(_root)
            {
                NodeId = new NodeId(name, NamespaceIndex),
                BrowseName = new QualifiedName(name, NamespaceIndex),
                DisplayName = new LocalizedText(name),
                ReferenceTypeId = ReferenceTypeIds.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                DataType = DataTypeIds.Double,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentRead, // 北向只读：MES/SCADA 来订阅，不反向写
                Historizing = false,
                StatusCode = StatusCodes.BadWaitingForInitialData, // 建好但还没数据
                Timestamp = DateTime.UtcNow,
            };
            _root.AddChild(node);
            _nodes[name] = node;

            // 运行期注册节点的标准入口：进节点字典后按 NodeId 的读取/订阅才能解析到它
            // （名字带 Predefined 是历史包袱，SDK 参考服务器动态建节点也走它）
            AddPredefinedNode(SystemContext, node);
            node.ClearChangeMasks(SystemContext, true);
        }
    }

    /// <summary>更新一个点位节点的值与质量（采集流每读到一帧调一次）。</summary>
    public void UpdateValue(string name, double? value, bool good)
    {
        BaseDataVariableState<double>? node;
        lock (Lock)
        {
            if (!_nodes.TryGetValue(name, out node))
            {
                return;
            }
        }

        lock (_updateGate)
        {
            if (value is { } v)
            {
                node.Value = v;
            }

            node.StatusCode = good ? StatusCodes.Good : StatusCodes.BadCommunicationError;
            node.Timestamp = DateTime.UtcNow;
            node.ClearChangeMasks(SystemContext, false);
        }
    }
}
