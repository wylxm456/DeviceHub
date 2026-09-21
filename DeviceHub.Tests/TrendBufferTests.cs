using DeviceHub.Core.Acquisition;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// 趋势缓冲区（曲线页的数据保留策略）的单测：
/// 定长 FIFO 窗口是"核心逻辑必须有测试"约定的落点——曲线页本身是 UI 不强求测，
/// 但窗口裁剪错了，趋势页就会悄悄丢数据或涨内存。
/// </summary>
public class TrendBufferTests
{
    [Fact]
    public void Add_BeyondCapacity_DropsOldestSamples()
    {
        var buffer = new TrendBuffer(capacity: 3);

        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(new DateTime(2026, 9, 21, 10, 0, 0).AddSeconds(i), i);
        }

        Assert.Equal(3, buffer.Count);
        // 最旧的 1、2 被挤掉，剩下最近的 3、4、5
        Assert.Equal([3.0, 4.0, 5.0], buffer.Snapshot().Select(s => s.Value).ToArray());
    }

    [Fact]
    public void Snapshot_ReturnsSamplesOldestToNewest()
    {
        var buffer = new TrendBuffer(capacity: 10);
        var baseTime = new DateTime(2026, 9, 21, 10, 0, 0);

        buffer.Add(baseTime.AddSeconds(2), 22);
        buffer.Add(baseTime.AddSeconds(1), 11);
        buffer.Add(baseTime.AddSeconds(3), 33);

        // 按"入队顺序"保序：Add 的调用顺序就是画线的先后，不是按时间戳排序——
        // 采集场景里样本本来就走时间正序，缓冲区不做排序（排序是开销，也是越权）
        var snapshot = buffer.Snapshot();
        Assert.Equal([22.0, 11.0, 33.0], snapshot.Select(s => s.Value).ToArray());
        Assert.Equal(3, snapshot.Count);
    }

    [Fact]
    public void Clear_RemovesAllSamplesButKeepsBufferUsable()
    {
        var buffer = new TrendBuffer(capacity: 4);

        buffer.Add(new DateTime(2026, 9, 21, 10, 0, 0), 1);
        buffer.Clear();
        Assert.Equal(0, buffer.Count);

        buffer.Add(new DateTime(2026, 9, 21, 10, 0, 1), 2);
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void Capacity_BelowTwo_Throws()
    {
        // 一个点画不出趋势，两个点才连得成线——容量下限在校验层挡住
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrendBuffer(1));
    }
}
