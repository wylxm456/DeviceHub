using DeviceHub.Core.Models;

namespace DeviceHub.Core.History;

/// <summary>一条点位历史采样（落库记录）。</summary>
/// <param name="PointName">点位显示名。</param>
/// <param name="Value">数值，Bad 质量时可能为 null。</param>
/// <param name="Quality">数据质量。</param>
/// <param name="Timestamp">采样时刻。</param>
public sealed record PointHistoryRecord(string PointName, double? Value, PointQuality Quality, DateTime Timestamp);

/// <summary>
/// 报警生命周期事件（触发/恢复/确认各记一条，追加不更新——事件溯源的最小形态）。
/// Condition/Level/Event 用字符串存储：历史表是"账本"，不依赖报警枚举的演进。
/// </summary>
/// <param name="AlarmId">引擎内的报警号（同一报警的三条事件共享此号，可还原生命周期）。</param>
/// <param name="PointName">点位显示名。</param>
/// <param name="Condition">条件：HighLimit / LowLimit / BadQuality。</param>
/// <param name="Level">级别：Warning / Critical。</param>
/// <param name="Event">事件：Raised / Cleared / Acknowledged。</param>
/// <param name="PeakValue">期间极值。</param>
/// <param name="Description">描述原文。</param>
/// <param name="EventTime">事件发生时刻。</param>
/// <param name="RaisedAt">该报警的触发时刻。</param>
/// <param name="ClearedAt">恢复时刻（未恢复为 null）。</param>
public sealed record AlarmEventRecord(
    long AlarmId,
    string PointName,
    string Condition,
    string Level,
    string Event,
    double? PeakValue,
    string Description,
    DateTime EventTime,
    DateTime RaisedAt,
    DateTime? ClearedAt);

/// <summary>点位历史存储抽象。实现负责建表与线程安全，调用方只管给数据。</summary>
public interface IPointHistoryStore
{
    /// <summary>批量追加采样。批量是为事务效率：单条插 1 万次和一次插 1 万条差一个数量级。</summary>
    Task AppendAsync(IReadOnlyList<PointHistoryRecord> records, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询某点位 [from, to] 的历史，按时间从旧到新返回；
    /// 超过 limit 时取最新 limit 条（画趋势关心的是临近当前的部分）。
    /// </summary>
    Task<IReadOnlyList<PointHistoryRecord>> QueryAsync(
        string pointName, DateTime from, DateTime to, int limit, CancellationToken cancellationToken = default);
}

/// <summary>报警事件存储抽象。</summary>
public interface IAlarmEventStore
{
    Task AppendAsync(IReadOnlyList<AlarmEventRecord> records, CancellationToken cancellationToken = default);

    /// <summary>最近的事件，按时间从新到旧。</summary>
    Task<IReadOnlyList<AlarmEventRecord>> QueryRecentAsync(int limit, CancellationToken cancellationToken = default);
}
