using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeviceHub.App.ViewModels;
using DeviceHub.Core.Security;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.WPF;

namespace DeviceHub.App;

public partial class MainWindow : Window
{
    private bool _curveChartCreated;
    private readonly AuthService _auth;

    public MainWindow(
        MainViewModel mainViewModel,
        MotionViewModel motionViewModel,
        VisionViewModel visionViewModel,
        CurveViewModel curveViewModel,
        AlarmViewModel alarmViewModel,
        AuthService auth)
    {
        InitializeComponent();
        AcquisitionTab.DataContext = mainViewModel;
        MotionTab.DataContext = motionViewModel;
        VisionTab.DataContext = visionViewModel;
        CurveTab.DataContext = curveViewModel;
        AlarmTab.DataContext = alarmViewModel;
        _auth = auth;

        // 会话栏随登录/登出/切换刷新；锁定期间模态登录窗天然挡住主界面操作
        _auth.CurrentUserChanged += RefreshCurrentUser;
        RefreshCurrentUser();
    }

    private void RefreshCurrentUser()
    {
        var user = _auth.CurrentUser;
        Dispatcher.Invoke(() =>
        {
            CurrentUserText.Text = user is null
                ? "未登录"
                : $"当前用户：{user.Username}（{user.Role switch { UserRole.Engineer => "工程师", _ => "操作员" }}）";
        });
    }

    /// <summary>锁定/切换用户：模态登录窗挡住主界面，成功则切换会话，取消则保持原会话。</summary>
    private void LockButton_Click(object sender, RoutedEventArgs e)
    {
        var login = new LoginWindow(_auth) { Owner = this };
        login.ShowDialog();
        RefreshCurrentUser();
    }

    /// <summary>
    /// 首次切到"实时曲线"页时才创建图表控件：把 OpenGL 初始化从启动路径挪到
    /// 用户主动查看的时刻（见 XAML 中的注释）。实时与历史两个图一起创建——
    /// 历史图平时被 HasHistoryResult 折叠，首次查询成功后才现身。
    /// </summary>
    private void CurveTab_Selected(object sender, RoutedEventArgs e)
    {
        if (_curveChartCreated || CurveChartHost is null || !IsLoaded)
        {
            return;
        }

        _curveChartCreated = true;
        var vm = (CurveViewModel)CurveTab.DataContext;
        CurveChartHost.Children.Add(new CartesianChart
        {
            Series = vm.Series,
            XAxes = vm.XAxes,
            YAxes = vm.YAxes,
            LegendPosition = LegendPosition.Top,
            Margin = new Thickness(0),
        });
        HistoryChartHost.Children.Add(new CartesianChart
        {
            Series = vm.HistorySeries,
            XAxes = vm.XAxes,
            YAxes = vm.YAxes,
            LegendPosition = LegendPosition.Top,
            Margin = new Thickness(0),
        });
    }

    /// <summary>Jog 按住即动：按下启动连续运动（Tag 是方向 ±1）。</summary>
    private async void JogButton_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { DataContext: AxisViewModel axis, Tag: string tag }
            && int.TryParse(tag, out var direction))
        {
            await axis.JogHoldAsync(direction);
        }
    }

    /// <summary>松开即停——与真机手柄"按住走、松手停"的操作习惯一致。</summary>
    private async void JogButton_Up(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { DataContext: AxisViewModel axis })
        {
            await axis.JogHoldStopAsync();
        }
    }
}
