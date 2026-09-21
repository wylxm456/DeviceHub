using System.IO;
using System.Windows;
using DeviceHub.App.ViewModels;
using DeviceHub.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DeviceHub.App;

/// <summary>
/// 应用入口：用 Generic Host 组装依赖（配置 / ViewModel），
/// 界面组件只声明依赖，不再自己 new——这是 MVVM + DI 的标准组装方式。
/// 日志用静态 Serilog 初始化（Serilog.Extensions.Hosting 10 的 UseSerilog
/// 扩展尚不支持 HostApplicationBuilder，无谓为它换回旧式 Builder）。
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF 从 IDE 启动时工作目录是项目目录，双击启动时是 exe 目录——统一锚定到 exe 目录，
        // 保证 appsettings.json 和日志目录在两种启动方式下行为一致
        Environment.CurrentDirectory = AppContext.BaseDirectory;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine("logs", "devicehub-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.Configure<HubOptions>(builder.Configuration.GetSection("Hub"));
        builder.Services.Configure<MotionConfig>(builder.Configuration.GetSection("Motion"));
        builder.Services.Configure<VisionConfig>(builder.Configuration.GetSection("Vision"));
        builder.Services.Configure<CurveConfig>(builder.Configuration.GetSection("Curve"));
        builder.Services.Configure<ReconnectConfig>(builder.Configuration.GetSection("Reconnect"));
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MotionViewModel>();
        builder.Services.AddSingleton<VisionViewModel>();
        builder.Services.AddSingleton<CurveViewModel>();

        _host = builder.Build();

        var mainWindow = new MainWindow(
            _host.Services.GetRequiredService<MainViewModel>(),
            _host.Services.GetRequiredService<MotionViewModel>(),
            _host.Services.GetRequiredService<VisionViewModel>(),
            _host.Services.GetRequiredService<CurveViewModel>());
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
