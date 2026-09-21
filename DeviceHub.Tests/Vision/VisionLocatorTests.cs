using DeviceHub.Core.Configuration;
using DeviceHub.Drivers.SimulatedMotion;
using DeviceHub.Vision;
using OpenCvSharp;
using Xunit;

namespace DeviceHub.Tests.Vision;

public class VisionLocatorTests
{
    private readonly SyntheticCamera _camera = new(new VisionConfig());
    private readonly VisionLocator _locator = new();

    [Fact]
    public void Locate_WorkpieceInScene_ShouldFindCenterWithinTolerance()
    {
        _camera.NewScene(32, 24, 20); // 视场中心附近，旋转 20°
        using var frame = _camera.CaptureFrame();

        var result = _locator.Locate(frame);

        Assert.True(result.Found);
        var (exPx, exPy) = _camera.WorldToPixel(32, 24);
        Assert.InRange(result.PixelX, exPx - 2, exPx + 2);
        Assert.InRange(result.PixelY, exPy - 2, exPy + 2);
        Assert.InRange(result.AngleDeg, 18, 22);
    }

    [Fact]
    public void Locate_BlankImage_ShouldReturnNotFound()
    {
        using var blank = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(238));

        var result = _locator.Locate(blank);

        Assert.False(result.Found);
    }

    [Fact]
    public void Locate_WorkpieceAtKnownCorner_ShouldMatchPixelTruth()
    {
        _camera.NewScene(10, 10, -25);
        using var frame = _camera.CaptureFrame();

        var result = _locator.Locate(frame);
        var (exPx, exPy) = _camera.WorldToPixel(10, 10);

        Assert.True(result.Found);
        Assert.InRange(result.PixelX, exPx - 2, exPx + 2);
        Assert.InRange(result.PixelY, exPy - 2, exPy + 2);
    }

    /// <summary>
    /// 视觉引导闭环集成测试：九点标定 → 定位 → 换算机台坐标 → 运动轴走位，
    /// 断言轴最终停在工件真实位置（Ground Truth）附近。
    /// 这就是"相机→标定→轴"三线闭环的自动化验证。
    /// </summary>
    [Fact]
    public async Task GuidedMove_VisionToMotion_ShouldStopAtWorkpiece()
    {
        // 场景：工件摆在机台 (40, 30) mm，旋转 15°
        _camera.NewScene(40, 30, 15);
        var (pixelToWorld, residual) = NinePointCalibration.Run(_camera, _locator);
        Assert.True(residual < 0.2, $"九点标定残差应小于 0.2mm，实际 {residual:0.000}");

        using var frame = _camera.CaptureFrame();
        var result = _locator.Locate(frame);
        Assert.True(result.Found);
        var (wx, wy) = pixelToWorld.Apply(result.PixelX, result.PixelY);

        // 运动轴走到视觉换算出的目标位
        var motionConfig = new MotionConfig
        {
            Name = "测试卡",
            DriverType = "SimMotion",
            Axes =
            [
                new MotionAxisConfig { Id = 0, Name = "X轴", SoftLimitPositive = 300, SoftLimitNegative = -300, DefaultSpeed = 100 },
                new MotionAxisConfig { Id = 1, Name = "Y轴", SoftLimitPositive = 300, SoftLimitNegative = -300, DefaultSpeed = 100 },
            ],
        };
        await using var motion = new SimMotionControl(motionConfig);
        await motion.ConnectAsync();
        await motion.HomeAsync(0);
        await motion.HomeAsync(1);
        await WaitIdleAsync(motion, 0);
        await WaitIdleAsync(motion, 1);

        await motion.MoveLinearAsync([0, 1], [wx, wy], 100);
        await WaitIdleAsync(motion, 0);
        await WaitIdleAsync(motion, 1);

        var xAxis = await motion.GetAxisStatusAsync(0);
        var yAxis = await motion.GetAxisStatusAsync(1);
        Assert.InRange(xAxis.Position, 40 - 0.5, 40 + 0.5);
        Assert.InRange(yAxis.Position, 30 - 0.5, 30 + 0.5);
    }

    private static async Task WaitIdleAsync(SimMotionControl control, int axis, int timeoutMs = 5000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var status = await control.GetAxisStatusAsync(axis);
        while (status.IsMoving && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
            status = await control.GetAxisStatusAsync(axis);
        }

        Assert.False(status.IsMoving, $"轴 {axis} 应在超时前停止运动");
    }
}
