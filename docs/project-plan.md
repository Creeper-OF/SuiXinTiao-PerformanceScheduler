# 随芯调项目总规划

随芯调不是玄学优化器。它的目标是成为一个安全、可解释、可回滚、可扩展的 Windows 前台应用性能与能效调度平台。

项目优先级固定为：

1. 安全性：任何电源方案、进程优先级、后台限制策略都必须可回滚。
2. 稳定性：异常退出、崩溃、快速切换前台窗口、用户手动修改系统设置时不能造成状态污染。
3. 可解释性：每次调度必须能解释命中了哪个 Profile、为什么命中、执行了哪些动作、哪些动作失败、如何恢复。
4. 可维护性：保持 Core / Infrastructure / App 分层，业务逻辑不要写进 UI。
5. 产品感：普通用户能看懂当前状态，高级用户能编辑细节策略。

## 当前架构

```text
PerformanceScheduler.App
  WPF shell, views, view models, localization, settings, import/export workflow.

PerformanceScheduler.Core
  Domain models, scheduler abstractions, profile matching, orchestration services.

PerformanceScheduler.Infrastructure
  Win32 foreground detection, powercfg integration, process priority changes,
  persistence, diagnostics, rollback, startup recovery.

tests/PerformanceScheduler.Tests
  Unit tests for matching, safety recovery, persistence, catalogs, and profile tooling.
```

这套分层方向是正确的。后续重构的核心不是推倒重来，而是把危险动作收束到可审计的管线中，把 UI 中过重的状态管理逐步拆出去。

## 已完成的安全基座

第一轮安全基座已经落地：

- 调度器先捕获 baseline，再持久化 rollback state，最后执行系统动作。
- 回滚状态使用临时文件写入，再移动到目标路径，避免半截 JSON 污染状态。
- 回滚部分失败时保留未恢复项目，成功恢复的项目才从状态中移除。
- 进程优先级调整经过统一安全策略。
- 默认阻止 `RealTime` 优先级、系统伪进程、调度器自身和已知 Windows 关键进程。
- 新增安全基座文档：[Safety Baseline](safety-baseline.md)。

## 主要风险清单

| 风险 | 当前状态 | 处理方向 |
| --- | --- | --- |
| 危险动作未统一建模 | 部分缓解 | 引入动作级计划、结果、失败原因和恢复说明 |
| 用户手动修改系统设置 | 未完全处理 | 记录 original / applied / current，恢复前做对账 |
| 快速前台切换 | 部分缓解 | 使用调度事务 ID 和去重策略避免状态覆盖 |
| UI ViewModel 过重 | 未处理 | 拆出 monitoring、profiles、appearance、diagnostics 子服务 |
| Profile 风险校验不足 | 未处理 | 增加导入和保存前风险验证 |
| 社区策略可信度 | 未处理 | 加来源、兼容性、风险级别和用户确认流程 |
| GPU 扩展风险 | 未处理 | 仅做 device-gated provider，不默认启用危险能力 |

## 阶段路线图

### Phase 1: Safety Foundation

目标：确保所有系统状态修改都可解释、可回滚、失败可保留。

已完成：

- 回滚状态原子写入。
- 回滚失败保留未完成状态。
- 调度前捕获 baseline。
- 进程优先级安全策略。

下一步：

- 引入动作级 `SchedulerActionPlan` / `SchedulerActionResult`。
- 每次调度记录动作列表、目标值、原值、失败原因和恢复方式。
- 恢复前检查当前状态是否仍等于本程序应用的状态。
- 为快速前台切换增加调度事务 ID。

验收标准：

- 任意危险动作都能回答：为什么执行、执行了什么、原值是什么、目标值是什么、如何恢复。
- 崩溃后启动能自动发现未完成回滚状态。
- 部分回滚失败不会丢失剩余恢复信息。

### Phase 2: Explainability and Diagnostics

目标：让普通用户能看懂当前状态，让高级用户能追踪每次调度。

计划：

- 扩展运行记录表，保存动作级审计信息。
- UI 状态中心展示命中原因、动作结果、失败原因和恢复建议。
- Profile 匹配原因从字符串升级为结构化原因列表。
- 日志分级区分用户可读信息和开发诊断信息。

验收标准：

- 用户无需看源码即可知道某次调度为什么发生。
- 失败动作不会只显示“失败”，必须说明失败对象和可能原因。

### Phase 3: Profile Safety and Validation

目标：Profile 可以编辑，但危险配置必须被识别、限制、确认。

计划：

- 保存 Profile 前做风险验证。
- 社区 Profile 导入前做兼容性和风险扫描。
- `RealTime` 等危险优先级不进入普通用户默认选项。
- 关键进程、系统路径、未知来源策略默认不可自动调整。

验收标准：

- 高风险配置必须明确提示。
- 默认配置不会调整系统关键进程。
- 社区导入不能绕过本地安全策略。

### Phase 4: Maintainability Refactor

目标：降低 UI 层复杂度，保持 Core / Infrastructure / App 边界清晰。

计划：

- 拆分过大的 `MainWindowViewModel`。
- 把监控循环、状态中心、Profile 编辑、社区目录、外观设置拆成独立协调器或子 ViewModel。
- Core 只保留业务规则和调度编排。
- Infrastructure 只负责平台实现和持久化。

验收标准：

- UI 不直接包含系统调度业务逻辑。
- 每个模块有清晰输入输出，测试可以覆盖核心规则。

### Phase 5: Extension Platform

目标：把设备策略包、社区目录、GPU 能力扩展为可维护生态。

计划：

- 设备策略包加入版本、来源、兼容性和风险元数据。
- 社区目录加入评分、适配设备、复现结果和回滚说明。
- GPU 能力只通过 provider 接入，默认禁用不可验证能力。
- 导入导出迁移包时保留安全元数据。

验收标准：

- 任意扩展能力都必须声明支持条件、风险等级、回滚方式。
- 不支持的设备能力必须在 UI 中明确置灰或解释。

## 下一步建议

下一步最值得推进的是 Phase 1 的动作级解释模型。

当前调度结果已经能记录 summary、Profile 和部分动作结果，但还没有统一的动作计划模型。补上这层之后，后面的 UI 状态中心、日志、恢复对账、社区策略风险提示都会更自然。

建议按这个顺序实现：

1. 在 Core 增加 `SchedulerActionPlan`、`SchedulerActionResult`、`SchedulerActionKind`。
2. 让调度器先生成动作计划，再交给 Infrastructure 执行。
3. 扩展 SQLite 运行记录，保存动作级审计信息。
4. UI 先展示最近一次调度的动作列表。
5. 再处理恢复前 current-state 对账。

## 工程约束

- 不做无法解释的性能优化。
- 不默认调整系统关键进程。
- 不把业务逻辑写进 UI。
- 不绕过 Windows、BIOS、驱动的边界。
- 所有危险操作必须有保护、日志和回滚。
- 每次架构变更必须同步更新文档。
