# API 与模块契约

这份文档是代码审查和后续协作的快速入口。它记录稳定的模块边界、主要输入输出、安全不变量和当前未开放能力；客户端具体实现仍以 `客户端/src/` 中的代码为准，社区接口见 [服务端说明](community-server.md)。

## 审查速读

如果只需要判断一次更新是否安全，按下面顺序阅读即可：

1. 本文的“关键数据契约”和“安全不变量”。
2. [安全基座](safety-baseline.md)。
3. [目标体验控制](target-experience.md)。
4. [多应用资源仲裁边界](resource-arbitration.md)。
5. [更新日志](../CHANGELOG.md) 的 `Unreleased` 条目。

只有涉及具体实现时，才进入对应的 `Core`、`Infrastructure` 或 `App` 文件。

## 分层边界

```text
PerformanceScheduler.App
    -> 调用 Core 契约，负责 WPF、ViewModel、状态展示和用户操作
PerformanceScheduler.Infrastructure
    -> 实现 Core 契约，负责 Windows、进程、电源、性能计数器和本地存储
PerformanceScheduler.Core
    -> 保存模型、规则、调度事务、安全判断和 Provider 抽象
```

- `Core` 不应依赖 WPF 控件、Windows 具体实现或厂商二进制。
- `Infrastructure` 可以降级返回“不支持/无数据”，不能伪造成功或把缺失值当成零。
- `App` 不应绕过 `ISchedulerActionExecutor` 直接修改电源方案或进程状态。
- 诊断、遥测和建议默认是只读的；真正的系统写入只能经过调度事务和回滚基线。

## 主要接口

客户端源码接口集中在 `客户端/src/PerformanceScheduler.Core/Abstractions`、`客户端/src/PerformanceScheduler.Core/Telemetry/Abstractions` 和 `客户端/src/PerformanceScheduler.Core/Hardware/Gpu/Abstractions`。

| 领域 | 接口 | 契约职责 |
| --- | --- | --- |
| 前台识别 | [`IForegroundWatcher`](../客户端/src/PerformanceScheduler.Core/Abstractions/IForegroundWatcher.cs) | 获取当前前台窗口；不存在时返回 `null`。 |
| 前台识别 | [`IForegroundChangeNotifier`](../客户端/src/PerformanceScheduler.Core/Abstractions/IForegroundChangeNotifier.cs) | 接收焦点变化事件；事件只触发重新解析，不直接执行系统写入。 |
| 进程识别 | [`IProcessInspector`](../客户端/src/PerformanceScheduler.Core/Abstractions/IProcessInspector.cs) | 将窗口快照解析为 `FocusedAppContext`；失败返回 `null`。 |
| Profile 选择 | [`IRuntimeProfileResolver`](../客户端/src/PerformanceScheduler.Core/Abstractions/IRuntimeProfileResolver.cs) | 按本地 Profile 和供电状态选择运行时策略。 |
| 能力检测 | [`ICapabilityDetector`](../客户端/src/PerformanceScheduler.Core/Abstractions/ICapabilityDetector.cs) | 返回 `CapabilitySnapshot`；不支持能力必须显式记录。 |
| 调度执行 | [`ISchedulerActionExecutor`](../客户端/src/PerformanceScheduler.Core/Abstractions/ISchedulerActionExecutor.cs) | 接收完整 `SchedulerActionExecutionRequest`，返回动作级结果。 |
| 电源方案 | [`IPowerPlanManager`](../客户端/src/PerformanceScheduler.Core/Abstractions/IPowerPlanManager.cs) | 读取、切换、读取高级设置、应用和恢复电源方案。 |
| 进程策略 | [`IPriorityManager`](../客户端/src/PerformanceScheduler.Core/Abstractions/IPriorityManager.cs) | 读取身份/优先级，应用前台和后台策略，并执行条件恢复。 |
| 回滚 | [`IRollbackManager`](../客户端/src/PerformanceScheduler.Core/Abstractions/IRollbackManager.cs) | 捕获状态、读取最近状态和执行恢复。 |
| 启动保护 | [`IStartupRecoveryManager`](../客户端/src/PerformanceScheduler.Core/Abstractions/IStartupRecoveryManager.cs) | 处理上次异常会话、启动保护和自动恢复。 |
| Profile 存储 | [`IProfileManager`](../客户端/src/PerformanceScheduler.Core/Abstractions/IProfileManager.cs) | 读取、保存、删除 Profile，并负责版本/快照边界。 |
| 设备策略包 | [`IDeviceStrategyPackageStore`](../客户端/src/PerformanceScheduler.Core/Abstractions/IDeviceStrategyPackageStore.cs) | 保存和读取机型策略包。 |
| 社区目录 | `ICommunityProfileCatalog` / `ICommunityDeviceStrategyCatalog` | 读取目录并原子保存远端缓存；社区内容按不可信输入处理。 |
| 运行状态 | [`IRuntimeStateStore`](../客户端/src/PerformanceScheduler.Core/Abstractions/IRuntimeStateStore.cs) | 保存活动会话、失败计数和恢复所需状态。 |
| 设备信息 | `IDeviceFingerprintProvider` / `IPowerStatusProvider` | 读取设备指纹和当前供电状态，不负责调度写入。 |
| 日志与指标 | `IAppLogger` / `IMetricsCollector` | 记录结构化事件和基础运行指标。 |
| 遥测 Provider | [`ITelemetryProvider`](../客户端/src/PerformanceScheduler.Core/Telemetry/Abstractions/ITelemetryProvider.cs) | 声明能力并采集前台目标样本；每个 Provider 独立降级。 |
| 遥测历史 | [`ITelemetryHistoryStore`](../客户端/src/PerformanceScheduler.Core/Telemetry/Abstractions/ITelemetryHistoryStore.cs) | 初始化、记录、读取和清理遥测历史。 |
| 多应用候选 | [`IResourceArbitrationCandidateProvider`](../客户端/src/PerformanceScheduler.Core/Telemetry/Abstractions/IResourceArbitrationCandidateProvider.cs) | 只读收集后台候选，不能直接改变进程状态。 |
| GPU 能力 | [`IGpuCapabilityProvider`](../客户端/src/PerformanceScheduler.Core/Hardware/Gpu/Abstractions/IGpuCapabilityProvider.cs) | 检测适配器、厂商和只读能力。 |
| GPU 扩展 | [`IGpuTuningProvider`](../客户端/src/PerformanceScheduler.Core/Hardware/Gpu/Abstractions/IGpuTuningProvider.cs) | 声明支持情况并提供锁频/复位方法；NVIDIA 已封装，AMD/Intel 写入未实现。 |

