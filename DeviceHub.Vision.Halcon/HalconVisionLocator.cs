using DeviceHub.Core.Configuration;
using DeviceHub.Vision;
using HalconDotNet;
using OpenCvSharp;

namespace DeviceHub.Vision.Halcon;

/// <summary>
/// Halcon 形状匹配定位器（IVisionLocator 的第二个实现）：基于
/// CreateShapeModel/FindShapeModel 的灰度形状模板匹配——Halcon 的招牌能力，
/// 对光照变化、遮挡、局部形变都比阈值+轮廓方案稳健。
///
/// 模板生成：用视觉配置（工件尺寸×像素当量）绘制与合成相机同极性的模板图
/// （亮底暗工件），首次定位时建模型并缓存；之后每帧只做 FindShapeModel。
/// 图像导入：Mat → 灰度 → GenImage1(原始指针)（Halcon 图像行紧密排列，
/// 与连续 Mat 布局一致）。
///
/// 授权事实（2026-09-22 实测）：本机 Halcon 12 的算子链（含形状匹配模块）
/// 无 license 文件也可运行——旧结论"无授权完全不可用"已翻案；换新机器时
/// 若缺本机 halcon.dll，本类调用会抛 DllNotFoundException，切回 OpenCv 即可。
/// </summary>
public sealed class HalconVisionLocator : IVisionLocator, IDisposable
{
    private readonly VisionConfig _config;
    private readonly object _modelGate = new();
    private HTuple? _modelId;

    public HalconVisionLocator(VisionConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public string Name => "Halcon 形状匹配";

    /// <summary>最低匹配得分（0~1），低于此判定未找到。</summary>
    public double MinScore { get; set; } = 0.6;

    public VisionResult Locate(Mat frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using var gray = ToGray(frame);
        if (!gray.IsContinuous())
        {
            // Halcon 图像是紧密行布局，带行填充的 Mat 必须先拷贝成连续内存
            using var continuous = gray.Clone();
            return LocateCore(continuous);
        }

        return LocateCore(gray);
    }

    private VisionResult LocateCore(Mat gray)
    {
        var model = GetOrCreateModel();

        using var image = ImportGray(gray);
        HOperatorSet.FindShapeModel(
            image, model,
            -Math.PI / 2, Math.PI,            // 模板允许 ±90°（工件实际 ±30°，留裕量）
            MinScore, 1, 0.5,                  // 最低分 / 最多 1 个 / 重叠阈值
            "least_squares", 0, 0.9,           // 亚像素最小二乘 / 金字塔层数自动 / 贪婪度
            out HTuple row, out HTuple column, out HTuple angle, out HTuple score);

        if (score.Length < 1 || score.D < MinScore)
        {
            return VisionResult.NotFound();
        }

        var angleDeg = angle.D * 180.0 / Math.PI;
        if (angleDeg > 90)
        {
            angleDeg -= 180;
        }

        if (angleDeg < -90)
        {
            angleDeg += 180;
        }

        // 工件是中心对称的矩形：矩形双向等价，匹配角与摆放角可能差 180° 的倍数，
        // 归一化后与 OpenCv 定位器的长边方向语义一致
        return new VisionResult(true, column.D, row.D, angleDeg, score.D);
    }

    /// <summary>灰度 Mat → Halcon 图像：GenImage1 直接引用像素指针，零拷贝。</summary>
    private static HObject ImportGray(Mat gray)
    {
        HOperatorSet.GenImage1(
            out HObject image, "byte", gray.Width, gray.Height, gray.Data);
        return image;
    }

    private static Mat ToGray(Mat frame)
    {
        if (frame.Channels() == 3)
        {
            var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            return gray;
        }

        var copy = new Mat();
        frame.CopyTo(copy);
        return copy;
    }

    /// <summary>按视觉配置绘制模板图（亮底暗工件，与合成相机同极性）并建形状模型。</summary>
    private HTuple GetOrCreateModel()
    {
        lock (_modelGate)
        {
            if (_modelId is { } existing)
            {
                return existing;
            }

            var widthPx = (int)Math.Round(_config.WorkpieceLengthMm * _config.ScalePxPerMm);
            var heightPx = (int)Math.Round(_config.WorkpieceWidthMm * _config.ScalePxPerMm);
            var margin = 16; // 模板四周留背景边，匹配更稳
            using var templateImage = BuildTemplate(widthPx, heightPx, margin);

            HOperatorSet.CreateShapeModel(
                templateImage, "auto",
                -Math.PI / 2, Math.PI,          // 覆盖定位时的搜索角度范围
                "auto", "auto",
                "use_polarity", "auto", "auto",
                out HTuple modelId);
            _modelId = modelId;
            return modelId;
        }
    }

    private static HObject BuildTemplate(int widthPx, int heightPx, int margin)
    {
        var imageWidth = widthPx + margin * 2;
        var imageHeight = heightPx + margin * 2;

        // 背景亮度对齐合成相机（~238），工件暗（~88）——极性一致匹配才成立
        HOperatorSet.GenImageConst(out HObject background, "byte", imageWidth, imageHeight);
        HOperatorSet.GenRectangle1(out HObject fullRect, 0, 0, imageHeight - 1, imageWidth - 1);
        HOperatorSet.PaintRegion(fullRect, background, out HObject light, 238, "fill");

        HOperatorSet.GenRectangle1(
            out HObject rect,
            margin, margin, margin + heightPx - 1, margin + widthPx - 1);
        HOperatorSet.PaintRegion(rect, light, out HObject painted, 88, "fill");

        rect.Dispose();
        fullRect.Dispose();
        background.Dispose();
        light.Dispose();
        return painted;
    }

    public void Dispose()
    {
        lock (_modelGate)
        {
            if (_modelId is { } model)
            {
                HOperatorSet.ClearShapeModel(model);
                _modelId = null;
            }
        }
    }
}
