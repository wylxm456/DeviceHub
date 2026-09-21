using DeviceHub.Core.Configuration;
using DeviceHub.Core.Motion;
using DeviceHub.Drivers.SimulatedMotion;

namespace DeviceHub.Drivers;

/// <summary>运动控制工厂：按配置创建控制卡实现（模拟卡 / 未来的雷赛等真卡）。</summary>
public static class MotionControlFactory
{
    public static IMotionControl Create(MotionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.DriverType.Trim().ToLowerInvariant() switch
        {
            "simmotion" => new SimMotionControl(config),
            _ => throw new NotSupportedException($"未知运动控制类型：{config.DriverType}（当前支持 SimMotion）"),
        };
    }
}
