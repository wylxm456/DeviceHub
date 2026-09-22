using DeviceHub.Core.Models;

namespace DeviceHub.Core.Alarming;

/// <summary>
/// 报警引擎：把点位数据流判成"报警生命周期"。纯逻辑、无线程、无 IO——
/// 线程模型由宿主决定（本项目在 UI 线程喂数），因此可以脱离任何运行环境单测。
///
/// 三条工控报警的核心设计：
/// 1. 报警是状态机，不是谓词：Normal→Active→(Acknowledged)→Cleared，
///    每次迁移生成快照、发事件、进历史——"超过阈值"只是一个谓词，"报警"是它的一生；
/// 2. 回差（Hysteresis）防抖动：高限"≥阈值报、&lt;阈值-回差才清"。值在阈值附近抖动时，
///    没有回差的报警会触发/恢复刷屏（现场叫报警震荡），回差让边界成为滞环；
/// 3. 数据质量与工艺报警分离：Bad 质量冻结限值评判——坏数据既不触发工艺报警、
///    也不清除已有报警（保持最后可信判断）；通信异常由独立的 BadQuality 规则负责。
/// </summary>
public sealed class AlarmEngine
{
    private readonly Dictionary<string, List<RuleTracker>> _trackersByPoint = new();
    private readonly object _activeGate = new();
    private readonly List<Alarm> _activeAlarms = [];
    private readonly Queue<Alarm> _history;
    private long _nextId = 1;

    /// <summary>报警触发（Normal→Active）。</summary>
    public event Action<Alarm>? AlarmRaised;

    /// <summary>报警恢复（→Cleared），快照含完整生命周期时间。</summary>
    public event Action<Alarm>? AlarmCleared;

    /// <summary>报警被确认（Active→Acknowledged）。</summary>
    public event Action<Alarm>? AlarmAcknowledged;

    public AlarmEngine(IReadOnlyList<AlarmRule> rules, int historyCapacity = 500)
    {
        if (historyCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(historyCapacity), historyCapacity, "历史容量至少为 1。");
        }

        HistoryCapacity = historyCapacity;
        _history = new Queue<Alarm>(historyCapacity);

