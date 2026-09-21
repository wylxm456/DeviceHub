namespace DeviceHub.Core.Acquisition;

/// <summary>
/// 趋势缓冲区：一个点位的定长滚动窗口，只保留最近 Capacity 个采样。
///
/// 为什么放在 Core 而不是界面层：窗口裁剪是"数据保留策略"，属于可独立验证的
/// 核心逻辑（配合 xUnit 测试）；界面层（LiveCharts）只负责把快照画出来。
///
/// 为什么选"定长窗口"而不是"全部历史"：工控趋势页要长时间挂着，内存占用必须
/// 与运行时长无关——定长窗口让内存上界 = 容量 × 单条样本大小，可控。
///
/// 线程约定：非线程安全，读写都必须在 UI 线程（采集事件已由界面层切换），
/// 因此不加锁——错误场合加锁只会掩盖线程模型的问题。
/// </summary>
public sealed class TrendBuffer
{
    private readonly Queue<(DateTime Timestamp, double Value)> _samples;

    public TrendBuffer(int capacity)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "窗口容量至少为 2，否则画不出趋势。");
        }

        Capacity = capacity;
        _samples = new Queue<(DateTime, double)>(capacity);
    }

    /// <summary>窗口容量（最大采样数）。</summary>
    public int Capacity { get; }

    /// <summary>当前样本数，恒不超过 Capacity。</summary>
    public int Count => _samples.Count;

    /// <summary>追加一个采样；窗口已满时最旧的样本被挤掉（FIFO）。</summary>
    public void Add(DateTime timestamp, double value)
    {
        if (_samples.Count == Capacity)
        {
            _samples.Dequeue();
        }

        _samples.Enqueue((timestamp, value));
    }

    public void Clear() => _samples.Clear();

    /// <summary>
    /// 按从旧到新的顺序导出快照。界面层用它"一次性回填"——
    /// 点位重新勾选显示时，曲线能带着隐藏期间的历史整段出现。
    /// </summary>
    public IReadOnlyList<(DateTime Timestamp, double Value)> Snapshot() => _samples.ToList();
}
