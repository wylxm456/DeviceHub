using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;

namespace DeviceHub.Core.Acquisition;

/// <summary>
/// 采集引擎：按固定周期轮询驱动，把读数以事件形式抛给订阅方。
/// 事件在线程池线程上触发，订阅方自行处理线程切换（界面侧切回 UI 线程）。
///
/// 断线恢复：连续失败达到阈值（ReconnectPolicy）后，引擎按指数退避间隔
/// 反复"断开→重连"驱动，恢复后自动回到采集并发出状态事件——
/// 现场网络抖动是常态，采集软件不许弃疗，恢复时间不可预估就只能无限退避重试。
/// </summary>
public sealed class AcquisitionEngine : IAsyncDisposable
{
    private readonly IDeviceDriver _driver;
    private readonly IReadOnlyList<PointDefinition> _points;
    private readonly TimeSpan _interval;
    private readonly ReconnectPolicy _policy;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>每读到一个点位值触发一次。</summary>
    public event Action<PointValue>? PointRead;

    /// <summary>某个采集周期读取失败时触发（网络断开、超时等），周期本身不中断。</summary>
    public event Action<Exception>? ReadFailed;

    /// <summary>采集状态变化（进入重连 / 恢复采集）时触发。</summary>
    public event Action<AcquisitionStatus>? StatusChanged;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public AcquisitionEngine(
        IDeviceDriver driver,
        IReadOnlyList<PointDefinition> points,
        TimeSpan interval,
        ReconnectPolicy? policy = null)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _points = points ?? throw new ArgumentNullException(nameof(points));
        if (points.Count == 0)
        {
            throw new ArgumentException("点位表不能为空。", nameof(points));
        }

        _interval = interval;
        _policy = policy ?? new ReconnectPolicy();
    }

    /// <summary>启动采集循环。重复调用幂等。</summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    /// <summary>停止采集循环并等待退出。</summary>
    public async Task StopAsync()
    {
        if (_loopTask is null)
        {
            return;
        }

        _cts!.Cancel();
        await _loopTask.ConfigureAwait(false);
        _cts.Dispose();
        _cts = null;
        _loopTask = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        var consecutiveFailures = 0;
        var reconnectAttempt = 0;
        var reconnecting = false;
        var lastError = string.Empty;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var values = await _driver
                    .ReadAsync(_points, ct)
                    .ConfigureAwait(false);

                consecutiveFailures = 0;
                if (reconnecting)
                {
                    // 重连后的首次成功读取才叫"恢复"——连接重建但读不通，重连就不算成功
                    reconnecting = false;
                    reconnectAttempt = 0;
                    StatusChanged?.Invoke(new AcquisitionStatus(AcquisitionState.Collecting, 0, TimeSpan.Zero));
                }

                foreach (var value in values)
                {
                    PointRead?.Invoke(value);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 单次失败不终止循环。但也不能让界面数值冻住、假装还在采——
                // 本轮所有点位按 Bad 质量发布，"读不到"本身就是信息（质量戳设计的兑现）
                consecutiveFailures++;
                lastError = ex.Message;
                ReadFailed?.Invoke(ex);
                PublishBadBatch();
            }

            if (_policy.ShouldAttemptReconnect(consecutiveFailures))
            {
                var delay = _policy.GetRetryDelay(reconnectAttempt);
                reconnecting = true;
                reconnectAttempt++;
                StatusChanged?.Invoke(new AcquisitionStatus(
                    AcquisitionState.Reconnecting, reconnectAttempt, delay, lastError));

                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);

                    // 先断后连：清掉半死的 socket/会话再重建。驱动契约保证
                    // Connect 幂等、Disconnect 在未连接时安全返回
                    await _driver.DisconnectAsync(ct).ConfigureAwait(false);
                    await _driver.ConnectAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 重连失败不终止：下一轮按更长的退避间隔继续（指数退避 + 封顶）
                }
            }

            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void PublishBadBatch()
    {
        var now = DateTime.Now;
        foreach (var point in _points)
        {
            PointRead?.Invoke(new PointValue(point.Name, null, PointQuality.Bad, now));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
