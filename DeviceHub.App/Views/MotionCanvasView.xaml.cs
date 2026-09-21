using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DeviceHub.App.ViewModels;

namespace DeviceHub.App.Views;

/// <summary>
/// 运动仿真画布：俯视 X-Y 平面 + Z 侧视高度条。
/// 绘制逻辑放在视图代码后置——自定义绘制本来就是视图的职责，
/// 它只消费 AxisViewModel 的绑定属性（100ms 轮询已驱动），不反向触碰控制层。
///
/// 坐标换算：画布 400px 代表 X/Y 各 ±300mm（0.667 px/mm），原点在画布中心；
/// Z 条 400px 代表 0~100mm（与 appsettings 中 Z 轴正软限位一致）。
/// 轨迹线记录平台走过的点位（上限 3000 点，防止长时间运行撑爆内存）。
/// </summary>
public partial class MotionCanvasView : UserControl
{
    private const double FieldSize = 400;
    private const double MmRange = 600;      // X/Y 视野 ±300mm
    private const double Scale = FieldSize / MmRange;
    private const double ZSlotHeight = 400;
    private const double ZRangeMm = 100;
    private const int MaxTrailPoints = 3000;

    private static readonly Color ColorMoving = Color.FromRgb(0xD9, 0x53, 0x4F);
    private static readonly Color ColorReady = Color.FromRgb(0x5C, 0xB8, 0x5C);
    private static readonly Color ColorIdle = Color.FromRgb(0x9E, 0x9E, 0x9E);

    private readonly Polyline _trail = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)),
        StrokeThickness = 2,
    };

    private readonly Rectangle _platform = new()
    {
        Width = 50,
        Height = 50,
        RadiusX = 4,
        RadiusY = 4,
        Fill = new SolidColorBrush(ColorIdle),
        Stroke = new SolidColorBrush(Colors.DimGray),
        StrokeThickness = 1,
    };

    private MotionViewModel? _vm;
    private AxisViewModel? _x;
    private AxisViewModel? _y;
    private AxisViewModel? _z;

    public MotionCanvasView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            DrawGrid();
            RefreshSubscription();
        };
        DataContextChanged += (_, _) => RefreshSubscription();
    }

    private void RefreshSubscription()
    {
        if (_x is not null)
        {
            _x.PropertyChanged -= AxisPropertyChanged;
        }

        if (_y is not null)
        {
            _y.PropertyChanged -= AxisPropertyChanged;
        }

        if (_z is not null)
        {
            _z.PropertyChanged -= AxisPropertyChanged;
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged -= VmPropertyChanged;
        }

        _vm = DataContext as MotionViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged += VmPropertyChanged;
        SubscribeAxes();
    }

    private void VmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 连接/断开会重建 Axes 集合，ViewModel 会通知三个轴引用变化
        if (e.PropertyName is nameof(MotionViewModel.XAxis)
            or nameof(MotionViewModel.YAxis)
            or nameof(MotionViewModel.ZAxis))
        {
            SubscribeAxes();
        }
    }

    private void SubscribeAxes()
    {
        _x = _vm?.XAxis;
        _y = _vm?.YAxis;
        _z = _vm?.ZAxis;

        if (_x is not null)
        {
            _x.PropertyChanged += AxisPropertyChanged;
        }

        if (_y is not null)
        {
            _y.PropertyChanged += AxisPropertyChanged;
        }

        if (_z is not null)
        {
            _z.PropertyChanged += AxisPropertyChanged;
        }

        _trail.Points.Clear();
        EmptyHint.Visibility = _x is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateFrame();
    }

    private void AxisPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AxisViewModel.Position)
            or nameof(AxisViewModel.IsMoving)
            or nameof(AxisViewModel.IsHomed))
        {
            UpdateFrame();
        }
    }

    private void UpdateFrame()
    {
        if (_x is null || _y is null)
        {
            return;
        }

        var center = ToScreen(_x.Position, _y.Position);
        Canvas.SetLeft(_platform, center.X - _platform.Width / 2);
        Canvas.SetTop(_platform, center.Y - _platform.Height / 2);

        _trail.Points.Add(center);
        if (_trail.Points.Count > MaxTrailPoints)
        {
            _trail.Points.RemoveAt(0);
        }

        if (_z is not null)
        {
            var zRatio = Math.Clamp(_z.Position / ZRangeMm, 0, 1);
            ZBar.Height = zRatio * ZSlotHeight;
        }

        var moving = (_x.IsMoving || _y.IsMoving || (_z?.IsMoving ?? false));
        var homed = _x.IsHomed && _y.IsHomed;
        var color = moving ? ColorMoving : homed ? ColorReady : ColorIdle;
        _platform.Fill = new SolidColorBrush(color);
    }

    private Point ToScreen(double xMm, double yMm) =>
        new(FieldSize / 2 + xMm * Scale, FieldSize / 2 + yMm * Scale);

    /// <summary>绘制背景网格（每 50mm 一格，100mm 处标数值）与原点十字。</summary>
    private void DrawGrid()
    {
        FieldCanvas.Children.Clear();
        FieldCanvas.Children.Add(_trail);
        FieldCanvas.Children.Add(_platform);

        // 空状态提示是画布子元素，清空后需重新挂回并手动居中
        FieldCanvas.Children.Add(EmptyHint);
        EmptyHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(EmptyHint, (FieldSize - EmptyHint.DesiredSize.Width) / 2);
        Canvas.SetTop(EmptyHint, FieldSize / 2);

        var gridBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        var axisBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        for (var mm = -300; mm <= 300; mm += 50)
        {
            var offset = FieldSize / 2 + mm * Scale;
            var isAxis = mm == 0;
            var brush = isAxis ? axisBrush : gridBrush;

            FieldCanvas.Children.Add(new Line
            {
                X1 = offset, Y1 = 0, X2 = offset, Y2 = FieldSize,
                Stroke = brush, StrokeThickness = isAxis ? 1.4 : 0.8,
            });
            FieldCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = offset, X2 = FieldSize, Y2 = offset,
                Stroke = brush, StrokeThickness = isAxis ? 1.4 : 0.8,
            });

            if (mm % 100 == 0)
            {
                AddLabel(mm.ToString(), offset + 2, 2);
                AddLabel(mm.ToString(), 2, offset + 2);
            }
        }

        void AddLabel(string text, double x, double y)
        {
            FieldCanvas.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = labelBrush,
            });
            var label = (TextBlock)FieldCanvas.Children[^1];
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
        }
    }

    private void ClearTrail_Click(object sender, RoutedEventArgs e)
    {
        _trail.Points.Clear();
    }
}
