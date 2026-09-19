using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;

namespace DeviceHub.Core.Acquisition;

/// <summary>
/// 采集引擎：按固定周期轮询驱动，把读数以事件形式抛给订阅方。
/// 事件在线程池线程上触发，订阅方自行处理线程切换（界面侧切回 UI 线程）。
/// M1 演进点：事件换成 Channel 队列，实现采集与消费的完全解耦和背压控制。
/// </summary>
public sealed class AcquisitionEngine : IAsyncDisposable
{
    private readonly IDeviceDriver _driver;
    private readonly IReadOnlyList<PointDefinition> _points;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>每读到一个点位值触发一次。</summary>
    public event Action<PointValue>? PointRead;

    /// <summary>某个采集周期读取失败时触发（网络断开、超时等），周期本身不中断。</summary>
    public event Action<Exception>? ReadFailed;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public AcquisitionEngine(
        IDeviceDriver driver,
        IReadOnlyList<PointDefinition> points,
        TimeSpan interval)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _points = points ?? throw new ArgumentNullException(nameof(points));
        if (points.Count == 0)
        {
            throw new ArgumentException("点位表不能为空。", nameof(points));
        }

        _interval = interval;
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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var values = await _driver
                    .ReadAsync(_points, ct)
                    .ConfigureAwait(false);

                foreach (var value in values)
                {
                    PointRead?.Invoke(value);
                }

                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 单次失败不终止循环：现场网络抖动是常态，重连策略在 M1 引入
                ReadFailed?.Invoke(ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
