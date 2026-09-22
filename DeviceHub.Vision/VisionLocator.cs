using OpenCvSharp;

namespace DeviceHub.Vision;

/// <summary>一次视觉定位的结果。</summary>
/// <param name="Found">是否找到工件。</param>
/// <param name="PixelX">工件中心像素坐标 X。</param>
/// <param name="PixelY">工件中心像素坐标 Y。</param>
/// <param name="AngleDeg">工件旋转角（度，归一化到 -90~90）。</param>
/// <param name="Score">置信度（面积占比）。</param>
public sealed record VisionResult(
    bool Found,
    double PixelX,
    double PixelY,
    double AngleDeg,
    double Score)
{
    public static VisionResult NotFound() => new(false, 0, 0, 0, 0);
}

/// <summary>
/// 视觉定位器抽象：一帧图像 → 工件中心的像素坐标与旋转角。
/// 实现可插拔（OpenCvSharp 轮廓定位 / Halcon 形状匹配定位）——
/// 定位算法与标定、引导运动的代码完全解耦，换实现只改配置。
/// </summary>
public interface IVisionLocator
{
    /// <summary>在一帧图像上定位工件。未找到返回 Found=false。</summary>
    VisionResult Locate(Mat frame);
}

/// <summary>
/// 视觉定位器：灰度 → 阈值分割 → 外轮廓 → 最大面积 → 最小外接矩形。
/// 输出工件中心的像素坐标与旋转角。算法刻意保持简单——目的是跑通
/// "取流 → 定位 → 标定换算 → 引导运动"的闭环，算法升级（模板匹配/深度学习）不动接口。
/// </summary>
public sealed class VisionLocator : IVisionLocator
{
    /// <summary>小于该面积（像素²）的轮廓视为噪点忽略。</summary>
    public double MinAreaPx { get; set; } = 200;

    public VisionResult Locate(Mat frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using var gray = new Mat();
        if (frame.Channels() == 3)
        {
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        }
        else
        {
            frame.CopyTo(gray);
        }

        // 工件（灰度 ~88）比背景（~238）暗：反二值化后工件为白
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 180, 255, ThresholdTypes.BinaryInv);

        Cv2.FindContours(
            binary,
            out var contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0)
        {
            return VisionResult.NotFound();
        }

        var largest = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
        var area = Cv2.ContourArea(largest);
        if (area < MinAreaPx)
        {
            return VisionResult.NotFound();
        }

        var rect = Cv2.MinAreaRect(largest);
        var angle = NormalizeAngle(rect);

        return new VisionResult(true, rect.Center.X, rect.Center.Y, angle, area);
    }

    /// <summary>
    /// MinAreaRect 的角度在 OpenCV 里语义别扭（可能返回短边方向）。
    /// 这里从矩形顶点显式取"长边方向"计算角度，归一化到 -90~90，
    /// 保证与摆放角度（Ground Truth）可比。
    /// </summary>
    private static double NormalizeAngle(RotatedRect rect)
    {
        var p = rect.Points();
        var edge1 = (dx: p[1].X - p[0].X, dy: p[1].Y - p[0].Y);
        var edge2 = (dx: p[2].X - p[1].X, dy: p[2].Y - p[1].Y);

        var len1Sq = edge1.dx * edge1.dx + edge1.dy * edge1.dy;
        var len2Sq = edge2.dx * edge2.dx + edge2.dy * edge2.dy;
        var (dx, dy) = len1Sq >= len2Sq ? edge1 : edge2;

        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (angle > 90)
        {
            angle -= 180;
        }

        if (angle < -90)
        {
            angle += 180;
        }

        return angle;
    }
}
