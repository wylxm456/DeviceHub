namespace DeviceHub.Vision;

/// <summary>
/// 2D 仿射变换（6 参数），九点标定的产物：像素坐标 ↔ 机台世界坐标。
/// 拟合用正规方程：x' 与 y' 各是一个 3x3 线性方程组（共享系数矩阵），
/// 克莱姆法则求解。9 个标定点远多于 6 个未知数，最小二乘天然抑制单点测量噪声。
/// </summary>
public sealed class AffineTransform2D
{
    private readonly double _a, _b, _c, _d, _e, _f;

    private AffineTransform2D(double a, double b, double c, double d, double e, double f)
    {
        _a = a;
        _b = b;
        _c = c;
        _d = d;
        _e = e;
        _f = f;
    }

    /// <summary>最小二乘拟合 source → target 的仿射变换。</summary>
    public static AffineTransform2D Fit(
        IReadOnlyList<(double X, double Y)> source,
        IReadOnlyList<(double X, double Y)> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.Count != target.Count || source.Count < 3)
        {
            throw new ArgumentException("标定至少需要 3 组不共线的对应点。");
        }

        // 正规方程 [Σx² Σxy Σx][a]   [Σx·x']
        //          [Σxy Σy² Σy][b] = [Σy·x']
        //          [Σx  Σy  N ][c]   [Σ x']
        double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0, n = 0;
        double sXtX = 0, sYtX = 0, stX = 0;
        double sXtY = 0, sYtY = 0, stY = 0;

        for (var i = 0; i < source.Count; i++)
        {
            var (x, y) = source[i];
            var (tx, ty) = target[i];

            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
            syy += y * y;
            n++;

            sXtX += x * tx;
            sYtX += y * tx;
            stX += tx;
            sXtY += x * ty;
            sYtY += y * ty;
            stY += ty;
        }

        double[,] m =
        {
            { sxx, sxy, sx },
            { sxy, syy, sy },
            { sx, sy, n },
        };

        var (a, b, c) = Solve3x3(m, sXtX, sYtX, stX);
        var (d, e, f) = Solve3x3(m, sXtY, sYtY, stY);

        return new AffineTransform2D(a, b, c, d, e, f);
    }

    public (double X, double Y) Apply(double x, double y) => (
        _a * x + _b * y + _c,
        _d * x + _e * y + _f);

    /// <summary>平均残差（映射误差），标定质量的直观指标，界面上直接展示。</summary>
    public double MeanResidual(
        IReadOnlyList<(double X, double Y)> source,
        IReadOnlyList<(double X, double Y)> target)
    {
        var sum = 0.0;
        for (var i = 0; i < source.Count; i++)
        {
            var (mx, my) = Apply(source[i].X, source[i].Y);
            var dx = mx - target[i].X;
            var dy = my - target[i].Y;
            sum += Math.Sqrt(dx * dx + dy * dy);
        }

        return sum / source.Count;
    }

    private static (double A, double B, double C) Solve3x3(
        double[,] m, double r1, double r2, double r3)
    {
        var det = Det(
            m[0, 0], m[0, 1], m[0, 2],
            m[1, 0], m[1, 1], m[1, 2],
            m[2, 0], m[2, 1], m[2, 2]);
        if (Math.Abs(det) < 1e-9)
        {
            throw new InvalidOperationException("标定点退化（近似共线），无法拟合仿射变换。");
        }

        var a = Det(r1, m[0, 1], m[0, 2], r2, m[1, 1], m[1, 2], r3, m[2, 1], m[2, 2]) / det;
        var b = Det(m[0, 0], r1, m[0, 2], m[1, 0], r2, m[1, 2], m[2, 0], r3, m[2, 2]) / det;
        var c = Det(m[0, 0], m[0, 1], r1, m[1, 0], m[1, 1], r2, m[2, 0], m[2, 1], r3) / det;
        return (a, b, c);
    }

    private static double Det(
        double m00, double m01, double m02,
        double m10, double m11, double m12,
        double m20, double m21, double m22) =>
        m00 * (m11 * m22 - m12 * m21)
        - m01 * (m10 * m22 - m12 * m20)
        + m02 * (m10 * m21 - m11 * m20);
}
