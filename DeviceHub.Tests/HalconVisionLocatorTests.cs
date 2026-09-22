using DeviceHub.Core.Configuration;
using DeviceHub.Vision;
using DeviceHub.Vision.Halcon;
using HalconDotNet;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Halcon 形状匹配定位器测试：精度对照合成相机的 Ground Truth，
/// 以及与 OpenCv 轮廓定位器的交叉一致性（同一帧两个引擎结果应吻合）。
/// 本机没有 HALCON 运行时（halcon.dll 不在 PATH）时自动跳过——
/// 定位器实现是插拔的，CI/别的机器默认跑 OpenCv 实现。
/// </summary>
public class HalconVisionLocatorTests : IDisposable
{
    private static readonly Lazy<bool> HalconAvailable = new(() =>
    {
        try
        {
            // 最小算子探活：能生成图像 = 本机 Halcon 可用（2026-09-22 实测无 license 文件也可跑）
            HOperatorSet.GenImageConst(out var image, "byte", 8, 8);
            image.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    });

    private readonly HalconVisionLocator _halcon = new(new VisionConfig());
    private readonly VisionLocator _openCv = new();
    private readonly SyntheticCamera _camera = new(new VisionConfig());

    public void Dispose()
    {
        _halcon.Dispose();
    }

    [Fact]
    public void Locate_MatchesGroundTruth_WithinTolerance()
    {
        if (!HalconAvailable.Value)
        {
            return; // 无 HALCON 环境：跳过
        }

        // 固定场景：位置与角度都给已知值
        _camera.NewScene(15.0, 10.0, 20.0);
        using var frame = _camera.CaptureFrame();

        var result = _halcon.Locate(frame);

        Assert.True(result.Found, $"应找到工件（score={result.Score}）");
        var (expectedPx, expectedPy) = _camera.WorldToPixel(15.0, 10.0);
        Assert.InRange(result.PixelX, expectedPx - 1.5, expectedPx + 1.5);
        Assert.InRange(result.PixelY, expectedPy - 1.5, expectedPy + 1.5);

        // 角度语义：图像 Y 轴朝下，世界系逆时针 20° 在像素系是 -20°；
        // 且矩形长边双向等价，角度按 mod 180 比较（与 OpenCv 定位器同一约定）
        var delta = Math.Abs(result.AngleDeg - (-20.0)) % 180;
        if (delta > 90)
        {
            delta = 180 - delta;
        }

        Assert.True(delta < 1.0, $"角度偏差过大：{result.AngleDeg:0.##}°（像素系期望 -20°）");
        Assert.True(result.Score > 0.7, $"匹配得分应足够高，实际 {result.Score:0.00}");
    }

    [Fact]
    public void Locate_RandomScenes_ConsistentWithOpenCvLocator()
    {
        if (!HalconAvailable.Value)
        {
            return; // 无 HALCON 环境：跳过
        }

        // 5 个随机场景：两个引擎在同一帧上的定位中心应吻合（角度语义同归一化到长边）
        for (var i = 0; i < 5; i++)
        {
            _camera.NewScene();
            using var frame = _camera.CaptureFrame();

            var byHalcon = _halcon.Locate(frame);
            var byOpenCv = _openCv.Locate(frame);

            Assert.True(byHalcon.Found && byOpenCv.Found, $"场景 {i}：两个引擎都应找到工件");
            Assert.True(Math.Abs(byHalcon.PixelX - byOpenCv.PixelX) < 1.5,
                $"场景 {i} X 不一致：Halcon={byHalcon.PixelX:0.##} OpenCv={byOpenCv.PixelX:0.##}");
            Assert.True(Math.Abs(byHalcon.PixelY - byOpenCv.PixelY) < 1.5,
                $"场景 {i} Y 不一致：Halcon={byHalcon.PixelY:0.##} OpenCv={byOpenCv.PixelY:0.##}");
        }
    }

    [Fact]
    public void Locate_BlankFrame_ReturnsNotFound()
    {
        if (!HalconAvailable.Value)
        {
            return; // 无 HALCON 环境：跳过
        }

        using var blank = new OpenCvSharp.Mat(
            new OpenCvSharp.Size(640, 480), OpenCvSharp.MatType.CV_8UC3,
            new OpenCvSharp.Scalar(238, 238, 238)); // 纯背景，无工件

        var result = _halcon.Locate(blank);

        Assert.False(result.Found);
    }
}
