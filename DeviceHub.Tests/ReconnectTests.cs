using System.Collections.Concurrent;
using System.Diagnostics;
using DeviceHub.Core.Acquisition;
using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;
using DeviceHub.Drivers.Simulated;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// 断线重连的引擎级行为测试：用装饰器 FaultyDriver 注入"读失败"并统计
/// 连接动作——断不断线由测试说了算，策略对不对不靠真拔网线验证。
/// </summary>
public class ReconnectEngineTests
{
    /// <summary>装饰器：包住真实驱动，可注入读失败，同时统计断开/重连的调用次数。</summary>
    private sealed class FaultyDriver : IDeviceDriver
    {
        private readonly IDeviceDriver _inner;
        private Func<IReadOnlyList<PointDefinition>, CancellationToken, Task<IReadOnlyList<PointValue>>>? _readOverride;
        private int _calls;

        public FaultyDriver(IDeviceDriver inner) => _inner = inner;

        public int ConnectCount { get; private set; }

        public int DisconnectCount { get; private set; }

        public string Name => _inner.Name;

        public bool IsConnected => _inner.IsConnected;

        /// <summary>前 count 次读取抛异常，之后放行给内层驱动——模拟"网络闪断后恢复"。</summary>
        public void FailFirstReads(int count)
        {
            _readOverride = async (points, ct) =>
            {
                if (Interlocked.Increment(ref _calls) <= count)
                {
                    throw new IOException("模拟连接中断");
                }

                return await _inner.ReadAsync(points, ct);
            };
        }

        /// <summary>所有读取都抛异常——模拟"设备长时间离线"。</summary>
        public void FailAllReads() =>
            _readOverride = (_, _) => throw new IOException("模拟连接中断");

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            await _inner.ConnectAsync(cancellationToken);
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            await _inner.DisconnectAsync(cancellationToken);
        }

        public Task<IReadOnlyList<PointValue>> ReadAsync(
            IReadOnlyList<PointDefinition> points, CancellationToken cancellationToken = default) =>
            _readOverride is null
                ? _inner.ReadAsync(points, cancellationToken)
                : _readOverride(points, cancellationToken);

