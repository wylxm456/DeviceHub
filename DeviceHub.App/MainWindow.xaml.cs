using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeviceHub.App.ViewModels;

namespace DeviceHub.App;

public partial class MainWindow : Window
{
    public MainWindow(
        MainViewModel mainViewModel,
        MotionViewModel motionViewModel,
        VisionViewModel visionViewModel)
    {
        InitializeComponent();
        AcquisitionTab.DataContext = mainViewModel;
        MotionTab.DataContext = motionViewModel;
        VisionTab.DataContext = visionViewModel;
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
