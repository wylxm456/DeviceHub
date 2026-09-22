namespace DeviceHub.Core.Alarming;

/// <summary>报警级别。分级的目的：紧急的必须大声，警告的不能吵死人。</summary>
public enum AlarmLevel
{
    Warning,
    Critical,
}

/// <summary>报警条件类型。</summary>
public enum AlarmCondition
{
    /// <summary>高限：值 ≥ 阈值触发，&lt; 阈值-回差 恢复。</summary>
    HighLimit,

    /// <summary>低限：值 ≤ 阈值触发，&gt; 阈值+回差 恢复。</summary>
    LowLimit,

    /// <summary>通信报警：点位质量变 Bad 触发、恢复 Good 消除——通信坏≠工艺越限，两类分开。</summary>
    BadQuality,
}

/// <summary>报警在生命周期中的状态。Cleared 只出现在历史里——恢复的报警不再占用"活动"列表。</summary>
public enum AlarmState
{
    Active,
    Acknowledged,
    Cleared,
}

/// <summary>
/// 报警规则（静态定义，来自配置）。一条规则绑定一个点位名 + 一种条件；
/// 当前连接的设备没有该点位时，规则静默不生效。
/// </summary>
/// <param name="PointName">点位显示名，与 PointDefinition.Name 对应。</param>
/// <param name="Condition">条件类型。</param>
/// <param name="Threshold">限值阈值（BadQuality 条件不使用）。</param>
/// <param name="Hysteresis">回差（BadQuality 条件不使用）。</param>
/// <param name="Level">报警级别。</param>
public sealed record AlarmRule(
    string PointName,
    AlarmCondition Condition,
    double Threshold,
    double Hysteresis,
    AlarmLevel Level)
{
    public static AlarmRule Create(
        string pointName,
        AlarmCondition condition,
        double threshold,
        double hysteresis,
        AlarmLevel level)
    {
        if (string.IsNullOrWhiteSpace(pointName))
        {
            throw new ArgumentException("报警规则必须绑定点位名。", nameof(pointName));
        }

        if (condition != AlarmCondition.BadQuality)
        {
            if (double.IsNaN(threshold) || double.IsInfinity(threshold))
            {
                throw new ArgumentException($"点位“{pointName}”的报警阈值非法：{threshold}。", nameof(threshold));
            }

            if (double.IsNaN(hysteresis) || hysteresis < 0)
            {
                throw new ArgumentException($"点位“{pointName}”的报警回差非法：{hysteresis}（不能为负）。", nameof(hysteresis));
            }
        }

        return new AlarmRule(pointName, condition, threshold, hysteresis, level);
    }
}

/// <summary>
/// 一条报警的快照（不可变）：触发/确认/恢复时由引擎生成新快照并对外发布。
/// 记录触发时刻的极值，方便事后分析"当时最坏到过多少"。
/// </summary>
public sealed record Alarm(
    long Id,
    string PointName,
    AlarmCondition Condition,
    AlarmLevel Level,
    AlarmState State,
    DateTime RaisedAt,
    DateTime? ClearedAt,
    double? PeakValue,
    string Description);
