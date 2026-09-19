using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceHub.Core.Acquisition;
using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;
using DeviceHub.Drivers.Simulated;

namespace DeviceHub.App.ViewModels;

/// <summary>
/// 主界面 ViewModel：组装驱动与采集引擎，把线程池线程上的读数切回 UI 线程。
/// M1 演进点：驱动与引擎的组装改为 Generic Host + 依赖注入，点位表改为 JSON 配置加载。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IDeviceDriver _driver = new SimulatedDriver();
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly Dictionary<string, PointRow> _rowsByName = new();
    private AcquisitionEngine? _engine;

    public ObservableCollection<PointRow> Points { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "未连接（当前驱动：Simulated 模拟设备）";

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        try
        {
            await _driver.ConnectAsync();

            foreach (var point in SimulatedDriver.DefaultPoints)
            {
                var row = new PointRow(point.Name, point.Address);
                _rowsByName[point.Name] = row;
                Points.Add(row);
            }

            _engine = new AcquisitionEngine(
                _driver,
                SimulatedDriver.DefaultPoints,
                TimeSpan.FromMilliseconds(500));
            _engine.PointRead += OnPointRead;
            _engine.ReadFailed += OnReadFailed;
            _engine.Start();

            StatusText = "采集中：模拟驱动 @ 500ms";
        }
        catch (Exception ex)
        {
            StatusText = $"连接失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            if (_engine is not null)
            {
                _engine.PointRead -= OnPointRead;
                _engine.ReadFailed -= OnReadFailed;
                await _engine.DisposeAsync();
                _engine = null;
            }

            await _driver.DisconnectAsync();
            StatusText = "已停止采集";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConnect() => !IsBusy;
    private bool CanDisconnect() => !IsBusy;

    private void OnPointRead(PointValue value)
    {
        // 事件来自线程池线程，切回 UI 线程再动 ObservableCollection
        _uiContext?.Post(_ =>
        {
            if (_rowsByName.TryGetValue(value.Name, out var row))
            {
                row.Update(value.Value, value.Quality, value.Timestamp);
            }
        }, null);
    }

    private void OnReadFailed(Exception ex)
    {
        _uiContext?.Post(_ => StatusText = $"读取异常：{ex.Message}", null);
    }
}
