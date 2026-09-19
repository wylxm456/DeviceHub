namespace DeviceHub.Core.Models;

/// <summary>点位质量。通信异常时为 Bad，界面据此置灰，M2 起接入报警引擎。</summary>
public enum PointQuality
{
    Good,
    Bad,
}

/// <summary>一次点位读取的结果。</summary>
/// <param name="Name">对应 PointDefinition.Name。</param>
/// <param name="Value">读取到的数值，读取失败时为 null。</param>
/// <param name="Quality">数据质量。</param>
/// <param name="Timestamp">采样时刻。</param>
public sealed record PointValue(string Name, double? Value, PointQuality Quality, DateTime Timestamp);
