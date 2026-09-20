using System.Windows;
using DeviceHub.App.ViewModels;

namespace DeviceHub.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
