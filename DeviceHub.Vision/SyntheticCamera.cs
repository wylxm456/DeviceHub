using DeviceHub.Core.Configuration;
using OpenCvSharp;

namespace DeviceHub.Vision;

/// <summary>
/// 合成相机：用 OpenCV 在画布上生成"带工件的机台视图"，替代真实相机取流。
/// 世界（机台坐标，mm）→ 像素是已知的线性映射（像素当量 = ScalePxPerMm），
/// 工件真实位置/角度作为 Ground Truth 保留，用于验证定位与标定精度。
/// 真实相机（海康/Basler SDK）接入时替换的是"取流"，定位与标定逻辑完全复用。
/// </summary>
public sealed class SyntheticCamera
{
    private readonly VisionConfig _config;
    private readonly Random _random = new();

    /// <summary>相机参数（标定流程需要读取视场与当量）。</summary>
    public VisionConfig Config => _config;

    /// <summary>当前工件的机台世界坐标与角度（Ground Truth）。</summary>
    public (double WorldX, double WorldY, double AngleDeg) Workpiece { get; private set; }

    public SyntheticCamera(VisionConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        NewScene();
    }

    /// <summary>摆放一个新工件。不给参数时在视场内随机放置（角度 ±30°）。</summary>
    public void NewScene(double? worldX = null, double? worldY = null, double? angleDeg = null)
    {
        var halfW = _config.WorkpieceLengthMm / 2;
        var halfH = _config.WorkpieceWidthMm / 2;
        var viewW = _config.ImageWidth / _config.ScalePxPerMm;
        var viewH = _config.ImageHeight / _config.ScalePxPerMm;

        var x = worldX ?? _config.WorldOriginX + halfW + _random.NextDouble() * (viewW - _config.WorkpieceLengthMm);
        var y = worldY ?? _config.WorldOriginY + halfH + _random.NextDouble() * (viewH - _config.WorkpieceWidthMm);
        var angle = angleDeg ?? (_random.NextDouble() * 60 - 30);

        Workpiece = (x, y, angle);
    }

    /// <summary>仅供标定等内部流程恢复场景使用。</summary>
    public void RestoreScene(double worldX, double worldY, double angleDeg) =>
        Workpiece = (worldX, worldY, angleDeg);

    /// <summary>机台坐标 → 像素坐标。</summary>
    public (double Px, double Py) WorldToPixel(double worldX, double worldY) => (
        (worldX - _config.WorldOriginX) * _config.ScalePxPerMm,
        (worldY - _config.WorldOriginY) * _config.ScalePxPerMm);

    /// <summary>像素坐标 → 机台坐标。</summary>
    public (double WorldX, double WorldY) PixelToWorld(double px, double py) => (
        px / _config.ScalePxPerMm + _config.WorldOriginX,
        py / _config.ScalePxPerMm + _config.WorldOriginY);

    /// <summary>拍一帧：机台背景 + 网格纹理 + 深灰矩形工件（带角度）。</summary>
    public Mat CaptureFrame()
    {
        var img = new Mat(_config.ImageHeight, _config.ImageWidth, MatType.CV_8UC3, Scalar.All(238));

        // 台面网格纹理（每 5mm 一条淡线），让图像有"机台感"
        for (var x = 0; x < _config.ImageWidth; x += (int)(_config.ScalePxPerMm * 5))
        {
            Cv2.Line(img, x, 0, x, _config.ImageHeight, new Scalar(222, 222, 222), 1);
        }

        for (var y = 0; y < _config.ImageHeight; y += (int)(_config.ScalePxPerMm * 5))
        {
            Cv2.Line(img, 0, y, _config.ImageWidth, y, new Scalar(222, 222, 222), 1);
        }

        // 工件：深灰旋转矩形
        var (px, py) = WorldToPixel(Workpiece.WorldX, Workpiece.WorldY);
        var rect = new RotatedRect(
            new Point2f((float)px, (float)py),
            new Size2f(
                (float)(_config.WorkpieceLengthMm * _config.ScalePxPerMm),
                (float)(_config.WorkpieceWidthMm * _config.ScalePxPerMm)),
            (float)Workpiece.AngleDeg);
        var polygon = rect.Points()
            .Select(pt => new Point((int)Math.Round(pt.X), (int)Math.Round(pt.Y)))
            .ToArray();
        Cv2.FillPoly(img, [polygon], new Scalar(88, 88, 88), LineTypes.AntiAlias);

        // 中心十字标记（白色），方便人眼核对定位是否指到工件中心
        Cv2.DrawMarker(img, new Point((int)px, (int)py), Scalar.White, MarkerTypes.Cross, 10, 1, LineTypes.AntiAlias);

        return img;
    }
}
