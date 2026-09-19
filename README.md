# DeviceHub — 多协议设备采集与监控平台

面向中小型产线"多协议设备并存、运行状态不透明、报警无追溯、数据难以对接 MES"的痛点，
设计并实现一套**配置化、插件化**的轻量设备采集与监控上位机：
统一接入 S7 / Modbus TCP / Modbus RTU / OPC UA 异构设备，
提供实时监控、分级报警、历史追溯与北向数据发布（OPC UA Server / MQTT）。

> 求职作品集项目，开发过程文档见 [docs/](docs/)。

## 功能路线图

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 骨架 | 解决方案分层、IDeviceDriver 抽象、模拟驱动、采集引擎、WPF 实时表格 | ✅ |
| M1 采集核心 | S7NetPlus + NModbus 真实驱动、JSON 点位配置、Generic Host + DI、LiveCharts 曲线 | 🚧 |
| M2 商业模块 | 报警引擎、SQLite 历史数据、用户权限、Excel 导出 | ⏳ |
| M3 北向对接 | OPC UA Server、MQTT 发布（MQTTnet）、Linux 虚拟机部署验证 | ⏳ |
| M4 亮点 | InfluxDB 时序库、内置 Modbus 模拟器、脚本引擎（任选） | ⏳ |

## 技术栈

- .NET 10 / C# / WPF（CommunityToolkit.Mvvm）
- S7NetPlus · NModbus · OPCFoundation.NetStandard · MQTTnet（按里程碑逐步引入）
- SQLite + Dapper · Serilog · xUnit

## 快速开始

```bash
dotnet build
dotnet run --project DeviceHub.App        # 或用 Visual Studio 打开 DeviceHub.sln 按 F5
dotnet test                               # 运行单元测试
```

当前版本使用内置的**模拟驱动**（无需任何硬件）：点击"连接并开始采集"即可看到
温度/压力/设定值三个点位以 500ms 周期刷新。

## 目录结构

```
DeviceHub/
├── DeviceHub.Core/      # 抽象与模型：IDeviceDriver、点位、采集引擎
├── DeviceHub.Drivers/   # 驱动实现：Simulated（M1 增 S7 / Modbus）
├── DeviceHub.App/       # WPF 界面（MVVM）
├── DeviceHub.Tests/     # xUnit 单元测试
└── docs/                # 设计文档（架构、里程碑）
```

## 核心设计

见 [docs/01-架构设计.md](docs/01-架构设计.md)。
