using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using DeviceHub.Core.Models;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace DeviceHub.Northbound.Mqtt;

/// <summary>
/// MQTT 北向服务：把采集数据以 JSON 推上消息总线——与 OPC UA 同为北向，
/// 角色不同：OPC UA 是"别人来订阅我们的服务器"，MQTT 是"我们主动推给总线"。
///
/// 自包含演示设计：进程内起嵌入式 Broker（MQTTnet.Server），发布客户端环回连接——
/// 不依赖任何外部软件，MQTTX 连 127.0.0.1:1883 即可看到数据；
/// 生产环境把这里换成指向公司总线（EMQX/Mosquitto）的连接参数即可，发布逻辑不变。
///
/// 三个工程实践：
/// 1. 遗嘱消息（LWT）——连接注册 will（status=offline，保留），连接成功即发
///    status=online（保留）；进程崩溃/断网时 Broker 自动代发 offline，
///    订阅方无需心跳就能感知采集端死活；正常停机则主动发 offline
///    （MQTT 规范：优雅断开不触发遗嘱——两路互补，任何死法都有通告）；
/// 2. 点位消息保留（retain）——晚到的订阅者立即拿到最后值，不用等下一帧；
/// 3. 发布走单读队列 + 后台泵——UI 线程只写 Channel，发布有序且不阻塞界面。
/// </summary>
public sealed class MqttNorthboundService : IHostedService, IAsyncDisposable
{
    public const string PointsTopicPrefix = "devicehub/points/";
    public const string StatusTopic = "devicehub/status";

    /// <summary>中文点位名直接进 JSON 载荷，关闭默认的 \uXXXX 转义（MQTTX 里要看原文）。</summary>
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly int _port;
    private readonly Channel<PointValue> _queue = Channel.CreateUnbounded<PointValue>(
        new UnboundedChannelOptions { SingleReader = true });

    private MqttServer? _broker;
    private IMqttClient? _publisher;
    private CancellationTokenSource? _pumpCts;
    private Task? _pump;

    public MqttNorthboundService(int port) => _port = port;

    public int Port => _port;

    public bool IsRunning => _publisher?.IsConnected == true;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_broker is not null)
        {
            return;
        }

        // 1. 嵌入式 Broker（演示自包含；生产改连外部总线）
        var brokerOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(_port)
            .Build();
        _broker = new MqttServerFactory().CreateMqttServer(brokerOptions);
        await _broker.StartAsync().ConfigureAwait(false);

        // 2. 发布客户端：遗嘱=offline（保留），连上后主动发 online（保留）
        var publisher = new MqttClientFactory().CreateMqttClient();
        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _port)
            .WithClientId("devicehub-publisher")
            .WithWillTopic(StatusTopic)
            .WithWillPayload("offline")
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await publisher.ConnectAsync(clientOptions, cancellationToken).ConfigureAwait(false);
        await publisher.PublishStringAsync(
            StatusTopic, "online",
            MqttQualityOfServiceLevel.AtLeastOnce, retain: true, cancellationToken)
            .ConfigureAwait(false);
        _publisher = publisher;

        // 3. 发布泵：单读者顺序消费，UI 线程只写队列
        _pumpCts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_pumpCts is { } cts)
        {
            cts.Cancel();
            if (_pump is { } pump)
            {
                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 泵被取消是正常收尾
                }
            }

            cts.Dispose();
            _pumpCts = null;
            _pump = null;
        }

        if (_publisher is { } publisher)
        {
            // 正常停机主动发 offline（保留）——MQTT 规范里优雅断开不触发遗嘱，
            // 遗嘱只在异常死亡（崩溃/断网）时由 Broker 代发，两者是互补的双保险
            try
            {
                await publisher.PublishStringAsync(
                    StatusTopic, "offline",
                    MqttQualityOfServiceLevel.AtLeastOnce, retain: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 停机路径的通告失败不阻塞后续清理
            }

            await publisher.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken)
                .ConfigureAwait(false);
            publisher.Dispose();
            _publisher = null;
        }

        if (_broker is { } broker)
        {
            await broker.StopAsync(new MqttServerStopOptions()).ConfigureAwait(false);
            broker.Dispose();
            _broker = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>喂一帧读数进发布队列（非阻塞，任何线程可调）。</summary>
    public void UpdateFrom(PointValue value) => _queue.Writer.TryWrite(value);

    private async Task PumpAsync(CancellationToken ct)
    {
        await foreach (var value in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    name = value.Name,
                    value = value.Value,
                    quality = value.Quality.ToString(),
                    timestamp = value.Timestamp.ToString("O"),
                }, PayloadJsonOptions);

                // 保留消息：晚到的订阅者立即拿到最后值（与趋势页"宁断线不伪造"同源的诚实数据——
                // Bad 也照发，quality 字段如实标注）
                await _publisher!.PublishStringAsync(
                    PointsTopicPrefix + value.Name, payload,
                    MqttQualityOfServiceLevel.AtLeastOnce, retain: true, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单帧发布失败（Broker 抖动）不终止泵：丢掉这一帧，下一帧继续
            }
        }
    }
}
