using DeviceHub.Core.Configuration;

namespace DeviceHub.Vision;

/// <summary>
/// 九点标定流程（仿真执行）：把工件依次摆到机台上 3x3 网格的 9 个已知位置，
/// 每个位置拍一张、用定位器测出像素坐标，最后最小二乘拟合"像素 → 机台坐标"仿射变换。
/// 真机上的等价流程：轴依次走 9 个标定点 → 相机拍标定针 → 记录像素位 → 拟合。
/// </summary>
public static class NinePointCalibration
{
    public static (AffineTransform2D PixelToWorld, double MeanResidualMm) Run(
        SyntheticCamera camera, VisionLocator locator, double spacingMm = 20)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(locator);

        var config = camera.Config;
        var centerX = config.WorldOriginX + config.ImageWidth / config.ScalePxPerMm / 2;
        var centerY = config.WorldOriginY + config.ImageHeight / config.ScalePxPerMm / 2;

        var pixels = new List<(double X, double Y)>();
        var worlds = new List<(double X, double Y)>();
        var groundTruth = camera.Workpiece;

        try
        {
            foreach (var gy in new[] { -1, 0, 1 })
            {
                foreach (var gx in new[] { -1, 0, 1 })
                {
                    var worldX = centerX + gx * spacingMm;
                    var worldY = centerY + gy * spacingMm;

                    camera.NewScene(worldX, worldY, angleDeg: 0);
                    using var frame = camera.CaptureFrame();
                    var result = locator.Locate(frame);
                    if (!result.Found)
                    {
                        throw new InvalidOperationException(
                            $"标定点 ({worldX:0.#}, {worldY:0.#}) 处未能定位到工件，请检查视场范围。");
                    }

                    pixels.Add((result.PixelX, result.PixelY));
                    worlds.Add((worldX, worldY));
                }
            }
        }
        finally
        {
            camera.RestoreScene(groundTruth.WorldX, groundTruth.WorldY, groundTruth.AngleDeg);
        }

        var transform = AffineTransform2D.Fit(pixels, worlds);
        return (transform, transform.MeanResidual(pixels, worlds));
    }
}
