using System.IO;
using System.Windows;
using DeviceHub.App.ViewModels;
using DeviceHub.Core.Configuration;
using DeviceHub.Core.History;
using DeviceHub.Core.Security;
using DeviceHub.Northbound.Mqtt;
using DeviceHub.Northbound.OpcUa;
using DeviceHub.Storage.History;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace DeviceHub.App;

/// <summary>
/// 应用入口：用 Generic Host 组装依赖（配置 / ViewModel / 存储与后台服务），
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
        builder.Services.Configure<AlarmConfig>(builder.Configuration.GetSection("Alarms"));
        builder.Services.Configure<StorageConfig>(builder.Configuration.GetSection("Storage"));
        builder.Services.Configure<AuthConfig>(builder.Configuration.GetSection("Auth"));
        builder.Services.Configure<NorthboundConfig>(builder.Configuration.GetSection("Northbound"));
        builder.Services.AddSingleton<AuthService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<AuthConfig>>().Value;
            return new AuthService([.. config.Users.Select(u => u.ToUser())]);
        });
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MotionViewModel>();
        builder.Services.AddSingleton<VisionViewModel>();
        builder.Services.AddSingleton<CurveViewModel>();
        builder.Services.AddSingleton<AlarmViewModel>();

        // 存储：同一个 SQLite 库同时实现点位历史与报警事件两个接口——
        // 一个文件、一套连接管理、两种数据
        builder.Services.AddSingleton<SqliteHistoryStore>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<StorageConfig>>().Value;
            var path = Path.IsPathRooted(config.DatabasePath)
                ? config.DatabasePath
                : Path.Combine(AppContext.BaseDirectory, config.DatabasePath);
            return new SqliteHistoryStore(path);
        });
        builder.Services.AddSingleton<IPointHistoryStore>(sp => sp.GetRequiredService<SqliteHistoryStore>());
        builder.Services.AddSingleton<IAlarmEventStore>(sp => sp.GetRequiredService<SqliteHistoryStore>());
        builder.Services.AddSingleton<IHistoryExporter, ClosedXmlHistoryExporter>();
        builder.Services.AddSingleton<HistoryRecorder>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<StorageConfig>>().Value;
            return new HistoryRecorder(
                sp.GetRequiredService<IPointHistoryStore>(),
                sp.GetRequiredService<IAlarmEventStore>(),
                TimeSpan.FromMilliseconds(config.FlushIntervalMs));
        });
        // 同一个单例既按类型注入、又作为托管服务启停——Host 负责它的生命周期
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HistoryRecorder>());

        // OPC UA 北向服务器：托管服务常驻，采集流喂节点（首次启动自动生成自签名证书）
        builder.Services.AddSingleton<OpcUaNorthboundService>(sp =>
            new OpcUaNorthboundService(sp.GetRequiredService<IOptions<NorthboundConfig>>().Value.OpcUaPort));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OpcUaNorthboundService>());

        // MQTT 北向：嵌入式 Broker + 发布客户端（遗嘱+保留消息），采集流推 JSON
        builder.Services.AddSingleton<MqttNorthboundService>(sp =>
            new MqttNorthboundService(sp.GetRequiredService<IOptions<NorthboundConfig>>().Value.MqttPort));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttNorthboundService>());

        _host = builder.Build();

        // 登录门：模态登录成功才进主界面；关闭登录窗 = 放弃使用，应用直接退出
        var authService = _host.Services.GetRequiredService<AuthService>();
        var loginWindow = new LoginWindow(authService);
        if (loginWindow.ShowDialog() != true)
        {
            Log.Information("登录窗口被关闭，应用退出");
            _host.Dispose();
            _host = null;
            Shutdown();
            return;
        }

        var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
        var motionViewModel = _host.Services.GetRequiredService<MotionViewModel>();
        var visionViewModel = _host.Services.GetRequiredService<VisionViewModel>();
        var curveViewModel = _host.Services.GetRequiredService<CurveViewModel>();
        var alarmViewModel = _host.Services.GetRequiredService<AlarmViewModel>();

        // 历史落库：采集流的又一个消费者——UI 线程只往无锁队列丢，磁盘由后台批量写
        var recorder = _host.Services.GetRequiredService<HistoryRecorder>();
        mainViewModel.PointUpdated += value =>
            recorder.EnqueuePoint(new PointHistoryRecord(value.Name, value.Value, value.Quality, value.Timestamp));
        recorder.FlushFailed += ex => Log.Error(ex, "历史数据冲刷失败");

        // OPC UA 北向：采集开始建点位节点，读数实时刷节点值——MES/SCADA 可随时接入订阅
        var opcUa = _host.Services.GetRequiredService<OpcUaNorthboundService>();
        mainViewModel.AcquisitionStarted += points => opcUa.EnsurePoints(points.Select(p => p.Name));
        mainViewModel.PointUpdated += opcUa.UpdateFrom;

        // MQTT 北向：读数进发布队列（单读者后台泵），devicehub/points/{点位} 推 JSON
        var mqtt = _host.Services.GetRequiredService<MqttNorthboundService>();
        mainViewModel.PointUpdated += mqtt.UpdateFrom;

        var mainWindow = new MainWindow(
            mainViewModel,
            motionViewModel,
            visionViewModel,
            curveViewModel,
            alarmViewModel,
            authService);
        MainWindow = mainWindow;
        mainWindow.Show();

        // 启动托管服务（历史落库/OPC UA/MQTT）。绝不在 UI 线程等待启动完成——
        // 之前用 GetResult 同步等，宿主启动慢时（首次证书/防火墙/环境差异）主窗口
        // 整段冻结，用户看到的就是"登录后没反应"。改为后台启动：
        // 采集/曲线/报警/落库队列都不依赖宿主就绪，北向晚几秒上线无感知；
        // 失败只记日志——北向挂了不该拖死整个采集端
        _ = Task.Run(() =>
        {
            try
            {
                _host.StartAsync().GetAwaiter().GetResult();
                Log.Information("Host 已启动：历史落库/OPC UA(4840)/MQTT(1883) 全部就绪");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Host 启动失败——北向与历史落库不可用，其余功能不受影响");
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 停机冲刷走线程池防死锁（同上）；退出可以等——把队列尾数落完再关。
        // 冲刷失败只记日志，不能卡死退出路径
        try
        {
            Task.Run(() =>
            {
                if (_host is { } host)
                {
                    host.StopAsync().GetAwaiter().GetResult();
                }
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停机冲刷失败（可能有少量尾部数据未落库）");
        }

        _host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
