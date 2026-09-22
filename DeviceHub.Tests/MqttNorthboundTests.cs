using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DeviceHub.Core.Models;
using DeviceHub.Northbound.Mqtt;
using MQTTnet;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// MQTT 北向端到端测试：进程内起真 Broker + 发布客户端，再用独立订阅客户端
/// 收消息——遗嘱/保留/JSON 载荷全部走真协议栈验证。
/// </summary>
public class MqttNorthboundTests
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class MessageCollector : IDisposable
    {
        private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
        private readonly ConcurrentQueue<(string Topic, string Payload)> _received = new();

        public async Task ConnectAndSubscribeAsync(int port, string topicFilter)
        {
            _client.ApplicationMessageReceivedAsync += e =>
            {
                _received.Enqueue((e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString()));
                return Task.CompletedTask;
            };
            await _client.ConnectAsync(new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", port)
                .WithClientId("devicehub-tests-subscriber")
                .Build());
            await _client.SubscribeAsync(topicFilter, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce);
        }

        public bool TryWaitFor(Func<string, string, bool> match, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                foreach (var (topic, payload) in _received)
                {
                    if (match(topic, payload))
                    {
                        return true;
                    }
                }

                Thread.Sleep(50);
            }

            return false;
        }

        public void Dispose() => _client.Dispose();
    }

    [Fact]
    public async Task Publisher_AnnouncesOnline_AndPublishesPointValues()
    {
        var service = new MqttNorthboundService(GetFreePort());
        await service.StartAsync(CancellationToken.None);
        try
        {
            using var collector = new MessageCollector();
            await collector.ConnectAndSubscribeAsync(service.Port, "devicehub/#");

            // 连接通告：online（保留消息）
            Assert.True(
                collector.TryWaitFor((t, p) => t == MqttNorthboundService.StatusTopic && p == "online",
                    TimeSpan.FromSeconds(5)),
                "应收到 status=online 通告");

            // 喂一帧好数据：JSON 载荷带值与质量，中文点位名不转义
            service.UpdateFrom(new PointValue("温度", 45.5, PointQuality.Good, new DateTime(2026, 9, 22, 10, 0, 0)));
            Assert.True(
                collector.TryWaitFor((t, p) => t == "devicehub/points/温度" && p.Contains("45.5") && p.Contains("Good") && p.Contains("温度"),
                    TimeSpan.FromSeconds(5)),
                "应收到温度点的 JSON 载荷");

            // 坏数据也照发，quality 如实标注（北向的诚实数据观）
            service.UpdateFrom(new PointValue("温度", null, PointQuality.Bad, DateTime.Now));
            Assert.True(
                collector.TryWaitFor((t, p) => t == "devicehub/points/温度" && p.Contains("Bad"),
                    TimeSpan.FromSeconds(5)),
                "断线帧应以 quality=Bad 发布");
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task RetainedMessage_LateSubscriber_GetsLastValueImmediately()
    {
        var service = new MqttNorthboundService(GetFreePort());
        await service.StartAsync(CancellationToken.None);
        try
        {
            // 先发布数据，订阅者后到——保留消息让它立即拿到最后值，不等下一帧
            service.UpdateFrom(new PointValue("压力", 0.42, PointQuality.Good, DateTime.Now));

            using var collector = new MessageCollector();
            await collector.ConnectAndSubscribeAsync(service.Port, MqttNorthboundService.PointsTopicPrefix + "压力");
            Assert.True(
                collector.TryWaitFor((t, p) => t == "devicehub/points/压力" && p.Contains("0.42"),
                    TimeSpan.FromSeconds(5)),
                "晚到的订阅者应立即收到保留的最后值");
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopAsync_PublishesOfflineViaWill()
    {
        var service = new MqttNorthboundService(GetFreePort());
        await service.StartAsync(CancellationToken.None);

        using var collector = new MessageCollector();
        await collector.ConnectAndSubscribeAsync(service.Port, MqttNorthboundService.StatusTopic);

        // 主动停止：客户端正常断开，Broker 按协议广播遗嘱/离线通告
        await service.StopAsync(CancellationToken.None);

        Assert.True(
            collector.TryWaitFor((t, p) => t == MqttNorthboundService.StatusTopic && p == "offline",
                TimeSpan.FromSeconds(5)),
            "服务停止后订阅方应收到 offline 通告");
    }
}
