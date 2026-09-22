using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceHub.Core.Alarming;
using DeviceHub.Core.Configuration;
using DeviceHub.Core.Models;
using Microsoft.Extensions.Options;

namespace DeviceHub.App.ViewModels;

/// <summary>
/// 报警页 ViewModel：采集数据的第三个消费者（表格、曲线之后）。
/// AlarmEngine 是纯逻辑类，本类负责三件事：喂数（UI 线程契约与曲线页相同）、
/// 把引擎快照翻译成界面行、把"确认"操作递回引擎。
/// 换设备（AcquisitionStarted）时重置引擎——上一台设备的报警对下一台毫无意义。
/// </summary>
public partial class AlarmViewModel : ObservableObject
{
    /// <summary>界面事件日志上限，防止长挂刷爆内存。</summary>
    private const int MaxLogLines = 200;

    private readonly AlarmEngine _engine;
    private readonly Dictionary<long, AlarmRow> _rowsById = new();

    public AlarmViewModel(MainViewModel mainViewModel, IOptions<AlarmConfig> options)
    {
        var config = options.Value;
        _engine = new AlarmEngine(
            [.. config.Rules.Select(r => r.ToRule())],
            config.HistoryCapacity);

        _engine.AlarmRaised += OnAlarmRaised;
        _engine.AlarmCleared += OnAlarmCleared;
        _engine.AlarmAcknowledged += OnAlarmAcknowledged;

        // 双方都是 App 级单例，生命周期与进程相同——订阅不退订不构成泄漏
        mainViewModel.AcquisitionStarted += OnAcquisitionStarted;
        mainViewModel.PointUpdated += OnPointUpdated;
        mainViewModel.AcquisitionStopped += OnAcquisitionStopped;
    }

    public ObservableCollection<AlarmRow> ActiveAlarms { get; } = [];

    public ObservableCollection<string> EventLog { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeSelectedCommand))]
    private AlarmRow? _selectedAlarm;

    [ObservableProperty]
    private string _statusText = "未采集（请在「设备采集」页连接设备）";

    /// <summary>确认选中的一条。只有"活动"状态可确认，已确认的再点是无效操作（引擎返回 false）。</summary>
    [RelayCommand(CanExecute = nameof(CanAcknowledgeSelected))]
    private void AcknowledgeSelected()
    {
        if (SelectedAlarm is not null)
        {
            _engine.Acknowledge(SelectedAlarm.Id);
        }
    }

    private bool CanAcknowledgeSelected() =>
        SelectedAlarm is { StateText: "活动" };

    /// <summary>确认全部活动报警：逐条递给引擎，引擎对无效的自行拒绝。</summary>
    [RelayCommand]
    private void AcknowledgeAll()
    {
        foreach (var alarm in _engine.ActiveAlarms.Where(a => a.State == AlarmState.Active).ToList())
        {
            _engine.Acknowledge(alarm.Id);
        }
    }

    private void OnAcquisitionStarted(IReadOnlyList<DeviceHub.Core.Models.PointDefinition> points)
    {
        _engine.Reset();
        ActiveAlarms.Clear();
        EventLog.Clear();
        _rowsById.Clear();
        StatusText = "0 条活动报警";
    }

    private void OnPointUpdated(PointValue value)
    {
        // 事件已在 UI 线程（MainViewModel 再发布契约），引擎内部发的快照事件
        // 会同步回到下面的 OnAlarm* 处理器，线程全程不出 UI
        _engine.Process(value);
    }

    private void OnAcquisitionStopped() =>
        StatusText = $"采集已停止（{_engine.ActiveAlarms.Count} 条活动报警保留待处理）";

    private void OnAlarmRaised(Alarm alarm)
    {
        var row = new AlarmRow(alarm);
        _rowsById[alarm.Id] = row;
        ActiveAlarms.Insert(0, row);
        AddLog($"[触发] {alarm.Description}（{alarm.LevelText()}）");
        RefreshStatus();
    }

    private void OnAlarmCleared(Alarm alarm)
    {
        if (_rowsById.Remove(alarm.Id, out var row))
        {
            ActiveAlarms.Remove(row);
        }

        AddLog($"[恢复] {alarm.PointName} {alarm.ConditionText()}（持续 {alarm.ClearedAt - alarm.RaisedAt:hh\\:mm\\:ss}）");
        RefreshStatus();
    }

    private void OnAlarmAcknowledged(Alarm alarm)
    {
        if (_rowsById.TryGetValue(alarm.Id, out var row))
        {
            row.Refresh(alarm);
        }

        AddLog($"[确认] {alarm.PointName} {alarm.ConditionText()}");
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var total = ActiveAlarms.Count;
        var unacked = ActiveAlarms.Count(r => r.StateText == "活动");
        StatusText = total == 0 ? "0 条活动报警" : $"{total} 条活动报警（{unacked} 条未确认）";
    }

    private void AddLog(string line)
    {
        EventLog.Insert(0, $"{DateTime.Now:HH:mm:ss} {line}");
        while (EventLog.Count > MaxLogLines)
        {
            EventLog.RemoveAt(EventLog.Count - 1);
        }
    }
}

/// <summary>报警页活动列表的行模型：引擎快照的界面投影，确认时原位刷新。</summary>
public partial class AlarmRow : ObservableObject
{
    [ObservableProperty]
    private string _stateText;

    public AlarmRow(Alarm alarm)
    {
        Id = alarm.Id;
        PointName = alarm.PointName;
        ConditionText = alarm.ConditionText();
        LevelText = alarm.LevelText();
        IsCritical = alarm.Level == AlarmLevel.Critical;
        RaisedAtText = alarm.RaisedAt.ToString("HH:mm:ss");
        Description = alarm.Description;
        _stateText = "活动";
    }

    public long Id { get; }

    public string PointName { get; }

    public string ConditionText { get; }

    public string LevelText { get; }

    /// <summary>级别着色用：紧急红、警告黄。</summary>
    public bool IsCritical { get; }

    public string RaisedAtText { get; }

    public string Description { get; }

    public void Refresh(Alarm alarm) => StateText = alarm.State == AlarmState.Acknowledged ? "已确认" : "活动";
}

/// <summary>报警快照的界面文案（中文）。</summary>
internal static class AlarmTextExtensions
{
    public static string ConditionText(this Alarm alarm) => alarm.Condition switch
    {
        AlarmCondition.HighLimit => "高限",
        AlarmCondition.LowLimit => "低限",
        AlarmCondition.BadQuality => "通信",
        _ => alarm.Condition.ToString(),
    };

    public static string LevelText(this Alarm alarm) => alarm.Level switch
    {
        AlarmLevel.Critical => "紧急",
        _ => "警告",
    };
}
