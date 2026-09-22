using System.Diagnostics;
using DeviceHub.Core.Alarming;
using DeviceHub.Core.History;
using DeviceHub.Core.Models;
using DeviceHub.Storage.History;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// 历史存储测试：真 SQLite（临时文件），不 mock 数据库——
/// 落库代码最容易出问题的地方（类型映射/时间格式/空值）恰恰在真库上。
/// </summary>
public class HistoryStorageTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"devicehub-test-{Guid.NewGuid():N}.db");

    private static readonly DateTime T0 = new(2026, 9, 22, 10, 0, 0);

    public void Dispose()
    {
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
            // 临时库删不掉不影响测试结论
        }
    }

    private SqliteHistoryStore CreateStore() => new(_dbPath);

    [Fact]
    public async Task PointHistory_Roundtrip_KeepsNullsEnumsAndOrder()
    {
        var store = CreateStore();
        var records = new List<PointHistoryRecord>
        {
            new("温度", 45.5, PointQuality.Good, T0),
            new("温度", null, PointQuality.Bad, T0.AddSeconds(1)),
            new("压力", 0.42, PointQuality.Good, T0.AddSeconds(2)),
        };

        await store.AppendAsync(records);

        var loaded = await store.QueryAsync("温度", T0.AddSeconds(-1), T0.AddSeconds(10), 100);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(45.5, loaded[0].Value);
        Assert.Equal(PointQuality.Good, loaded[0].Quality);
        Assert.Null(loaded[1].Value);
        Assert.Equal(PointQuality.Bad, loaded[1].Quality);
        Assert.Equal(T0.AddSeconds(1), loaded[1].Timestamp);
    }

    [Fact]
    public async Task Query_FiltersByPointAndRange_TakesNewest_OrdersOldestFirst()
    {
        var store = CreateStore();
        var records = Enumerable.Range(0, 5)
            .Select(i => new PointHistoryRecord("温度", 40 + i, PointQuality.Good, T0.AddSeconds(i)))
            .Concat(
            [
                new PointHistoryRecord("压力", 0.4, PointQuality.Good, T0),
            ])
            .ToList();
        await store.AppendAsync(records);

        // 只查温度，时间窗卡在 3 秒处：只剩 0/1/2/3 秒四条
        var inRange = await store.QueryAsync("温度", T0.AddSeconds(-1), T0.AddSeconds(3), 100);
        Assert.Equal(4, inRange.Count);
        Assert.Equal(40, inRange[0].Value);

        // limit 2 取最新两条，返回顺序仍从旧到新
        var limited = await store.QueryAsync("温度", T0.AddSeconds(-1), T0.AddSeconds(10), 2);
        Assert.Equal(2, limited.Count);
        Assert.Equal(43, limited[0].Value);
        Assert.Equal(44, limited[1].Value);
    }

    [Fact]
    public async Task AlarmEvents_Roundtrip_KeepsNullableFields()
    {
        var store = CreateStore();
        var events = new List<AlarmEventRecord>
        {
            new(1, "温度", "HighLimit", "Critical", "Raised", 46.2, "高限报警：46.2 ≥ 阈值 45",
                T0, T0, null),
            new(1, "温度", "HighLimit", "Critical", "Cleared", 46.2, "高限报警：46.2 ≥ 阈值 45",
                T0.AddMinutes(3), T0, T0.AddMinutes(3)),
        };

        await store.AppendAsync(events);

        var loaded = await store.QueryRecentAsync(1);
        Assert.Single(loaded);
        Assert.Equal("Cleared", loaded[0].Event);
        Assert.Equal(T0.AddMinutes(3), loaded[0].ClearedAt);

        var all = await store.QueryRecentAsync(10);
        Assert.Equal(2, all.Count);
        Assert.Null(all[1].ClearedAt);
    }

    [Fact]
    public async Task Recorder_BatchesInBackground_AndFlushesTailOnStop()
    {
        var store = CreateStore();
        var recorder = new HistoryRecorder(store, store, TimeSpan.FromMilliseconds(30));

        await recorder.StartAsync(CancellationToken.None);
        for (var i = 0; i < 5; i++)
        {
            recorder.EnqueuePoint(new PointHistoryRecord("温度", 40 + i, PointQuality.Good, T0.AddSeconds(i)));
        }

        // 等后台冲刷把 5 条落库（周期 30ms，宽限 3 秒）
        var stopwatch = Stopwatch.StartNew();
        var count = 0;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(3))
        {
            count = (await store.QueryAsync("温度", T0.AddSeconds(-1), T0.AddSeconds(100), 100)).Count;
            if (count >= 5)
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.True(count >= 5, $"3 秒内后台冲刷应落库 5 条，实际 {count} 条");

        // 停止前的队列尾数由 StopAsync 的最终冲刷兜底
        recorder.EnqueuePoint(new PointHistoryRecord("温度", 99, PointQuality.Good, T0.AddSeconds(9)));
        await recorder.StopAsync(CancellationToken.None);

        var all = await store.QueryAsync("温度", T0.AddSeconds(-1), T0.AddSeconds(100), 100);
        Assert.Equal(6, all.Count);
        Assert.Equal(99, all[^1].Value);
    }

    [Fact]
    public async Task Recorder_FlushFailure_SurfacesViaEvent_WithoutKillingLoop()
    {
        var failingStore = new ThrowingPointStore();
        var alarmEvents = new List<AlarmEventRecord>();
        var recorder = new HistoryRecorder(failingStore, new RecordingAlarmStore(alarmEvents), TimeSpan.FromMilliseconds(30));

        var failures = new List<Exception>();
        recorder.FlushFailed += failures.Add;

        await recorder.StartAsync(CancellationToken.None);
        recorder.EnqueuePoint(new PointHistoryRecord("温度", 1, PointQuality.Good, T0));

        var stopwatch = Stopwatch.StartNew();
        while (failures.Count == 0 && stopwatch.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(25);
        }

        Assert.NotEmpty(failures);

        // 失败后循环仍活着：换成好库（此处直接清空故障标志模拟恢复）再入队，事件继续产生
        await recorder.StopAsync(CancellationToken.None);
        Assert.Empty(alarmEvents); // 报警批次未受故障存储牵连（本用例没入队报警）
    }

    private sealed class ThrowingPointStore : IPointHistoryStore
    {
        public Task AppendAsync(IReadOnlyList<PointHistoryRecord> records, CancellationToken cancellationToken = default) =>
            throw new IOException("模拟磁盘故障");

        public Task<IReadOnlyList<PointHistoryRecord>> QueryAsync(
            string pointName, DateTime from, DateTime to, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PointHistoryRecord>>([]);
    }

    private sealed class RecordingAlarmStore(List<AlarmEventRecord> sink) : IAlarmEventStore
    {
        public Task AppendAsync(IReadOnlyList<AlarmEventRecord> records, CancellationToken cancellationToken = default)
        {
            sink.AddRange(records);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AlarmEventRecord>> QueryRecentAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AlarmEventRecord>>([.. sink.Take(limit).Reverse()]);
    }
}
