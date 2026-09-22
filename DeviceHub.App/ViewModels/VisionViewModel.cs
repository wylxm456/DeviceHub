using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceHub.Core.Configuration;
using DeviceHub.Core.Motion;
using DeviceHub.Core.Security;
using DeviceHub.Vision;
using Microsoft.Extensions.Options;
using OpenCvSharp;

namespace DeviceHub.App.ViewModels;

/// <summary>
/// 视觉定位页：合成相机预览 → 九点标定 → 单帧定位 → 视觉引导运动。
/// "视觉引导"是三线闭环的最后一环：相机定位工件像素坐标，
/// 标定变换换算成机台坐标，交给运动控制轴走位。
/// </summary>
public partial class VisionViewModel : ObservableObject
{
    private readonly VisionConfig _config;
    private readonly MotionViewModel _motion;
    private readonly VisionLocator _locator = new();
    private readonly DispatcherTimer _previewTimer;
    private SyntheticCamera? _camera;
    private AffineTransform2D? _pixelToWorld;

    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty]
    private ImageSource? _cameraImage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCameraCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCameraCommand))]
    [NotifyCanExecuteChangedFor(nameof(LocateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CalibrateCommand))]
    [NotifyCanExecuteChangedFor(nameof(GuidedMoveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "相机未连接";

    [ObservableProperty]
    private string _locateResultText = "—";

    [ObservableProperty]
    private string _calibrationText = "未标定（视觉引导运动需要先标定）";

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public VisionViewModel(IOptions<VisionConfig> options, MotionViewModel motion, AuthService session)
    {
        _config = options.Value;
        _motion = motion;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _previewTimer.Tick += async (_, _) => await CapturePreviewAsync().ConfigureAwait(true);

        // 视觉整页操作（连接/定位/标定/引导）需要 VisionGuide 权限；会话变化即时刷新
        CanOperateVision = session.HasPermission(Permission.VisionGuide);
        session.CurrentUserChanged += () => CanOperateVision = session.HasPermission(Permission.VisionGuide);
    }

    /// <summary>视觉操作权限门禁：整页按钮的可用性随之（操作员只读预览之外的提示）。</summary>
    [ObservableProperty]
    private bool _canOperateVision;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectCameraAsync()
    {
        IsBusy = true;
        try
        {
            _camera = new SyntheticCamera(_config);
            _previewTimer.Start();
            StatusText = $"相机已连接（合成 {_config.ImageWidth}x{_config.ImageHeight}，{_config.ScalePxPerMm} px/mm）";
            Log("相机已连接，工件已随机摆放");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectCameraAsync()
    {
        IsBusy = true;
        try
        {
            _previewTimer.Stop();
            _camera = null;
            CameraImage = null;
            StatusText = "相机已断开";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>单帧定位：在当前帧上找工件，画十字标记，换算机台坐标。</summary>
    [RelayCommand(CanExecute = nameof(CanLocate))]
    private async Task LocateAsync()
    {
        if (_camera is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            using var frame = _camera.CaptureFrame();
            var result = _locator.Locate(frame);
            if (!result.Found)
            {
                LocateResultText = "未找到工件";
                return;
            }

            DrawCrosshair(frame, result.PixelX, result.PixelY);
            CameraImage = frame.ToBitmapSource();

            var text = $"像素 ({result.PixelX:0.#}, {result.PixelY:0.#})  角度 {result.AngleDeg:0.#}°";
            if (_pixelToWorld is not null)
            {
                var (wx, wy) = _pixelToWorld.Apply(result.PixelX, result.PixelY);
                text += $"\n机台 ({wx:0.###}, {wy:0.###}) mm";
            }
            else
            {
                text += "\n（未标定，无机台坐标）";
            }

            LocateResultText = text;
            Log($"定位成功：{text.ReplaceLineEndings(" / ")}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>九点标定（仿真）：工件走 3x3 网格，拟合像素→机台仿射变换。</summary>
    [RelayCommand(CanExecute = nameof(CanLocate))]
    private async Task CalibrateAsync()
    {
        if (_camera is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (transform, residual) = NinePointCalibration.Run(_camera, _locator);
            _pixelToWorld = transform;
            CalibrationText = $"已标定（九点，平均残差 {residual:0.000} mm）";
            Log($"九点标定完成，平均残差 {residual:0.000} mm");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"标定失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 视觉引导运动（闭环演示）：定位工件 → 标定换算机台坐标 → X/Y 轴走过去。
    /// 依赖运动控制页已连接并回零。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLocate))]
    private async Task GuidedMoveAsync()
    {
        IMotionControl? motion = _motion.CurrentControl;
        if (motion is null)
        {
            ErrorMessage = "请先在\"运动控制\"页连接控制卡并回零 X/Y 轴。";
            return;
        }

        if (_camera is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            using var frame = _camera.CaptureFrame();
            var result = _locator.Locate(frame);
            if (!result.Found)
            {
                ErrorMessage = "视觉引导失败：当前帧未找到工件。";
                return;
            }

            if (_pixelToWorld is null)
            {
                ErrorMessage = "视觉引导失败：请先执行九点标定。";
                return;
            }

            var (wx, wy) = _pixelToWorld.Apply(result.PixelX, result.PixelY);
            await motion.MoveLinearAsync([0, 1], [wx, wy], 100);
            Log($"视觉引导：目标机台 ({wx:0.###}, {wy:0.###}) mm，X/Y 轴插补运动中");
            ErrorMessage = string.Empty;
            StatusText = $"视觉引导执行中 → ({wx:0.###}, {wy:0.###}) mm";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"视觉引导失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConnect() => !IsBusy;
    private bool CanDisconnect() => !IsBusy;
    private bool CanLocate() => !IsBusy && _camera is not null;

    private async Task CapturePreviewAsync()
    {
        if (_camera is null || IsBusy)
        {
            return;
        }

        using var frame = _camera.CaptureFrame();
        CameraImage = frame.ToBitmapSource();
        await Task.CompletedTask;
    }

    private static void DrawCrosshair(Mat frame, double x, double y)
    {
        Cv2.DrawMarker(frame, new Point((int)x, (int)y), new Scalar(0, 0, 255), MarkerTypes.Cross, 24, 2, LineTypes.AntiAlias);
        Cv2.Circle(frame, new Point((int)x, (int)y), 18, new Scalar(0, 0, 255), 1, LineTypes.AntiAlias);
    }

    private void Log(string message)
    {
        LogLines.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        while (LogLines.Count > 50)
        {
            LogLines.RemoveAt(LogLines.Count - 1);
        }
    }
}
