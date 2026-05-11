# 随芯调

> 随芯调是一款面向 Windows 笔记本和台式机的前台应用能效调度软件。它会以前台焦点应用为核心，根据应用配置、电源状态和安全策略，尽量在流畅度、温度、功耗和稳定性之间取得更合适的平衡。

[English README](README.en.md)

## 文档入口

- [项目总规划](docs/project-plan.md)：产品目标、架构原则、风险清单和阶段路线图。
- [安全基座](docs/safety-baseline.md)：调度事务、回滚状态和进程优先级保护规则。
- [公开仓库维护清单](docs/open-source-checklist.md)：公开提交前的隐私、构建产物和文档同步检查。
- [文档索引](docs/README.md)：文档维护规则与入口。
- [功能结构](docs/功能结构.md)：当前桌面端的用户任务结构。

## 参与项目

- [贡献指南](CONTRIBUTING.md)：代码分层、开发流程、测试和文档同步规则。
- [安全策略](SECURITY.md)：安全问题、状态污染和隐私风险的报告方式。

## 项目状态

随芯调目前处于早期原型 / MVP 阶段，已经具备可运行的 WPF 桌面端、控制台、调度配置、诊断中心、外观自定义、安全回滚、中英文语言包和基础测试。

当前阶段的目标不是突破 BIOS、显卡驱动或系统限制，而是优先使用 Windows 公开接口和用户态能力，做一个可恢复、可解释、可逐步扩展的调度平台。

## 术语约定

- 应用配置（Profile）：针对某个游戏、软件或使用场景保存的调度规则。
- 机型策略包：针对某一台电脑型号或某一类硬件组合整理的一组应用配置。
- 迁移包：用于导入导出应用配置、机型策略包和部分软件设置的打包文件。
- 自动调度：根据当前前台应用自动匹配并应用对应配置。
- 安全回滚：在异常退出、配置不稳定或用户手动触发时恢复到更安全的状态。

## 已实现能力

- 前台窗口识别：监听当前焦点窗口变化，识别进程、路径与应用信息。
- 应用配置匹配：根据进程名、可执行文件名、窗口标题等规则匹配应用配置。
- 自动调度开关：开启后会跟随前台应用变化自动应用对应策略。
- 电源方案管理：通过 Windows `powercfg` 读取、切换和恢复电源方案。
- 进程优先级管理：对前台进程应用优先级策略，并保留恢复能力。
- 安全保护：支持异常启动保护、一键回滚、退出恢复原电源方案等策略。
- 配置管理：支持 JSON 格式的应用配置保存、编辑、版本回滚、导入导出。
- 机型策略包结构：为后续“某个设备型号的一整套策略”做数据结构预留。
- 诊断中心：展示能力检测、设备指纹、显卡扩展准备状态、存储状态与最近日志。
- 外观自定义：支持窗口背景、侧边栏背景、主内容背景、主题颜色和卡片透明度。
- 本地化：使用 JSON 语言包，内置简体中文与英文。

## 计划中的扩展

- 更完整的后台程序差异化限制策略。
- 电池模式和接通电源模式下的独立策略。
- 显卡厂商扩展能力检测，以及在设备支持时启用频率/功耗相关选项。
- 机型策略包与应用配置之间的组合、移出和重新打包。
- 社区资源同步、下载、评分、复现成功率和设备匹配度。
- 目标体验配置，例如目标帧率、温度上限、噪音偏好、优先省电或优先稳定。

## 项目结构

```text
PerformanceScheduler.sln
PerformanceScheduler.slnx
src/
  PerformanceScheduler.App/             # WPF 桌面端、界面、视图模型、应用设置
  PerformanceScheduler.Core/            # 核心模型、接口、匹配逻辑与调度编排
  PerformanceScheduler.Infrastructure/  # Win32、powercfg、SQLite、文件日志等实现
tests/
  PerformanceScheduler.Tests/           # xUnit 单元测试
profiles/                               # 内置示例应用配置
community/                              # 社区目录和机型策略包示例数据
locales/                                # 语言包
docs/                                   # 设计文档
```

## 开发环境

- Windows 10 / Windows 11
- .NET 10 SDK
- VS Code 或 Visual Studio

## 启动方式

```powershell
dotnet build PerformanceScheduler.sln
dotnet run --project .\src\PerformanceScheduler.App\PerformanceScheduler.App.csproj
```

运行测试：

```powershell
dotnet test .\tests\PerformanceScheduler.Tests\PerformanceScheduler.Tests.csproj
```

## 公开仓库维护

随芯调已经是公开仓库，提交前请确认不包含本机运行数据、构建产物或个人配置。这些目录通常会在别人启动、构建或运行软件时自动重新生成。

- `bin/`、`obj/`、`artifacts/`、`publish/` 等构建输出。
- `runtime/`、`logs/`、`appearance/` 等运行期目录。
- `*.db`、`*.sqlite`、`*.sqlite3`、`*.log`、`*.dmp` 等数据库、日志和崩溃转储。
- `.env`、证书、密钥、发布配置、个人截图或包含设备隐私的信息。
- 用户自己导出的迁移包、带个人硬件信息的配置包，除非已经确认可以公开。

通常可以上传：`src/`、`tests/`、`profiles/` 中的示例配置、`community/` 中的示例目录、`locales/`、`docs/`、解决方案文件、README、贡献说明、安全说明和许可证文件。

更完整的检查项见 [公开仓库维护清单](docs/open-source-checklist.md)。

## 安全说明

随芯调会调整电源方案和进程优先级，因此项目默认强调可回滚、可解释和可禁用。不支持的设备能力应该在界面中明确置灰或提示“设备不支持该选项”。显卡频率、电压、厂商私有接口等能力会在后续以扩展方式接入，不以绕过 BIOS、驱动或系统限制为目标。

## 许可证

本项目使用 Apache License 2.0。

如果你分发随芯调或基于随芯调源码修改后的版本，请保留 `LICENSE` 和 `NOTICE` 文件，并明确说明项目基于随芯调源码修改。
