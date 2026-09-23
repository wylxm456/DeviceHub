# DeviceHub — 多协议设备采集与监控平台

面向中小型产线"多协议设备并存、运行状态不透明、报警无追溯、数据难以对接 MES"的痛点，
设计并实现一套**配置化、插件化**的轻量设备采集与监控上位机：
南向统一接入 S7 / Modbus TCP / Modbus RTU 异构设备，北向以 OPC UA Server / MQTT
双通道对接 MES 与数据平台；中间提供实时监控、趋势曲线与历史查询、分级报警、
历史落库与 Excel 报表、登录与用户权限，并集成运动控制（模拟卡）与视觉定位引导闭环。

> 求职作品集项目，开发过程文档见 [docs/](docs/)。
> **119 项自动化测试**，含 S7 / Modbus TCP / Modbus RTU / OPC UA 四条真实协议栈的端到端集成测试。

## 功能路线图

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 骨架 | 分层解决方案、IDeviceDriver 抽象、模拟驱动、采集引擎、WPF 实时表格 | ✅ |
| M1 采集核心 | S7NetPlus + NModbus 真实驱动（TCP + RTU）、JSON 点位配置、Generic Host + DI、断线重连（阈值+指数退避）、LiveCharts 实时曲线 + 历史趋势查询 | ✅ v0.1 |
| M2 商业模块 | 报警引擎（状态机+回差+质量分离）、SQLite 历史落库（后台批量写）、登录与用户权限（急停豁免）、Excel 导出 | ✅ v0.2 |
| M3 北向对接 | OPC UA Server（点位动态节点+质量戳）、MQTT 发布（遗嘱+保留消息）；Linux 部署验证待做 | ✅ |
| M4 亮点 | InfluxDB 时序库、脚本引擎（任选，未启动） | ⏳ |
| M5 运动控制 | IMotionControl 抽象 + 模拟运动卡（回零/Jog/定位/直线插补/软限位/急停）+ 轴控界面 + 运动仿真画布 | ✅ |
| M6 视觉定位 | 合成相机 + **定位引擎插拔**（OpenCvSharp 轮廓 / Halcon 形状匹配）+ 九点标定（最小二乘仿射）+ 视觉引导运动闭环 | ✅ |

## 技术栈

- .NET 10 / C# / WPF（CommunityToolkit.Mvvm + Generic Host + DI）
- 南向采集：S7NetPlus · snap7（自研 S7 从站模拟器）· NModbus（TCP；RTU 经内存虚拟串口线全链路验证）
- 北向发布：OPCFoundation.NetStandard（OPC UA Server）· MQTTnet 5（嵌入式 Broker + 发布客户端）
- 数据：Microsoft.Data.Sqlite（命名占位符参数化 + WAL + 后台批量事务写）· ClosedXML（Excel 报表）
- 视觉：OpenCvSharp4（轮廓定位）· Halcon 12（halcondotnet，形状模板匹配）· 九点标定最小二乘
- 图表：LiveCharts2 · 日志：Serilog · 测试：xUnit

## 快速开始

```bash
dotnet build
dotnet test                               # 119 项测试（含四条真实协议栈端到端）
dotnet run --project DeviceHub.App        # 或 VS 打开 DeviceHub.slnx 多启动（Simulator + App）F5
dotnet run --project DeviceHub.Simulator  # 可选：独立 S7 从站模拟器（127.0.0.1:102）
```

**登录账号**（appsettings.json `Auth` 节可改，密码支持 `sha256:` 哈希形态）：

| 账号 | 密码 | 角色 | 能力 |
|---|---|---|---|
| engineer | engineer123 | 工程师 | 全功能（运动/视觉/标定/确认） |
| operator | operator123 | 操作员 | 看板/曲线/历史查询/报警确认；运动与视觉操作按钮按权限禁用（**急停与停止永不禁用**） |

**演示链路**：连模拟设备看实时数据 → 曲线页看趋势滚动与历史查询 → 报警页看触发/确认/导出 Excel →
运动页回零/Jog/急停/仿真画布 → 视觉页九点标定 + 视觉引导运动 → 北向验证（MQTTX 订阅
`127.0.0.1:1883` 的 `devicehub/#`；UaExpert 连 `opc.tcp://127.0.0.1:4840` 浏览 `Objects/DeviceHub`）。

**北向端口**（appsettings.json `Northbound` 节）：OPC UA `opc.tcp://127.0.0.1:4840`——
按点位表自动建节点，值与质量戳实时刷新；MQTT `127.0.0.1:1883`（进程内嵌入式 Broker）——
`devicehub/points/{点位}` 推 JSON、`devicehub/status` 上下线通告，均保留消息。

**视觉引擎切换**（appsettings.json `Vision.Locator`）：`OpenCv`（阈值+轮廓，默认）/
`Halcon`（形状模板匹配，需本机 HALCON 运行时）——标定与引导逻辑零改动复用。

## 目录结构

```
DeviceHub/
├── DeviceHub.Core/           # 抽象与模型：IDeviceDriver、采集引擎、IMotionControl、
│                             #   IVisionLocator、报警引擎、TrendBuffer、存储/导出/北向接口
├── DeviceHub.Drivers/        # 南向驱动：Simulated、S7、Modbus TCP、Modbus RTU（共享 Modbus 协议核心）
├── DeviceHub.Simulator/      # 自研 S7 从站模拟器（snap7 Server API）+ 迷你 Modbus TCP 从站
├── DeviceHub.Vision/         # 视觉：合成相机、轮廓定位器（OpenCvSharp）、九点标定
├── DeviceHub.Vision.Halcon/  # 视觉：Halon 形状匹配定位器（IVisionLocator 第二实现）
├── DeviceHub.Storage/        # SQLite 历史存储（手写参数化）+ 后台批量写入器 + Excel 导出
├── DeviceHub.Northbound/     # 北向：OPC UA Server + MQTT 发布（Generic Host 托管服务）
├── DeviceHub.App/            # WPF 界面（MVVM，五页签：采集/运动/视觉/曲线/报警 + 登录）
├── DeviceHub.Tests/          # xUnit：119 项（含四条真实协议栈端到端集成测试）
└── docs/                     # 设计文档（架构设计、里程碑计划）
```

## 核心设计

见 [docs/01-架构设计.md](docs/01-架构设计.md)：依赖倒置主线（**驱动是插件，引擎和界面是宿主**）、
质量戳设计（表格/曲线/报警/北向四处一致的"读不到是信息不是 0"）、采集流多消费者模型、
断线重连策略（阈值+指数退避）、角色→能力集权限映射（急停豁免）。

## License

[MIT](LICENSE)
