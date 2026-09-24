using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceHub.Core.Acquisition;
using DeviceHub.Core.Configuration;
using DeviceHub.Core.History;
using DeviceHub.Core.Models;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using SkiaSharp;

namespace DeviceHub.App.ViewModels;

/// <summary>
/// 实时曲线页 ViewModel：采集数据的第二个消费者（第一个是采集页的表格）。
///
/// 数据流与"设备采集"页同源不同路：
///   AcquisitionEngine.PointRead(线程池) → MainViewModel 切回 UI 线程 → PointUpdated 事件 → 本页。
///   本页从头到尾不接触驱动和引擎——数据从哪台设备来，它不关心。
///
/// 双层存储是本页的核心取舍：
///   TrendBuffer（Core，无界面依赖）——无论显不显示都持续积累，是权威历史；
///   ObservableCollection&lt;DateTimePoint&gt;——只装"当前勾选"的点位，供 LiveCharts 增量刷新。
///   好处：取消勾选不丢历史，重新勾选时从缓冲区整段回填。
///
/// Bad 质量的样本不画线：趋势页宁可断线也不能伪造数据点——
/// 把读不到画成 0 或者补一条假线，是趋势页最危险的错误。
///
/// 历史查询：实时曲线只有窗口长度（内存），长周期趋势查 SQLite 落库的历史——
/// 查询结果是一次性静态序列，和实时增量序列分属两个图表，互不打架。
/// </summary>
public partial class CurveViewModel : ObservableObject
{
    // 曲线配色：按点位在点位表中的顺序循环取色，同一页最多 6 条线也够区分
    private static readonly SKColor[] Palette =
    [
        new(0xE7, 0x4C, 0x3C), // 红
        new(0x35, 0x7A, 0xB7), // 蓝
        new(0x2E, 0xCC, 0x71), // 绿
        new(0xF3, 0x9C, 0x12), // 橙
        new(0x9B, 0x59, 0xB6), // 紫
        new(0x1A, 0xBC, 0x9C), // 青
    ];

    private readonly Dictionary<string, Trace> _traces = new();
    private readonly int _capacity;
    private readonly IPointHistoryStore _pointHistoryStore;
    private readonly IHistoryExporter _exporter;
    private readonly int _maxQueryRows;
    private IReadOnlyList<PointHistoryRecord> _lastQuery = [];

    public CurveViewModel(
        MainViewModel mainViewModel,
        IOptions<CurveConfig> options,
        IOptions<HubOptions> hubOptions,
        IOptions<StorageConfig> storageOptions,
        IPointHistoryStore pointHistoryStore,
        IHistoryExporter exporter)
    {
        // 窗口长度来自配置（禁魔法数字）；配置失真时兜底到 300，不让界面崩
        _capacity = Math.Max(2, options.Value.MaxPoints);
        _pointHistoryStore = pointHistoryStore;
        _exporter = exporter;
        _maxQueryRows = Math.Max(1, storageOptions.Value.MaxQueryRows);

        // 历史查询的下拉候选 = 配置里所有设备的点位名并集（断开状态下也能查历史）
        foreach (var name in hubOptions.Value.Devices
                     .SelectMany(d => d.GetPoints())
                     .Select(p => p.Name)
                     .Distinct())
        {
            HistoryPointNames.Add(name);
        }

        SelectedHistoryPoint = HistoryPointNames.FirstOrDefault();
        SelectedRange = RangeOptions[1];

        // 双方都是 App 级单例，生命周期与进程相同——订阅不退订，不构成泄漏
        mainViewModel.AcquisitionStarted += OnAcquisitionStarted;
        mainViewModel.PointUpdated += OnPointUpdated;
        mainViewModel.AcquisitionStopped += OnAcquisitionStopped;
    }

    /// <summary>勾选了"可见"的点位序列，勾选变化即时增删曲线。</summary>
    public ObservableCollection<ISeries> Series { get; } = [];

    [ObservableProperty]
    private ObservableCollection<CurvePointOption> _pointOptions = [];

    [ObservableProperty]
    private string _statusText = "等待采集——请到「设备采集」页连接设备";

    [ObservableProperty]
    private string _pauseButtonText = "暂停";

    /// <summary>暂停期间缓冲区照常积累，只是不刷界面；恢复时从缓冲区回填，曲线无断档。</summary>
    [ObservableProperty]
    private bool _isPaused;

    // ===== 历史查询（SQLite 落库的趋势回看）=====

    /// <summary>历史查询结果序列（静态一次性绘制，与实时曲线分属两个图表）。</summary>
    public ObservableCollection<ISeries> HistorySeries { get; } = [];

    /// <summary>可查询的点位名 = 配置中所有设备点位名的并集。</summary>
    public ObservableCollection<string> HistoryPointNames { get; } = [];

