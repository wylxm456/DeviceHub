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
    private readonly ReconnectPolicy _reconnectPolicy;
    private IDeviceDriver? _driver;
    private AcquisitionEngine? _engine;
    private bool _connected;

    public IReadOnlyList<DeviceConfig> Devices { get; }

    public ObservableCollection<PointRow> Points { get; } = [];

    /// <summary>采集开始（UI 线程），参数是本次采集的点位表——曲线页据此重建序列。</summary>
    public event Action<IReadOnlyList<PointDefinition>>? AcquisitionStarted;

    /// <summary>一个点位读到新值（UI 线程）——曲线页等第二个消费者从这里取数，不再自己碰引擎。</summary>
    public event Action<PointValue>? PointUpdated;

    /// <summary>采集停止（UI 线程）。</summary>
    public event Action? AcquisitionStopped;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private DeviceConfig? _selectedDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "未连接（请选择设备）";

    public MainViewModel(IOptions<HubOptions> options, IOptions<ReconnectConfig> reconnectOptions)
    {
        Devices = options.Value.Devices;
        SelectedDevice = Devices.FirstOrDefault();

        var rc = reconnectOptions.Value;
        _reconnectPolicy = new ReconnectPolicy(
            rc.FailureThreshold,
            TimeSpan.FromMilliseconds(rc.BaseDelayMs),
            TimeSpan.FromMilliseconds(rc.MaxDelayMs));
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
            // 新连接先清掉上一台设备留下的行：既避免旧点位残留，
            // 也避免新旧设备同名点位（都叫"温度"）在内部字典里互相顶替
            Points.Clear();
            _rowsByName.Clear();

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
                TimeSpan.FromMilliseconds(device.PollIntervalMs),
                _reconnectPolicy);
            _engine.PointRead += OnPointRead;
            _engine.StatusChanged += OnStatusChanged;
            _engine.Start();

            _connected = true;
            StatusText = $"采集中：{device.Name}（{device.DriverType}）@ {device.PollIntervalMs}ms";
            AcquisitionStarted?.Invoke(points);
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
                _engine.StatusChanged -= OnStatusChanged;
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
            AcquisitionStopped?.Invoke();
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
        // 事件来自线程池线程，切回 UI 线程再动 ObservableCollection；
        // PointUpdated 也在切回之后发布——消费方拿到的必然是 UI 线程事件，不必再自己切换
        _uiContext?.Post(_ =>
        {
            if (_rowsByName.TryGetValue(value.Name, out var row))
            {
                row.Update(value.Value, value.Quality, value.Timestamp);
            }

            PointUpdated?.Invoke(value);
        }, null);
    }

    private void OnStatusChanged(AcquisitionStatus status)
    {
        // 引擎事件来自后台循环线程，切回 UI 线程再动状态文本
        _uiContext?.Post(_ =>
        {
            StatusText = status.State == AcquisitionState.Reconnecting
                ? $"采集异常，重连中：第 {status.Attempt} 次，{status.NextRetryDelay.TotalSeconds:0.#}s 后重试（{status.LastError}）"
                : $"已恢复采集：{SelectedDevice?.Name}";
        }, null);
    }
}
