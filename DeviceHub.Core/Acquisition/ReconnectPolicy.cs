namespace DeviceHub.Core.Acquisition;

/// <summary>
/// 断线重连策略：连续失败多少个周期才触发重连、重试间隔如何递增。
/// 独立成纯逻辑类（无 IO、无线程）是为了让"策略对不对"可以脱离网络单测——
/// 网络断不断由现场决定，但阈值和退避曲线必须由我们决定。
/// </summary>
public sealed class ReconnectPolicy
{
    public ReconnectPolicy(int failureThreshold = 3, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
    {
        if (failureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold), failureThreshold, "失败阈值至少为 1。");
        }

        var b = baseDelay ?? TimeSpan.FromSeconds(1);
        var m = maxDelay ?? TimeSpan.FromSeconds(15);
        if (b <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay), b, "重试间隔必须为正。");
        }

        if (m < b)
        {
            throw new ArgumentException("重试间隔上限不能小于基础间隔。", nameof(maxDelay));
        }

        FailureThreshold = failureThreshold;
        BaseDelay = b;
        MaxDelay = m;
    }

    /// <summary>连续失败多少个采集周期才触发重连。单次网络抖动不值得惊动重连逻辑。</summary>
    public int FailureThreshold { get; }

    /// <summary>首次重试等待时间，之后按指数退避翻倍。</summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>重试等待上限。指数退避必须封顶，否则长时间断线后恢复会等得过久。</summary>
    public TimeSpan MaxDelay { get; }

    public bool ShouldAttemptReconnect(int consecutiveFailures) => consecutiveFailures >= FailureThreshold;

    /// <summary>
    /// 第 attempt 次重试（从 0 计）前应等待的时长：BaseDelay × 2^attempt，封顶 MaxDelay。
    /// 指数退避的理由：设备重启以分钟计，5 秒一次的重连风暴只会空耗；封顶的理由：
    /// 退避是"猜测"，封顶保证恢复后的最大感知延迟有界。
    /// </summary>
    public TimeSpan GetRetryDelay(int attempt)
    {
        if (attempt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "重试序号不能为负。");
        }

        // 2^20 倍早已越过任何现实配置，直接封顶——顺带杜绝左移溢出
        if (attempt >= 20)
        {
            return MaxDelay;
        }

        return TimeSpan.FromTicks(Math.Min(BaseDelay.Ticks * (1L << attempt), MaxDelay.Ticks));
    }
}

/// <summary>采集循环的工作状态。</summary>
public enum AcquisitionState
{
    /// <summary>正常采集。</summary>
    Collecting,

    /// <summary>读取连续失败，正在按退避间隔尝试重连。</summary>
    Reconnecting,
}

/// <summary>
/// 采集状态快照。Reconnecting 时 Attempt/NextRetryDelay/LastError 有意义；
/// 恢复采集时发一条 Collecting（Attempt=0），界面据此把"重连中"字样撤下来。
/// </summary>
/// <param name="State">当前状态。</param>
/// <param name="Attempt">第几次重连尝试（从 1 计）。</param>
/// <param name="NextRetryDelay">距下次重试的等待时长。</param>
/// <param name="LastError">触发重连的最后一次读取异常消息。</param>
public sealed record AcquisitionStatus(
    AcquisitionState State,
    int Attempt,
    TimeSpan NextRetryDelay,
    string? LastError = null);