## 关键数据契约

### `SchedulerActionExecutionRequest`

调度执行必须同时提供：

- `TransactionId`：本次可回滚事务的唯一标识。
- `ActiveApp`：当前前台目标。
- `Match`：已经完成的 Profile 匹配结果。
- `Capabilities`：能力检测快照。
- `PowerSourceMode`：当前供电状态。
- `ActivePowerPlan`：执行前已知电源方案，可为空。
- `UseResourceArbitration`：多应用仲裁显式开关，默认关闭。
- `ResourceArbitration`：仲裁摘要；没有可信证据时必须阻止后台动作。

执行结果必须保留电源、前台优先级、后台调整和动作级状态，不能用一个总的“成功”覆盖部分失败。

### 进程身份

涉及进程读取、写入或恢复时，`ProcessIdentitySnapshot` 至少包含：

- PID；
- 进程启动时间；
- 进程名；
- 可用时的可执行文件路径。

PID 单独不能作为安全身份。恢复和多应用仲裁动作都必须复核进程启动时间，发现身份不一致时跳过动作并记录原因。

Windows 可执行文件路径使用 `QueryFullProcessImageNameW` 与有限查询权限读取，不以进程启动阶段可能变化的主模块列表作为身份来源。优先级读取及写后确认须清除 `Process.PriorityClass` 缓存，观察实际系统状态；相关平台读取集中在 `WindowsProcessStateReader`。

### 能力与缺失数据

- `CapabilitySnapshot` 中的 `false` 表示对应能力不可用，不得偷偷替换成另一种未声明能力。
- Provider 失败、超时、预热或无权限时保留稳定状态和原因，不生成伪造的 `0`。
- 只有状态为 `Collected` 的遥测样本进入压力、争用、趋势和目标体验判断。
- 用户界面应将不支持能力置灰或明确显示“设备不支持该选项”。

### 多应用仲裁

- `ResourceArbitrationCandidate` 是观察对象，不是写入请求。
- 受保护进程、系统分类、前台进程和没有适用策略的进程不能成为后台限制对象。
- 首次 CPU 采样没有历史基线时，使用率保持未知。
- `ResourceArbitrationActionGate` 只有在显式开启、证据充分、存在推荐且 PID 与启动时间均匹配时才允许候选进入动作准备阶段。
- 默认关闭时保持原有 Profile 后台策略行为，不改变现有用户配置。

### GPU

