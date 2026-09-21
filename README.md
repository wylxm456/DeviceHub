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
| M4 亮点 | InfluxDB 时序库、内置 Modbus 模拟器（迷你版已随测试交付）、脚本引擎（任选） | ⏳ |
| M5 运动控制 | IMotionControl 抽象 + 模拟运动卡（回零/Jog/定位/直线插补/软限位/急停）+ 轴控界面 + 运动仿真画布 | ✅ |
| M6 视觉定位 | 合成相机 + 轮廓定位器 + 九点标定（最小二乘仿射）+ **视觉引导运动闭环**（定位→换算→轴走位） | ✅ |

M1 进度：**双真实协议打通并接入界面**——
- 自研 [DeviceHub.Simulator](DeviceHub.Simulator/)：S7 从站模拟器（snap7 Server API）+ 迷你 Modbus TCP 从站（测试用）；
- **S7NetPlus 驱动**：真实 S7 协议栈读写 DB1，最小覆盖区间一次往返批量读；
- **NModbus TCP 驱动**：FC03/06/16，HRn / 4xxxx 双地址格式，浮点 ABCD 大端与 S7 共用转换器，2 秒传输超时防挂起；
- 界面设备下拉框 + `appsettings.json` 配置化：**新增设备只改配置不改代码**，组装走 Generic Host + DI；
- 46 项测试含两条真实协议栈的端到端集成测试；对接真机仅需在配置里更换连接参数。

## 技术栈

- .NET 10 / C# / WPF（CommunityToolkit.Mvvm）
- S7NetPlus（S7 客户端）· snap7（自研 S7 从站模拟器）· NModbus（Modbus TCP 驱动）· Modbus TCP 迷你从站模拟器
- OpenCvSharp4（视觉定位与九点标定）
- SQLite + Dapper · Serilog · xUnit · Generic Host + DI

## 快速开始

```bash
dotnet build
dotnet run --project DeviceHub.App        # 或用 Visual Studio 打开 DeviceHub.slnx 按 F5
dotnet test                               # 运行单元测试（含 S7 协议端到端集成测试）

# 可选：独立启动 S7 从站模拟器（模拟一台 S7-300，127.0.0.1:102，Rack 0 / Slot 2）
dotnet run --project DeviceHub.Simulator
```

当前版本的界面仍使用内置 SimulatedDriver（无任何依赖即点即用）；
S7 链路已由集成测试验证，M1 后续把驱动切换接入界面配置。

## 目录结构

```
DeviceHub/
├── DeviceHub.Core/        # 抽象与模型：IDeviceDriver、点位、采集引擎
├── DeviceHub.Drivers/     # 驱动实现：Simulated、S7（S7NetPlus）；M1 增 Modbus
├── DeviceHub.Simulator/   # 自研 S7 从站模拟器（snap7 Server API + 物理模型）
├── DeviceHub.App/         # WPF 界面（MVVM）
├── DeviceHub.Tests/       # xUnit 单元测试 + S7 端到端集成测试
└── docs/                  # 设计文档（架构、里程碑）
```

## 核心设计

见 [docs/01-架构设计.md](docs/01-架构设计.md)。
