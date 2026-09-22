using DeviceHub.Core.Alarming;
using DeviceHub.Core.Configuration;
using DeviceHub.Core.Models;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// 报警引擎单测：状态机语义（触发/恢复/确认）、回差防抖、质量与工艺分离、
/// 历史容量、重置。纯逻辑测试——没有一处 Task.Delay。
/// </summary>
public class AlarmEngineTests
{
    private static readonly DateTime T0 = new(2026, 9, 22, 10, 0, 0);

    private static PointValue Good(string name, double v, int seconds = 0) =>
        new(name, v, PointQuality.Good, T0.AddSeconds(seconds));

    private static PointValue Bad(string name, int seconds = 0) =>
        new(name, null, PointQuality.Bad, T0.AddSeconds(seconds));

    private static AlarmEngine CreateEngine(params AlarmRule[] rules) =>
        new(rules);

    [Fact]
    public void HighLimit_RaisesAtThreshold_ClearsOnlyAfterHysteresis()
    {
        var raised = new List<Alarm>();
        var cleared = new List<Alarm>();
        var engine = CreateEngine(AlarmRule.Create("温度", AlarmCondition.HighLimit, 45, hysteresis: 1, AlarmLevel.Critical));
        engine.AlarmRaised += raised.Add;
        engine.AlarmCleared += cleared.Add;

        engine.Process(Good("温度", 44.9));
        Assert.Empty(raised);

        engine.Process(Good("温度", 45.0, seconds: 1));
        Assert.Single(raised);
        var alarm = raised[0];
        Assert.Equal(AlarmState.Active, alarm.State);
        Assert.Equal(45.0, alarm.PeakValue);
        Assert.Contains("45", alarm.Description);

        // 滞环内徘徊（44.5 ≥ 45-1）：保持报警，不恢复也不新增
        engine.Process(Good("温度", 44.5, seconds: 2));
        Assert.Single(raised);
        Assert.Single(engine.ActiveAlarms);

        // 跌出滞环（43.9 < 45-1）：恢复
        engine.Process(Good("温度", 43.9, seconds: 3));
        Assert.Single(cleared);
        Assert.Equal(AlarmState.Cleared, cleared[0].State);
        Assert.NotNull(cleared[0].ClearedAt);
        Assert.Empty(engine.ActiveAlarms);
        Assert.Equal(2, engine.History.Count);
    }

    [Fact]
    public void LowLimit_RaisesAtThreshold_ClearsOnlyAfterHysteresis()
    {
        var raised = new List<Alarm>();
        var engine = CreateEngine(AlarmRule.Create("温度", AlarmCondition.LowLimit, 30, hysteresis: 1, AlarmLevel.Warning));
        engine.AlarmRaised += raised.Add;

        engine.Process(Good("温度", 30.5));
        Assert.Empty(raised);

        engine.Process(Good("温度", 30.0, seconds: 1));
        Assert.Single(raised);

        engine.Process(Good("温度", 30.5, seconds: 2)); // 仍 ≤ 30+1：滞环内
        Assert.Single(raised);

        engine.Process(Good("温度", 31.1, seconds: 3)); // > 31：恢复
        Assert.Empty(engine.ActiveAlarms);
    }

    [Fact]
    public void BadQualityData_FreezesLimitEvaluation()
    {
        var raised = new List<Alarm>();
        var cleared = new List<Alarm>();
        var engine = CreateEngine(AlarmRule.Create("温度", AlarmCondition.HighLimit, 45, hysteresis: 1, AlarmLevel.Critical));
        engine.AlarmRaised += raised.Add;
        engine.AlarmCleared += cleared.Add;

        // 坏数据不触发工艺报警
        engine.Process(Bad("温度"));
        Assert.Empty(raised);

        engine.Process(Good("温度", 46));
        Assert.Single(raised);

        // 坏数据也不恢复已有报警——保持最后可信判断
        engine.Process(Bad("温度", seconds: 1));
        Assert.Empty(cleared);
        Assert.Single(engine.ActiveAlarms);

        engine.Process(Good("温度", 43, seconds: 2));
        Assert.Single(cleared);
    }

    [Fact]
    public void BadQualityRule_RaisesOnBad_ClearsOnGood()
    {
        var raised = new List<Alarm>();
        var engine = CreateEngine(AlarmRule.Create("温度", AlarmCondition.BadQuality, 0, 0, AlarmLevel.Critical));
        engine.AlarmRaised += raised.Add;

        engine.Process(Good("温度", 40));
        Assert.Empty(raised);

        engine.Process(Bad("温度", seconds: 1));
        Assert.Single(raised);
        Assert.Contains("通信", raised[0].Description);

        engine.Process(Good("温度", 41, seconds: 2));
        Assert.Empty(engine.ActiveAlarms);
    }