- GPU 能力分为未检测到适配器、只读遥测和可回滚限制准备状态。
- NVIDIA 已封装 NVML 锁频/复位并接入供电门控和自检；供电、自检完整锁频/复位及应用恢复由同一门控协调。GPU 自检只在已确认 AC 且没有待恢复状态时执行，其他情况返回 `Skipped`，全项报告为 Warning；急停模拟只使用隔离模型，不写真实 GPU。普通 Profile GPU 动作未进入调度事务，功耗墙写入、nvidia-smi 回退及 AMD/Intel 控制未实现。
- AppRuntime 注入独立 GPU 恢复存储：锁频前保存意图；部分失败和取消保留记录；全部设备复位及文件清理成功后解除。没有记录时不执行 reset。
- `gpu-clock-recovery.json` 是恢复到驱动默认值的意图，不包含稳定设备 UUID、原锁频区间或写后对账。`SchedulerState` 仍没有 GPU 字段，不能把此标记称为完整 GPU 回滚基线。
- 开放 GPU 修改前，必须具备设备身份、原始/已应用状态、写后确认、恢复条件和实机证据；不能把驱动复位默认值等同于恢复外部工具原有设置。支持条件见 [NVML 专题](nvidia-nvml-telemetry.md)。
- “检测到 GPU”不等于“可以修改 GPU”。

### CPU 与供电状态

- 当前高级电源控制仅支持 `ProcessorMaxStatePercent`（1–100）；未实现 `PROCFREQMAX` 指定 MHz，百分比不保证某个固定频率。
- 调度检测仍读取 `IPowerStatusProvider`，另有默认 150ms 的 `Win32PowerChangeWatcher` 轮询供电门控；未订阅 Windows 电源事件，不具备硬实时中断优先权。
- 供电门控串行处理补偿/释放/自检/应用恢复；首次电池状态也触发补偿，相同状态下的失败默认每 1 秒重试，供电变化立即处理。异常读取不会结束监测，未知供电保留限制并阻止新自检。启动 GPU 恢复失败时仍暂停轮询与自动调度。150ms 是采样周期，不是端到端时延或避免断电的保证。

## 取消、异常与回滚

- `CancellationToken` 取消应向上传播；用户取消不能被伪装成“不支持”。
- 单个 Provider 异常只能使该 Provider 降级，不能污染其他指标或绕过回滚。
- 任何系统写入前先持久化可验证的恢复依据；失败或取消后使用独立令牌恢复。系统状态恢复按各项依赖与当前值对账处理，不能笼统称为所有动作的逆序执行；配置迁移事务按逆序恢复参与存储。
- 恢复还需要再次校验进程身份、当前值和状态文件结构。
- 回滚不完整时禁止新的调度写入，直到保护状态被解决。
- `StartupRecoveryResult.GpuRecoveryFailed` 独立阻止恢复监控；手动回滚重试 GPU 与普通调度恢复，成功只解除对应保护。正常关闭标记前再次检查 GPU 恢复，false 和异常不算成功。

## 当前状态与未开放能力

| 能力 | 当前状态 | 说明 |
| --- | --- | --- |
| 前台 Profile 调度 | 已实现 | 通过焦点变化触发，按 Profile 和供电状态匹配。 |
| 电源方案与进程优先级 | 已实现 | 经过事务、能力检测和回滚边界。 |
| 目标体验评估 | 已实现 | 观察窗口、稳定样本、滞回和建议已具备。 |
| 多应用候选与安全门禁 | 基础已实现 | 诊断采集和门禁已具备，运行时自动注入仍未开放。 |
| GPU 只读能力 | 基础已实现 | 多适配器识别和部分只读 Provider 已接入。 |
| CPU 最大处理器状态 | 已实现 | 仅百分比限制，不提供指定 MHz 的固定频率动作。 |
| GPU 核心锁频/复位 | 已封装，未实机验收 | 门控/自检可调用；恢复意图已落盘，尚无完整原始状态基线与写后对账。 |
| GPU 功耗墙、AMD/Intel 控制 | 未实现 | 不开放加压超频，不绕过驱动限制。 |
| 供电轮询门控 | 已实现 | 默认 150ms 周期、1 秒失败重试，覆盖初始电池及异常后继续监测；与自检/恢复协调，没有硬实时保证。 |
| 社区 HTTP 同步 | 已接入 | 拉取、校验、单文件原子缓存和离线回退；两类缓存整体事务仍需补齐。 |
| 真实硬件兼容性结论 | 待实机验证 | 以[硬件验证矩阵](hardware-validation-matrix.md)的证据为准。 |

## 更新同步规则

修改以下内容时必须同步更新：

1. 修改接口签名、DTO 字段或默认值：更新本文、对应测试和 `CHANGELOG.md`。
2. 修改安全顺序、回滚条件或动作门禁：更新本文和 `safety-baseline.md`。
3. 修改目标体验或仲裁语义：更新本文和对应专题文档。
4. 增加用户可见功能：更新 `功能结构.md`、README 和本次更新日志。
5. 每次提交在日志中记录验证命令、测试数量和未完成风险。

这份文档是审查索引，不替代编译器、单元测试或真实硬件验证。