        foreach (var rule in rules)
        {
            if (!_trackersByPoint.TryGetValue(rule.PointName, out var trackers))
            {
                trackers = [];
                _trackersByPoint[rule.PointName] = trackers;
            }

            trackers.Add(new RuleTracker(rule));
        }
    }

    /// <summary>历史事件容量上限（触发/恢复各算一条），超出挤掉最旧的。</summary>
    public int HistoryCapacity { get; }

    /// <summary>当前活动报警（含已确认未恢复的），按触发时间倒序。</summary>
    public IReadOnlyList<Alarm> ActiveAlarms
    {
        get
        {
            lock (_activeGate)
            {
                return _activeAlarms.OrderByDescending(a => a.RaisedAt).ToList();
            }
        }
    }

    /// <summary>历史事件日志，从旧到新，容量封顶。</summary>
    public IReadOnlyList<Alarm> History
    {
        get
        {
            lock (_history)
            {
                return _history.ToList();
            }
        }
    }

    /// <summary>
    /// 喂入一个点位读数，驱动所有绑定该点位的规则状态机。
    /// 约定在宿主的单线程上下文调用（与界面一致的线程模型）。
    /// </summary>
    public void Process(PointValue value)
    {
        if (!_trackersByPoint.TryGetValue(value.Name, out var trackers))
        {
            return;
        }

        foreach (var tracker in trackers)
        {
            tracker.Evaluate(value, this);
        }
    }

    /// <summary>
    /// 确认一条活动报警。只有 Active 可以确认（重复确认返回 false）。
    /// </summary>
    public bool Acknowledge(long alarmId)
    {
        Alarm? snapshot = null;
        lock (_activeGate)
        {
            var index = _activeAlarms.FindIndex(a => a.Id == alarmId && a.State == AlarmState.Active);
            if (index < 0)
            {
                return false;
            }

            snapshot = _activeAlarms[index] with { State = AlarmState.Acknowledged };
            _activeAlarms[index] = snapshot;
        }

        AlarmAcknowledged?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// 清空一切（换设备时调用——上一台设备的报警对下一台毫无意义）。
    /// 不发事件：界面对这个动作有显式感知，不需要逐条通知。
    /// </summary>
    public void Reset()
    {
        lock (_activeGate)
        {
            _activeAlarms.Clear();
        }

        lock (_history)
        {
            _history.Clear();
        }

        foreach (var tracker in _trackersByPoint.Values.SelectMany(t => t))
        {
            tracker.ResetToNormal();
        }
    }

    // ===== 供 RuleTracker 回调的内部操作 =====

    internal void Raise(RuleTracker tracker, DateTime now, double? value, string description)
    {
        var alarm = new Alarm(
            _nextId++, tracker.Rule.PointName, tracker.Rule.Condition, tracker.Rule.Level,
            AlarmState.Active, now, null, value, description);

        tracker.Attach(alarm.Id);

        lock (_activeGate)
        {
            _activeAlarms.Add(alarm);
        }

        AppendHistory(alarm);
        AlarmRaised?.Invoke(alarm);
    }

    internal void Clear(RuleTracker tracker, DateTime now)
    {
        var id = tracker.TakeCurrentId();
        if (id is null)
        {
            return;
        }

        Alarm current;
        lock (_activeGate)
        {
            var index = _activeAlarms.FindIndex(a => a.Id == id.Value);
            if (index < 0)
            {
                return;
            }

            current = _activeAlarms[index];
            _activeAlarms.RemoveAt(index);
        }

        var alarm = current with { State = AlarmState.Cleared, ClearedAt = now };
        AppendHistory(alarm);
        AlarmCleared?.Invoke(alarm);
    }

    /// <summary>活动期间刷新极值（高限取最大、低限取最小），描述保持触发时刻的原文。</summary>
    internal void UpdatePeak(long alarmId, double value)
    {
        lock (_activeGate)
        {
            var index = _activeAlarms.FindIndex(a => a.Id == alarmId);
            if (index < 0)
            {
                return;
            }

            var current = _activeAlarms[index];
            var isWorse = current.Condition == AlarmCondition.HighLimit
                ? value > (current.PeakValue ?? double.MinValue)
                : value < (current.PeakValue ?? double.MaxValue);
            if (isWorse)
            {
                _activeAlarms[index] = current with { PeakValue = value };
            }
        }
    }

    private void AppendHistory(Alarm alarm)
    {
        lock (_history)
        {
            if (_history.Count == HistoryCapacity)
            {
                _history.Dequeue();
            }

            _history.Enqueue(alarm);
        }
    }

    /// <summary>一条规则的运行时状态机。规则是静态定义，Tracker 是它的一生——只管生命周期位，快照在引擎的活动表里。</summary>
    internal sealed class RuleTracker(AlarmRule rule)
    {
        private bool _active;

        public AlarmRule Rule { get; } = rule;

        public long? CurrentId { get; private set; }

        public void Attach(long id)
        {
            CurrentId = id;
            _active = true;
        }

        public long? TakeCurrentId()
        {
            var id = CurrentId;
            CurrentId = null;
            _active = false;
            return id;
        }

        public void ResetToNormal()
        {
            CurrentId = null;
            _active = false;
        }

        public void Evaluate(PointValue value, AlarmEngine engine)
        {
            var now = value.Timestamp;

            if (Rule.Condition == AlarmCondition.BadQuality)
            {
                if (value.Quality == PointQuality.Bad && !_active)
                {
                    engine.Raise(this, now, null, $"通信报警：点位“{Rule.PointName}”质量变 Bad");
                }
                else if (value.Quality == PointQuality.Good && _active)
                {
                    engine.Clear(this, now);
                }

                return;
            }

            // 限值评判只在好数据上进行：坏数据冻结判断（不触发也不恢复）
            if (value.Quality != PointQuality.Good || value.Value is null)
            {
                return;
            }

            var v = value.Value.Value;

            if (Rule.Condition == AlarmCondition.HighLimit)
            {
                if (!_active && v >= Rule.Threshold)
                {
                    engine.Raise(this, now, v, $"高限报警：{v:0.###} ≥ 阈值 {Rule.Threshold:0.###}");
                }
                else if (_active && v < Rule.Threshold - Rule.Hysteresis)
                {
                    engine.Clear(this, now);
                }
                else if (_active)
                {
                    // 滞环内徘徊：保持报警，只刷新极值
                    engine.UpdatePeak(CurrentId!.Value, v);
                }
            }
            else
            {
                if (!_active && v <= Rule.Threshold)
                {
                    engine.Raise(this, now, v, $"低限报警：{v:0.###} ≤ 阈值 {Rule.Threshold:0.###}");
                }
                else if (_active && v > Rule.Threshold + Rule.Hysteresis)
                {
                    engine.Clear(this, now);
                }
                else if (_active)
                {
                    engine.UpdatePeak(CurrentId!.Value, v);
                }
            }
        }
    }
}
