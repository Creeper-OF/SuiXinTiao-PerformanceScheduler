# 贡献指南

感谢你愿意参与随芯调。这个项目涉及 Windows 电源方案、进程优先级和设备能力检测，因此贡献代码时请优先考虑安全、可解释和可回滚。

## 基本原则

- 不做无法解释的“玄学优化”。
- 不默认调整系统关键进程。
- 不绕过 Windows、BIOS、驱动或硬件厂商限制。
- 涉及系统状态修改的功能必须有保护、日志和回滚方案。
- 修改调度行为、回滚行为、Profile 语义或用户可见安全承诺时，必须同步更新文档。

## 分层约定

- `PerformanceScheduler.Core`：放核心模型、接口、匹配规则和调度编排。
- `PerformanceScheduler.Infrastructure`：放 Win32、`powercfg`、SQLite、文件系统等平台实现。
- `PerformanceScheduler.App`：放 WPF UI、视图模型、本地化和应用设置。

业务规则不要直接写进 UI。平台调用不要写进 Core。

## 开发流程

1. 先阅读 [项目总规划](docs/project-plan.md) 和 [安全基座](docs/safety-baseline.md)。
2. 保持改动聚焦，一次提交解决一个清晰问题。
3. 新增危险动作前，先设计 baseline、action result、失败原因和恢复方式。
4. 为核心规则和回滚路径补测试。
5. 提交前检查不要包含本机运行数据、日志、数据库、迁移包或设备隐私信息。

## 本地构建

```powershell
dotnet build PerformanceScheduler.sln
dotnet test .\tests\PerformanceScheduler.Tests\PerformanceScheduler.Tests.csproj
```

如果本机没有安装对应 .NET SDK，请先安装 README 中声明的 SDK 版本。

## 提交信息

提交信息建议简短明确，例如：

- `Add scheduler action audit model`
- `Preserve rollback state on partial failure`
- `Document profile safety validation`

## 文档

文档是项目设计的一部分，不是事后补充。

- 产品方向和路线图写入 [docs/project-plan.md](docs/project-plan.md)。
- 安全事务和回滚规则写入 [docs/safety-baseline.md](docs/safety-baseline.md)。
- UI 和用户任务结构写入 [docs/功能结构.md](docs/功能结构.md)。