        public Task WriteAsync(PointDefinition point, double value, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(point, value, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static async Task<FaultyDriver> CreateConnectedWrapperAsync()
    {
        // 必须通过包装器连接，让首次连接也计入 ConnectCount——
        // 断言"没有额外重连"依赖这个基准数
        var wrapper = new FaultyDriver(new SimulatedDriver());
        await wrapper.ConnectAsync();
        return wrapper;
    }

    private static ReconnectPolicy FastPolicy() => new(
        failureThreshold: 3,
        baseDelay: TimeSpan.FromMilliseconds(20),
        maxDelay: TimeSpan.FromMilliseconds(60));

    [Fact]
    public async Task ConsecutiveFailures_TriggerReconnectWithBackoff()
    {
        var driver = await CreateConnectedWrapperAsync();
        driver.FailAllReads();

        var statuses = new ConcurrentQueue<AcquisitionStatus>();
        var badCount = 0;
        var engine = new AcquisitionEngine(
            driver, SimulatedDriver.DefaultPoints, TimeSpan.FromMilliseconds(30), FastPolicy());
        engine.PointRead += v =>
        {
            if (v.Quality == PointQuality.Bad)
            {
                Interlocked.Increment(ref badCount);
            }
        };
        engine.StatusChanged += statuses.Enqueue;

        try
        {
            engine.Start();
            var stopwatch = Stopwatch.StartNew();
            while (driver.ConnectCount < 3 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(20);
            }

            Assert.True(driver.ConnectCount >= 3, $"5 秒内应发生多次重连，实际连接次数 {driver.ConnectCount}");
            Assert.True(driver.DisconnectCount >= 2, "重连前必须先断开半死的连接");
            Assert.True(badCount >= 6, $"失败周期应发布 Bad 质量数据，实际 {badCount} 条");
            Assert.True(engine.IsRunning, "重连永不放弃：引擎不能自行退出");

            var reconnectStatuses = statuses
                .Where(s => s.State == AcquisitionState.Reconnecting)
                .ToList();
            Assert.True(reconnectStatuses.Count >= 2, "每次重试都应发布重连状态");
            Assert.Equal(1, reconnectStatuses[0].Attempt);
            Assert.True(reconnectStatuses[1].Attempt > reconnectStatuses[0].Attempt, "重试序号应递增");
            Assert.True(
                reconnectStatuses[1].NextRetryDelay >= reconnectStatuses[0].NextRetryDelay,
                "退避间隔应随重试次数不减（指数退避）");
            Assert.False(string.IsNullOrEmpty(reconnectStatuses[0].LastError), "重连状态应携带触发异常的消息");
        }
        finally
        {
            await engine.DisposeAsync();
            await driver.DisposeAsync();
        }
    }

    [Fact]
    public async Task Recovery_AfterSuccessfulReconnect_ResumesCollecting()
    {
        var driver = await CreateConnectedWrapperAsync();
        driver.FailFirstReads(6);

        var statuses = new ConcurrentQueue<AcquisitionStatus>();
        var goodCount = 0;
        var engine = new AcquisitionEngine(
            driver, SimulatedDriver.DefaultPoints, TimeSpan.FromMilliseconds(30), FastPolicy());
        engine.PointRead += v =>
        {
            if (v.Quality == PointQuality.Good)
            {
                Interlocked.Increment(ref goodCount);
            }
        };
        engine.StatusChanged += statuses.Enqueue;

        try
        {
            engine.Start();
            var stopwatch = Stopwatch.StartNew();
            while (goodCount < 5 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(20);
            }

            Assert.True(goodCount >= 5, "恢复后应重新读到 Good 数据");
            Assert.True(driver.ConnectCount >= 2, "失败超过阈值应发生过重连");

            // 状态轨迹必须收在"恢复采集"上，且重试序号归零
            var states = statuses.Select(s => s.State).ToList();
            Assert.Contains(AcquisitionState.Reconnecting, states);
            Assert.Equal(AcquisitionState.Collecting, states.Last());
            Assert.Equal(0, statuses.Last().Attempt);
        }
        finally
        {
            await engine.DisposeAsync();
            await driver.DisposeAsync();
        }
    }

    [Fact]
    public async Task SingleFailure_BelowThreshold_DoesNotReconnect()
    {
        var driver = await CreateConnectedWrapperAsync();
        driver.FailFirstReads(1);

        var goodCount = 0;
        var statusCount = 0;
        var engine = new AcquisitionEngine(
            driver, SimulatedDriver.DefaultPoints, TimeSpan.FromMilliseconds(30), FastPolicy());
        engine.PointRead += v =>
        {
            if (v.Quality == PointQuality.Good)
            {
                Interlocked.Increment(ref goodCount);
            }
        };
        engine.StatusChanged += _ => Interlocked.Increment(ref statusCount);

        try
        {
            engine.Start();
            var stopwatch = Stopwatch.StartNew();
            while (goodCount < 9 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(20);
            }

            Assert.True(goodCount >= 9, "单次失败后应继续正常采集");
            Assert.Equal(1, driver.ConnectCount);  // 只有测试建立的那次连接，没有重连
            Assert.Equal(0, driver.DisconnectCount);
            Assert.Equal(0, statusCount);          // 阈值未到，连状态事件都不该发
        }
        finally
        {
            await engine.DisposeAsync();
            await driver.DisposeAsync();
        }
    }
}

/// <summary>重连策略纯逻辑单测：阈值边界、指数退避曲线、封顶、参数校验。</summary>
public class ReconnectPolicyTests
{
    [Fact]
    public void Threshold_IsBoundaryOfReconnectTrigger()
    {
        var policy = new ReconnectPolicy(failureThreshold: 3);
        Assert.False(policy.ShouldAttemptReconnect(2));
        Assert.True(policy.ShouldAttemptReconnect(3));
        Assert.True(policy.ShouldAttemptReconnect(10));
    }

    [Fact]
    public void RetryDelay_DoublesWithAttempt_AndCaps()
    {
        var policy = new ReconnectPolicy(
            failureThreshold: 3,
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetRetryDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetRetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetRetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetRetryDelay(3));  // 8s 封顶到 5s
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetRetryDelay(100)); // 序号很大也不溢出
    }

    [Fact]
    public void Constructor_HasSensibleDefaults()
    {
        var policy = new ReconnectPolicy();
        Assert.Equal(3, policy.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(15), policy.MaxDelay);
    }

    [Fact]
    public void Constructor_RejectsInvalidParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy(failureThreshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy(baseDelay: TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new ReconnectPolicy(
            baseDelay: TimeSpan.FromSeconds(5), maxDelay: TimeSpan.FromSeconds(1)));
    }
}
