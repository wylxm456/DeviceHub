using DeviceHub.Vision;
using Xunit;

namespace DeviceHub.Tests.Vision;

public class AffineTransform2DTests
{
    [Fact]
    public void Fit_IdentityPoints_ShouldMapExactly()
    {
        var points = new List<(double, double)>
        {
            (0, 0), (100, 0), (0, 100), (100, 100), (50, 25),
        };

        var transform = AffineTransform2D.Fit(points, points);

        var (x, y) = transform.Apply(73.5, 41.2);
        Assert.Equal(73.5, x, 6);
        Assert.Equal(41.2, y, 6);
        Assert.True(transform.MeanResidual(points, points) < 1e-9);
    }

    [Fact]
    public void Fit_ScaleAndTranslation_ShouldRecoverParameters()
    {
        // 像素→机台的真实场景：10 px/mm + 视场偏移
        List<(double, double)> Source() => [(0, 0), (320, 0), (0, 240), (320, 240), (160, 120)];
        var target = Source().Select(p => (p.Item1 / 10.0, p.Item2 / 10.0)).ToList();

        var transform = AffineTransform2D.Fit(Source(), target);

        var (x, y) = transform.Apply(123.0, 456.0);
        Assert.Equal(12.3, x, 9);
        Assert.Equal(45.6, y, 9);
        Assert.True(transform.MeanResidual(Source(), target) < 1e-9);
    }

    [Fact]
    public void Fit_CollinearPoints_ShouldThrow()
    {
        var collinear = new List<(double, double)> { (0, 0), (1, 1), (2, 2) };

        Assert.Throws<InvalidOperationException>(
            () => AffineTransform2D.Fit(collinear, collinear));
    }

    [Fact]
    public void Fit_TooFewPoints_ShouldThrow()
    {
        var two = new List<(double, double)> { (0, 0), (1, 1) };

        Assert.Throws<ArgumentException>(() => AffineTransform2D.Fit(two, two));
    }
}