    public IReadOnlyList<HistoryRange> RangeOptions { get; } =
    [
        new HistoryRange(5, "最近 5 分钟"),
        new HistoryRange(30, "最近 30 分钟"),
        new HistoryRange(60, "最近 1 小时"),
        new HistoryRange(240, "最近 4 小时"),
    ];

    [ObservableProperty]
    private string? _selectedHistoryPoint;

    [ObservableProperty]
    private HistoryRange _selectedRange;

    [ObservableProperty]
    private bool _isQuerying;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportHistoryCommand))]
    private bool _hasHistoryResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportHistoryCommand))]
    private bool _isExportingHistory;

    [ObservableProperty]
    private string _historyStatusText = "选择点位与范围后查询";

    /// <summary>查询历史趋势：SQLite 只被后台查询碰，UI 线程只等结果。</summary>
    [RelayCommand(CanExecute = nameof(CanQueryHistory))]
    private async Task QueryHistoryAsync()
    {
        if (SelectedHistoryPoint is null)
        {
            return;
        }

        IsQuerying = true;
        try
        {
            var to = DateTime.Now;
            var from = to.AddMinutes(-SelectedRange.Minutes);
            var records = await _pointHistoryStore
                .QueryAsync(SelectedHistoryPoint, from, to, _maxQueryRows)
                .ConfigureAwait(true);
            _lastQuery = records; // 导出"所见即所导"用的当前结果

            HistorySeries.Clear();
            var values = new ObservableCollection<DateTimePoint>(
                records.Select(r => new DateTimePoint(r.Timestamp, r.Value)));
            HistorySeries.Add(new LineSeries<DateTimePoint>
            {
                Name = $"{SelectedHistoryPoint}（历史 {records.Count} 点）",
                Values = values,
                Stroke = new SolidColorPaint(new SKColor(0x7F, 0x8C, 0x8D)) { StrokeThickness = 2 }, // 灰色：与实时曲线的彩色区分
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0,
            });

            HasHistoryResult = true;
            HistoryStatusText = records.Count == 0
                ? "该时间段没有历史数据（落库有约 2 秒批量写延迟）"
                : $"查询到 {records.Count} 个采样点";
        }
        catch (Exception ex)
        {
            HistoryStatusText = $"查询失败：{ex.Message}";
        }
        finally
        {
            IsQuerying = false;
        }
    }

    /// <summary>
    /// 导出当前查询结果到 Excel：所见即所导——图上画的是什么，表里就是什么，
    /// 不做第二次查询避免"图和表对不上"。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportHistory))]
    private async Task ExportHistoryAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel 工作簿|*.xlsx",
            FileName = $"点位历史_{SelectedHistoryPoint}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsExportingHistory = true;
        try
        {
            await _exporter.ExportPointHistoryAsync(_lastQuery, dialog.FileName).ConfigureAwait(true);
            HistoryStatusText = $"已导出 {_lastQuery.Count} 个采样点 → {dialog.FileName}";
        }
        catch (Exception ex)
        {
            HistoryStatusText = $"导出失败：{ex.Message}";
        }
        finally
        {
            IsExportingHistory = false;
        }
    }

    private bool CanExportHistory() => HasHistoryResult && !IsExportingHistory && _lastQuery.Count > 0;

    private bool CanQueryHistory() => !IsQuerying && SelectedHistoryPoint is not null;

    partial void OnSelectedHistoryPointChanged(string? value) => QueryHistoryCommand.NotifyCanExecuteChanged();

    /// <summary>历史查询的时间范围选项。</summary>
    public sealed record HistoryRange(int Minutes, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>时间轴：LiveCharts 的 DateTimePoint 以 DateTime.Ticks 为横坐标，标签按秒格式化。</summary>
    public Axis[] XAxes { get; } =
    [
        new Axis
        {
            Labeler = FormatTick,
            UnitWidth = TimeSpan.FromSeconds(1).Ticks,
            MinStep = TimeSpan.FromSeconds(1).Ticks,
            LabelsRotation = 15,
        },
    ];

    /// <summary>
    /// 时间轴标签格式化。图表为空或坐标轴初始化时，LiveCharts 会拿 0 附近的默认
    /// 刻度值（含负数）调用标签器——负数当 Ticks 传给 DateTime 会抛
    /// ArgumentOutOfRangeException，调试器"首次异常中断"会把整个程序按住。
    /// 守卫住：不是合法刻度就不显示标签。
    /// </summary>
    private static string FormatTick(double value) =>
        double.IsNaN(value) || value < 0 || value >= DateTime.MaxValue.Ticks
            ? string.Empty
            : new DateTime((long)value).ToString("HH:mm:ss");

    public Axis[] YAxes { get; } = [new Axis()];

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        PauseButtonText = IsPaused ? "继续" : "暂停";

        if (IsPaused)
        {
            return;
        }

        // 从暂停恢复：缓冲区在暂停期间一直进账，整段回填把断档补齐
        foreach (var trace in _traces.Values)
        {
            if (trace.Series is not null)
            {
                RefillFromBuffer(trace);
            }
        }
    }

    [RelayCommand]
    private void Clear()
    {
        foreach (var trace in _traces.Values)
        {
            trace.Buffer.Clear();
            trace.Points.Clear();
        }
    }

    private void OnAcquisitionStarted(IReadOnlyList<PointDefinition> points)
    {
        _traces.Clear();
        Series.Clear();

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            _traces[point.Name] = new Trace(point.Name, _capacity, Palette[i % Palette.Length]);
        }

        // Bool 点位是开关量（0/1 跳变），默认不勾选，避免把模拟量的纵轴压扁
        PointOptions = new ObservableCollection<CurvePointOption>(
            points.Select(p => new CurvePointOption(this, p.Name, isVisible: p.DataType != PointDataType.Bool)));

        // 构造器里给 IsVisible 赋初值不经过 setter，OnIsVisibleChanged 不会触发——
        // 默认可见的点位必须在这里显式创建序列，否则勾选框是"空勾"：看着选了，实际没有曲线
        foreach (var option in PointOptions.Where(o => o.IsVisible))
        {
            SetSeriesVisible(option.Name, visible: true);
        }

        StatusText = $"实时曲线中：{points.Count} 个点位（窗口 {_capacity} 点）";
    }

    private void OnPointUpdated(PointValue value)
    {
        if (value.Quality != PointQuality.Good || value.Value is null)
        {
            return;
        }

        if (!_traces.TryGetValue(value.Name, out var trace))
        {
            return;
        }

        trace.Buffer.Add(value.Timestamp, value.Value.Value);

        if (IsPaused || trace.Series is null)
        {
            return;
        }

        trace.Points.Add(new DateTimePoint(value.Timestamp, value.Value.Value));
        if (trace.Points.Count > _capacity)
        {
            trace.Points.RemoveAt(0);
        }
    }

    private void OnAcquisitionStopped()
    {
        StatusText = "采集已停止，曲线冻结（重新连接后刷新）";
    }

    /// <summary>勾选框切换的落点：按需增删序列。序列对象每次重建，缓冲区里的历史不丢。</summary>
    internal void SetSeriesVisible(string name, bool visible)
    {
        if (!_traces.TryGetValue(name, out var trace))
        {
            return;
        }

        if (visible)
        {
            if (trace.Series is not null)
            {
                return;
            }

            RefillFromBuffer(trace);
            trace.Series = new LineSeries<DateTimePoint>
            {
                Name = trace.Name,
                Values = trace.Points,
                Stroke = new SolidColorPaint(trace.Color) { StrokeThickness = 2 },
                Fill = null, // 只画折线不涂面积：多曲线重叠时面积填充会互相遮挡
                GeometrySize = 0,
                LineSmoothness = 0, // 直折线：平滑曲线好看，但会在采样点之间编造走势
            };
            Series.Add(trace.Series);
        }
        else
        {
            if (trace.Series is null)
            {
                return;
            }

            Series.Remove(trace.Series);
            trace.Series = null;
            trace.Points.Clear(); // 显示集合清掉省内存，权威历史在 Buffer 里
        }
    }

    private void RefillFromBuffer(Trace trace)
    {
        trace.Points.Clear();
        foreach (var (timestamp, sample) in trace.Buffer.Snapshot())
        {
            trace.Points.Add(new DateTimePoint(timestamp, sample));
        }
    }

    /// <summary>一个点位的曲线踪迹：Buffer 是权威历史，Points/Series 是给图表看的显示层。</summary>
    private sealed class Trace(string name, int capacity, SKColor color)
    {
        public TrendBuffer Buffer { get; } = new(capacity);

        public ObservableCollection<DateTimePoint> Points { get; } = [];

        public LineSeries<DateTimePoint>? Series { get; set; }

        public string Name { get; } = name;

        public SKColor Color { get; } = color;
    }
}

/// <summary>曲线页"显示点位"勾选框的行模型：勾选状态变化直接回调宿主增删序列。</summary>
public partial class CurvePointOption : ObservableObject
{
    private readonly CurveViewModel _owner;

    public CurvePointOption(CurveViewModel owner, string name, bool isVisible)
    {
        _owner = owner;
        Name = name;
        _isVisible = isVisible;
    }

    public string Name { get; }

    [ObservableProperty]
    private bool _isVisible;

    partial void OnIsVisibleChanged(bool value) => _owner.SetSeriesVisible(Name, value);
}
