using System.Windows;
using DeviceHub.App.ViewModels;

namespace DeviceHub.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel mainViewModel, MotionViewModel motionViewModel)
    {
        InitializeComponent();
        AcquisitionTab.DataContext = mainViewModel;
        MotionTab.DataContext = motionViewModel;
    }
}
