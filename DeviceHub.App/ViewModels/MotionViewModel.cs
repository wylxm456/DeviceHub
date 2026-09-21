using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceHub.Core.Configuration;
using DeviceHub.Core.Motion;
using DeviceHub.Drivers;
using Microsoft.Extensions.Options;

namespace DeviceHub.App.ViewModels;

/// <summary>
/// 单轴卡片：位置实时轮询 + 回零/Jog/定位/停止操作。
/// 软限位与安全联锁的拒绝信息统一冒泡到 MotionViewModel 的错误栏。
/// </summary>
public partial class AxisViewModel : ObservableObject
{
    private readonly IMotionControl _control;
    private readonly double _defaultSpeed;

    public int AxisId { get; }
    public string Name { get; }

    [ObservableProperty]
    private double _position;

    [ObservableProperty]
    private string _stateText = "未回零";

    [ObservableProperty]
    private string _targetInput = "0";

    [ObservableProperty]
    private string _speedInput;

    public AxisViewModel(IMotionControl control, MotionAxisConfig config)
    {
        _control = control;
        _defaultSpeed = config.DefaultSpeed;
        AxisId = config.Id;
        Name = string.IsNullOrWhiteSpace(config.Name) ? $"轴{config.Id}" : config.Name;
        _speedInput = config.DefaultSpeed.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>轮询线程每 100ms 调用一次，刷新位置与状态文字。</summary>
    public void UpdateFrom(AxisStatus status)
    {
        Position = status.Position;

        var state = status.IsMoving ? "运动中" : status.IsHomed ? "就绪" : "未回零";
        if (status.Alarm || status.PositiveLimitTriggered || status.NegativeLimitTriggered)
        {
            state += " | 报警(";
            if (status.PositiveLimitTriggered)
            {
                state += "正限位 ";
            }

            if (status.NegativeLimitTriggered)
            {
                state += "负限位 ";
            }

            state = state.TrimEnd() + ")";
        }

        StateText = state;
    }

    [RelayCommand]
    private Task HomeAsync() => RunAsync(() => _control.HomeAsync(AxisId));

    [RelayCommand]
    private Task StopAsync() => RunAsync(() => _control.StopAsync(AxisId));

    [RelayCommand]
    private Task JogPositiveAsync() => RunAsync(() => _control.JogStartAsync(AxisId, ParseSpeed(), +1));

    [RelayCommand]
    private Task JogNegativeAsync() => RunAsync(() => _control.JogStartAsync(AxisId, ParseSpeed(), -1));

    [RelayCommand]
    private Task MoveAbsoluteAsync() =>
        RunAsync(() => _control.MoveAbsoluteAsync(AxisId, ParseTarget(), ParseSpeed()));

    /// <summary>Jog 开始后不会自己停（除了撞软限位），点完 Jog 用"停止"键结束。</summary>
    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(true);
            ErrorText = string.Empty;
        }
        catch (Exception ex)
        {
            // 软限位/联锁拒绝是正常业务反馈，显示在轴卡片上而不是弹窗打断操作
            ErrorText = ex.Message;
        }
    }

    private double ParseTarget()
    {
        if (!double.TryParse(TargetInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"目标位置无法解析：{TargetInput}");
        }

        return value;
    }

    private double ParseSpeed()
    {
        if (!double.TryParse(SpeedInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return _defaultSpeed;
        }

        return value;
    }

    [ObservableProperty]
    private string _errorText = string.Empty;
}

/// <summary>
/// 运动控制页：连接控制卡（按配置创建）→ 构建轴卡片 → 100ms 轮询刷新位置。
/// 轮询模式与真卡开发一致（读寄存器/状态字），事件驱动留给后续优化。
/// </summary>
public partial class MotionViewModel : ObservableObject
{
    private readonly MotionConfig _config;
    private readonly System.Windows.Threading.DispatcherTimer _pollTimer;
    private IMotionControl? _control;
    private bool _polling;

    public ObservableCollection<AxisViewModel> Axes { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "未连接（当前实现：SimMotion 模拟卡）";

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public MotionViewModel(IOptions<MotionConfig> options)
    {
        _config = options.Value;
        _pollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _pollTimer.Tick += async (_, _) => await PollStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        try
        {
            _control = MotionControlFactory.Create(_config);
            await _control.ConnectAsync();

            Axes.Clear();
            foreach (var axisConfig in _config.Axes)
            {
                Axes.Add(new AxisViewModel(_control, axisConfig));
            }

            _pollTimer.Start();
            StatusText = $"已连接：{_config.Name}（{_config.DriverType}），{_config.Axes.Count} 轴";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"运动控制连接失败：{ex.Message}";
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
            _pollTimer.Stop();
            if (_control is not null)
            {
                // 断开前先急停，避免"界面断了、轴还在走"的幽灵运动
                await _control.EmergencyStopAsync();
                await _control.DisposeAsync();
                _control = null;
            }

            Axes.Clear();
            StatusText = "运动控制已断开";
        }
        finally
        {
            IsBusy = false;
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task EmergencyStopAsync()
    {
        if (_control is null)
        {
            return;
        }

        await _control.EmergencyStopAsync();
        StatusText = "已急停——所有轴停止";
    }

    private bool CanConnect() => !IsBusy;
    private bool CanDisconnect() => !IsBusy;

    private async Task PollStatusAsync()
    {
        if (_polling || _control is null)
        {
            return;
        }

        _polling = true;
        try
        {
            foreach (var axis in Axes)
            {
                var status = await _control.GetAxisStatusAsync(axis.AxisId).ConfigureAwait(true);
                axis.UpdateFrom(status);
            }
        }
        catch
        {
            // 单次轮询失败不终止界面刷新（断线场景由 Disconnect 处理）
        }
        finally
        {
            _polling = false;
        }
    }
}