    [Fact]
    public void Acknowledge_TransitionsOnce_ThenAlarmCanClear()
    {
        Alarm? acknowledged = null;
        var engine = CreateEngine(AlarmRule.Create("温度", AlarmCondition.HighLimit, 45, hysteresis: 1, AlarmLevel.Critical));
        long raisedId = 0;
        engine.AlarmRaised += a => raisedId = a.Id;
        engine.AlarmAcknowledged += a => acknowledged = a;

        engine.Process(Good("温度", 46));
        Assert.True(engine.Acknowledge(raisedId));
        Assert.NotNull(acknowledged);
        Assert.Equal(AlarmState.Acknowledged, acknowledged!.State);
        Assert.Equal(AlarmState.Acknowledged, engine.ActiveAlarms[0].State);

        // 已确认的再确认是无效操作
        Assert.False(engine.Acknowledge(raisedId));
        Assert.False(engine.Acknowledge(9999)); // 不存在的报警

        // 确认过再恢复：正常走清除
        engine.Process(Good("温度", 40, seconds: 1));
        Assert.Empty(engine.ActiveAlarms);
        Assert.Equal(AlarmState.Cleared, engine.History.Last().State);
    }

    [Fact]
    public void History_RespectsCapacity()
    {
        var engine = new AlarmEngine(
            [AlarmRule.Create("温度", AlarmCondition.HighLimit, 45, hysteresis: 0, AlarmLevel.Warning)],
            historyCapacity: 2);

        engine.Process(Good("温度", 50)); // 触发 → 历史 1
        engine.Process(Good("温度", 40)); // 恢复 → 历史 2
        engine.Process(Good("温度", 50, seconds: 1)); // 再触发 → 挤掉最旧

        Assert.Equal(2, engine.History.Count);
        Assert.Equal(AlarmState.Cleared, engine.History[0].State); // 第一次触发已被挤掉
        Assert.Equal(AlarmState.Active, engine.History[1].State);
    }

    [Fact]
    public void Reset_ClearsActiveAndHistory_AllowsFreshAlarms()
    {
        var raised = new List<Alarm>();
        var engine = CreateEngine(AlarmRule.Create("温度", AlarmCondition.HighLimit, 45, hysteresis: 1, AlarmLevel.Critical));
        engine.AlarmRaised += raised.Add;

        engine.Process(Good("温度", 50));
        Assert.Single(engine.ActiveAlarms);

        engine.Reset();
        Assert.Empty(engine.ActiveAlarms);
        Assert.Empty(engine.History);

        raised.Clear(); // 只统计重置之后的触发
        engine.Process(Good("温度", 50, seconds: 1));
        Assert.Single(raised); // 重置后同一条件能重新触发
    }

    [Fact]
    public void MultipleRulesOnSamePoint_EvaluateIndependently()
    {
        var raised = new List<Alarm>();
        var cleared = new List<Alarm>();
        var engine = CreateEngine(
            AlarmRule.Create("温度", AlarmCondition.HighLimit, 45, hysteresis: 1, AlarmLevel.Critical),
            AlarmRule.Create("温度", AlarmCondition.LowLimit, 30, hysteresis: 1, AlarmLevel.Warning));
        engine.AlarmRaised += raised.Add;
        engine.AlarmCleared += cleared.Add;

        engine.Process(Good("温度", 20)); // 低温触发
        Assert.Single(raised);
        Assert.Equal(AlarmCondition.LowLimit, raised[0].Condition);

        engine.Process(Good("温度", 50, seconds: 1)); // 升温：低温恢复 + 高温触发
        Assert.Single(cleared);
        Assert.Equal(2, raised.Count);
        Assert.Equal(AlarmCondition.HighLimit, raised[1].Condition);
    }

    [Fact]
    public void UnknownPoint_IsIgnored()
    {
        var engine = CreateEngine(AlarmRule.Create("温度", AlarmCondition.HighLimit, 45, hysteresis: 1, AlarmLevel.Critical));

        engine.Process(Good("压力", 99));

        Assert.Empty(engine.ActiveAlarms);
        Assert.Empty(engine.History);
    }

    [Fact]
    public void RuleCreate_ValidatesParameters()
    {
        Assert.Throws<ArgumentException>(() =>
            AlarmRule.Create(" ", AlarmCondition.HighLimit, 45, 0, AlarmLevel.Warning));
        Assert.Throws<ArgumentException>(() =>
            AlarmRule.Create("温度", AlarmCondition.HighLimit, 45, hysteresis: -1, AlarmLevel.Warning));
        Assert.Throws<ArgumentException>(() =>
            AlarmRule.Create("温度", AlarmCondition.HighLimit, double.NaN, 0, AlarmLevel.Warning));

        // BadQuality 条件不使用阈值/回差，不做校验
        var rule = AlarmRule.Create("温度", AlarmCondition.BadQuality, double.NaN, -5, AlarmLevel.Critical);
        Assert.Equal(AlarmCondition.BadQuality, rule.Condition);
    }

    [Fact]
    public void AlarmRuleConfig_ParsesStrings_AndRejectsUnknowns()
    {
        var rule = new AlarmRuleConfig
        {
            PointName = "温度",
            Type = "highlimit", // 大小写不敏感
            Threshold = 45,
            Hysteresis = 1,
            Level = "Critical",
        }.ToRule();
        Assert.Equal(AlarmCondition.HighLimit, rule.Condition);
        Assert.Equal(AlarmLevel.Critical, rule.Level);

        Assert.Throws<FormatException>(() => new AlarmRuleConfig { PointName = "温度", Type = "Weird", Level = "Warning" }.ToRule());
        Assert.Throws<FormatException>(() => new AlarmRuleConfig { PointName = "温度", Type = "HighLimit", Level = "Fatal" }.ToRule());
    }
}
