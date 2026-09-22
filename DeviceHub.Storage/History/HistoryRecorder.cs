using System.Collections.Concurrent;
using DeviceHub.Core.History;
using Microsoft.Extensions.Hosting;

namespace DeviceHub.Storage.History;

/// <summary>
/// 历史记录后台写入器：宿主线程（UI）只往无锁队列丢数据，本服务按周期把积压
/// 批量冲刷落库。采集频率是每秒若干条，磁盘事务是每两秒一次——界面永不碰磁盘。
///
/// 实现 IHostedService：交给 Generic Host 托管，随应用启停；StopAsync 时做
/// 最后一次冲刷，不丢队列尾数。冲刷失败不终止服务（磁盘故障不该杀死采集），
/// 通过 FlushFailed 事件交给宿主决定怎么记日志——库不发日志策略。
/// </summary>
public sealed class HistoryRecorder : IHostedService, IAsyncDisposable
{
    private readonly IPointHistoryStore _pointStore;
    private readonly IAlarmEventStore _alarmStore;
    private readonly TimeSpan _flushInterval;
    private readonly ConcurrentQueue<PointHistoryRecord> _points = new();
    private readonly ConcurrentQueue<AlarmEventRecord> _alarms = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>单次冲刷失败时触发（磁盘满、库损坏等），宿主据此记日志/报警。</summary>
    public event Action<Exception>? FlushFailed;

    public HistoryRecorder(
        IPointHistoryStore pointStore,
        IAlarmEventStore alarmStore,
        TimeSpan flushInterval)
    {
        if (flushInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(flushInterval), flushInterval, "冲刷周期必须为正。");
        }

        _pointStore = pointStore;
        _alarmStore = alarmStore;
        _flushInterval = flushInterval;
    }

    /// <summary>入队一条点位采样。非阻塞，任何线程可调。</summary>
    public void EnqueuePoint(PointHistoryRecord record) => _points.Enqueue(record);

    /// <summary>入队一条报警事件。非阻塞，任何线程可调。</summary>
    public void EnqueueAlarm(AlarmEventRecord record) => _alarms.Enqueue(record);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => FlushLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loop is null)
        {
            return;
        }

        _cts!.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 循环被取消是正常退出路径
        }

        _loop = null;
        _cts.Dispose();
        _cts = null;

        // 停止前最后一次冲刷：不丢队列尾数，也不受取消令牌影响；
        // 失败同样只发事件不外抛——停机路径不能因磁盘问题崩掉
        try
        {
            await FlushOnceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FlushFailed?.Invoke(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_loop is not null)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_flushInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
                await FlushOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 单次冲刷失败不终止循环：现场磁盘问题可能是暂时的，下个周期重试
                FlushFailed?.Invoke(ex);
            }
        }
    }

    /// <summary>冲刷一次：把队列当前积压全部写库。internal 供单测直接驱动。</summary>
    internal async Task FlushOnceAsync(CancellationToken ct)
    {
        var pointBatch = new List<PointHistoryRecord>();
        while (_points.TryDequeue(out var record))
        {
            pointBatch.Add(record);
        }

        if (pointBatch.Count > 0)
        {
            await _pointStore.AppendAsync(pointBatch, ct).ConfigureAwait(false);
        }

        var alarmBatch = new List<AlarmEventRecord>();
        while (_alarms.TryDequeue(out var record))
        {
            alarmBatch.Add(record);
        }

        if (alarmBatch.Count > 0)
        {
            await _alarmStore.AppendAsync(alarmBatch, ct).ConfigureAwait(false);
        }
    }
}
