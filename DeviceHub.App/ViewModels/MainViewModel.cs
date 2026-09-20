using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceHub.Core.Acquisition;
using DeviceHub.Core.Configuration;
using DeviceHub.Core.DeviceDriver;
using DeviceHub.Core.Models;
using DeviceHub.Drivers;
using Microsoft.Extensions.Options;

namespace DeviceHub.App.ViewModels;

/// <summary>
/// 主界面 ViewModel：从配置读取设备清单，选中哪台就连哪台。
/// 驱动实例由工厂按配置创建，连接失败/断开时完整释放——
/// "加设备只改配置不改代码"在界面这一层的体现就是下拉框多一项。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly Dictionary<string, PointRow> _rowsByName = new();
    private IDeviceDriver? _driver;
    private AcquisitionEngine? _engine;
    private bool _connected;

    public IReadOnlyList<DeviceConfig> Devices { get; }

    public ObservableCollection<PointRow> Points { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private DeviceConfig? _selectedDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "未连接（请选择设备）";

    public MainViewModel(IOptions<HubOptions> options)
    {
        Devices = options.Value.Devices;
        SelectedDevice = Devices.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _driver = DeviceDriverFactory.Create(device);
            await _driver.ConnectAsync();

            var points = device.GetPoints();
            foreach (var point in points)
            {
                var row = new PointRow(point.Name, point.Address);
                _rowsByName[point.Name] = row;
                Points.Add(row);
            }

            _engine = new AcquisitionEngine(
                _driver,
                points,
                TimeSpan.FromMilliseconds(device.PollIntervalMs));
            _engine.PointRead += OnPointRead;
            _engine.ReadFailed += OnReadFailed;
            _engine.Start();

            _connected = true;
            StatusText = $"采集中：{device.Name}（{device.DriverType}）@ {device.PollIntervalMs}ms";
        }
        catch
        {
            // 连接没建立成功时把半成品驱动释放掉，不留悬挂的 socket
            if (_driver is not null)
            {
                await _driver.DisposeAsync();
                _driver = null;
            }

            StatusText = $"连接失败：{SelectedDevice?.Name}，请检查设备是否在线（模拟器/PLC 是否已启动）";
        }
        finally
        {
            IsBusy = false;
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
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

            if (_driver is not null)
            {
                await _driver.DisconnectAsync();
                await _driver.DisposeAsync();
                _driver = null;
            }

            _connected = false;
            StatusText = "已停止采集";
        }
        finally
        {
            IsBusy = false;
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanConnect() => !IsBusy && !_connected && SelectedDevice is not null;
    private bool CanDisconnect() => !IsBusy && _connected;

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
