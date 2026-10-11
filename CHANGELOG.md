# 更新日志

本项目采用“中文为主、每次变更可审查”的更新记录。历史版本尚未完整整理时，不追溯编造旧版本内容；从当前快照开始建立结构化记录。

## AMD & Intel GPU 控频适配与系统托盘静默后台调度（P1 优先级完结） - 2026-10-11

### 新增与优化

- **AMD GPU 调优支持（ADLX/ADL 体系）**：
  - 引入 `IAmdGpuSource`、`AmdGpuAvailability`、`AmdGpuSnapshot`、`NullAmdGpuSource` 与 `AmdGpuSource`，动态探测 `adlx64.dll` 与 `atiadlxx.dll`，在不支持机器上平滑降级（`IsAvailable = false`）；
  - `AmdGpuTuningProvider` 完整脱离占位桩，对齐标准结构注入 `IAmdGpuSource` 与 `IGpuClockRecoveryStore`，支持并发门禁 `SemaphoreSlim`、恢复标记持久化、锁频（`TrySetLockedClocksAsync`）与重置（`TryResetLockedClocksAsync`）。
- **Intel GPU 调优支持（Level Zero Sysman 体系）**：
  - 引入 `IIntelGpuSource`、`IntelGpuAvailability`、`IntelGpuSnapshot`、`NullIntelGpuSource` 与 `IntelGpuSource`，动态探测 `ze_loader.dll` 与 `igfxcmrt64.dll`；
  - `IntelGpuTuningProvider` 同样脱离占位桩，注入 `IIntelGpuSource` 与恢复存储，提供锁频与重置支持。
- **系统托盘与静默后台自启（Tray & Silent Background Mode）**：
  - 实现纯 Win32 原生 `WindowsTrayNotificationService`（P/Invoke `Shell_NotifyIconW` + Win32 弹出菜单，零 Windows.Forms 依赖），提供左键单击/双击恢复置前主窗口、右键菜单（“显示主窗口”、“暂停/恢复调度”、“一键紧急回滚”、“退出随芯调”）、气泡通知与 `IDisposable` 彻底销毁杜绝幽灵图标；
  - 实现 `StartupCommandLineArgs`，支持解析 `--silent` / `--minimized` 命令行标志；`WindowsStartupRegistrationService` 注册开机自启项默认注入 `--silent` 参数；
  - `App.xaml.cs` 与 `MainWindow.xaml.cs` 实现后台静默启动（检测到 silent 参数时不弹出主窗口，仅托盘常驻），拦截常规关闭转为隐藏至托盘，仅托盘显式退出才终止进程。
- **本地化与约束**：
  - 追加托盘菜单双语键值（`zh-CN.json` 与 `en-US.json`）；
  - 严格维持 UI 冻结指令，零 XAML 布局修改。

### 验证

- 新增 **27 项**全新单元测试（包含 AMD/Intel Provider、命令行解析、开机注册等，客户端测试总数从 562 提升至 **589 项**，0 失败、0 跳过）；服务端 **3 项全部通过**。
- 两端 Release solution 构建均 **0 警告、0 错误**。

## Windows Job Object 资源硬限额（CPU Rate Cap & Memory Limit）调度与事务性写前回滚 - 2026-10-11

### 新增与优化

- 引入 Windows Job Object 资源硬限额核心领域模型：`JobObjectCpuRatePreference`（支持 `MaxCpuRatePercent` 与 `HardCap`）、`JobObjectMemoryLimitPreference`（支持 `MaxCommitMemoryBytes`）、`JobObjectLimitPreference` 与 `JobObjectLimitSnapshot`。
- 在 `BackgroundProcessPolicy` 与 `BackgroundProcessPriorityBaseline` 中增加 `TargetJobCpuRatePercent` 与 `TargetJobMemoryLimitBytes`，提供 `GetEffectiveJobObjectLimits()` 归一化解析器。
- 引入 `IProcessJobObjectManager` 纯接口与 `Win32ProcessJobObjectManager` 平台实现：
  - 基于 Windows 原生 Job Object API（`CreateJobObjectW`, `AssignProcessToJobObject`, `SetInformationJobObject`, `QueryInformationJobObject`）；
  - 调用 `JOBOBJECT_CPU_RATE_CONTROL_INFORMATION`（`JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP`）限制后台失控进程最大 CPU 周期占比（1.0%~100.0%）；
  - 调用 `JOBOBJECT_EXTENDED_LIMIT_INFORMATION`（`JOB_OBJECT_LIMIT_PROCESS_MEMORY`）硬性限制后台进程最大提交内存上限；
  - 维护句柄生命周期缓存，自身进程（`Environment.ProcessId`）与受保护系统关键进程排除，异常安全捕获。
- `CapabilitySnapshot` 增加 `SupportsJobObjectLimits` 标志位；`CapabilityDetector` 在 Windows 环境自动探测支持，非 Windows 记录 `JobObjectLimitsUnavailable` 原因码。
- `ProfileSafetyValidator` 增加 CPU Rate 与内存限额的范围、正数与有限浮点安全校验。
- `SchedulerActionKind` 增加 `BackgroundJobObjectLimit`；`SchedulerProcessActionHandler` 与 `SchedulerActionExecutor` 接入基线准备、动作应用与写前回滚快照（`AppliedJobObjectLimits`）。
- `FileRollbackManager` 扩展 Job Object 安全解除链路，在回滚时校验进程身份一致性并安全释放 Job 限制。
- `AppRuntime` 与 `SchedulerOrchestrator` 完成全链路依赖注入。
- `ProfileDraftViewModel` 与 `BackgroundPolicyDraftItemViewModel` 完成无损往返（Round-trip Integrity）增强，无缝保持 CPU Affinity、CPU Sets、IO/Memory Priority 与 Job Object Limits 配置。
- 严格遵循 UI 冻结指令，零 XAML 布局与菜单变动。

### 验证

- 新增 **15 项**全新单元与回归测试（客户端测试总数由 547 项提升至 **562 项**，0 失败、0 跳过）；服务端 **3 项全部通过**。
- 两端 Release solution 构建均 **0 警告、0 错误**。

## 进程 I/O 优先级与内存优先级（Disk & RAM 深度后台泄压）调度与事务性写前回滚 - 2026-10-10

### 新增与优化

- 引入 `IoPriorityLevel`（VeryLow, Low, Normal, High, Critical）与 `MemoryPriorityLevel`（VeryLow, Low, Medium, BelowNormal, Normal）核心领域模型，在 `PerformanceProfile` 中增加 `MemoryIoPriorityPreference`（支持前台与后台独立配置），并在 `BackgroundProcessPolicy` 与 `BackgroundProcessPriorityBaseline` 中增加 `TargetIoPriority` 与 `TargetMemoryPriority` 细粒度后台进程降级策略。
- 引入 `IProcessMemoryIoManager` 纯接口与 `Win32ProcessMemoryIoManager` 平台实现：
  - 基于 Windows 8+ `SetProcessInformation`/`GetProcessInformation`（`ProcessMemoryPriority = 0`，`MEMORY_PRIORITY_INFORMATION`）设置进程工作集内存修剪/分页优先级；
  - 基于 Windows NT 内核 `NtSetInformationProcess`/`NtQueryInformationProcess`（`ProcessIoPriority = 33`）设置进程存储 I/O 队列优先级；
  - 严格安全边界：包含自身进程（`Environment.ProcessId`）与系统关键进程保护（`ProtectedProcessNamePolicy`）、无效 PID 拦截、写入后回读校验（Read-back verification）及异常安全兜底。
- `CapabilitySnapshot` 增加 `SupportsMemoryIoPriority` 字段；`CapabilityDetector` 完成系统支持探测与不可用原因记录（`MemoryIoPriorityUnavailable`）。
- 扩展动作计划与编排：`SchedulerActionKind` 增加 `ForegroundIoPriority`、`BackgroundIoPriority`、`ForegroundMemoryPriority`、`BackgroundMemoryPriority`；`SchedulerProcessActionHandler` 完成基线捕获、动作计划与实际执行；`SchedulerActionExecutor` 接入写前事务回滚状态抓取。
- `FileRollbackManager` 扩展写前回滚事务通道：在 `SchedulerState` 中持久化 `OriginalIoPriorities`、`AppliedIoPriorities`、`OriginalMemoryPriorities`、`AppliedMemoryPriorities`，并在回滚时校验进程身份一致性与外部修改防覆盖（`CurrentValueMismatch`），受保护跳过已退出或被用户外部改动过的进程。
- 架构解耦与 DI 依赖组装：`SchedulerOrchestrator` 与 `AppRuntime` 完成 `Win32ProcessMemoryIoManager` 接入，零 XAML 布局变动，严格遵循 UI 冻结。

### 验证

- 新增 **13 项**回归与单元测试（客户端测试总数由 534 提升至 **547 项**，0 失败、0 跳过）；服务端 **3 项全部通过**。
- 两端 Release solution 构建均 **0 警告、0 错误**。

## CPU Sets（软亲和性 / 默认调度集）集成与事务性写前回滚 - 2026-10-09

### 新增与优化

- 引入 `CpuSetPreference` 领域模型（支持 `ForegroundCpuSets` 与 `BackgroundCpuSets`），并在 `PerformanceProfile` 与 `BackgroundProcessPolicy`（`TargetCpuSets`）中提供细粒度软亲和性 CPU 默认调度集配置能力。
- 引入 `ICpuSetManager` 纯接口与 `Win32CpuSetManager` 平台实现，调用 Win10 Build 14393+ `GetProcessDefaultCpuSets`/`SetProcessDefaultCpuSets` 原生 API，包含系统受保护进程与自身进程保护、集合等价性比对以及写入后回读验证。
- `CapabilityDetector` 增加 `SupportsCpuSets` 能力探测；`SchedulerProcessActionHandler` 接入前台与后台 CPU Sets 动作计划与执行；`SchedulerActionExecutor` 接入写前事务回滚状态抓取。
- `FileRollbackManager` 强化写前回滚完整性：同时支持 CPU 亲和性与 CPU Sets 回滚、引入并发互斥门禁 `SemaphoreSlim`、结构与孤立条目校验 `ValidateState`、读回验证与用户外部篡改防护。
- 严格遵循 UI 冻结指令，零 XAML 布局与菜单变动。

### 验证

- 新增 **5 项**回归测试（客户端测试总数由 529 提升至 **534 项**，0 失败、0 跳过）；服务端 **3 项通过**。
- 两端 Release solution 构建均 **0 警告、0 错误**。

## 细粒度 CPU 亲和性掩码调度与事务性写前回滚 - 2026-10-09

### 新增与优化

- 引入 CpuAffinityPreference 领域模型（支持 ForegroundAffinityMask 与 BackgroundAffinityMask），并在 PerformanceProfile 与 BackgroundProcessPolicy（TargetAffinityMask）中提供细粒度核心掩码配置能力。
- 引入 ICpuAffinityManager 纯接口与 Win32ProcessAffinityManager 平台实现，包含关键系统进程与自身排除、可用 CPU 核心数截断（Math.Clamp(Environment.ProcessorCount, 1, 64)）及写入后回读验证。
- SchedulerProcessActionHandler 接入前台与后台 CPU 亲和性计划编排与应用；SchedulerActionExecutor 实现写前回滚状态抓取。
- FileRollbackManager 扩展 CPU 亲和性基线持久化、进程身份（PID + 启动时间）校验、用户外部修改防覆盖（CurrentValueMismatch）及安全恢复事务。
- ProfileDraftViewModel 增加亲和性属性编辑与草稿往返校验；严格保持 UI 冻结无 XAML 变动。

### 验证

- 新增 **5 项**回归测试（客户端测试总数由 524 提升至 **529 项**，0 失败、0 跳过）；服务端 **3 项通过**。
- 两端 Release solution 构建均 **0 警告、0 错误**。

## 目标体验轻量闭环自适应微调 - 2026-10-09

### 新增与优化

- 引入 `TargetExperienceAutoTuningEnvelope`、`TargetExperienceAutoTuningPlan` 与 `TargetExperienceAdaptiveResult`，确立 60%~100% 处理器最大频率安全包络、5% 单次步进与 10 秒冷却防振荡规范。
- `TargetExperienceController` 在 Profile 显式启用 `ControlMode = Automatic` 时放行 `CanApplyAutomatically = true` 建议；`TargetExperienceAutoTuner` 完成速率与边界受控计算。
- 引入 `ITargetExperienceAdaptiveCoordinator` 与 `TargetExperienceAdaptiveCoordinator`，负责快照当前会话初始电源高级状态基线、冷却计时器跟踪与事务性调用电源管理接口执行微调，并在会话退出时无条件重置恢复基线。
- `SchedulerOrchestrator` 接入 `AdaptTargetExperienceAsync` 编排入口与会话重置联动；`AppRuntime` 完成依赖注入，`MainWindowViewModel.Diagnostics` 实时触发自适应决策并输出本地化诊断日志。
- 严格遵循 UI 冻结指令，零 XAML 布局与菜单变动。

### 验证

- 新增 **16 项**回归测试（客户端测试总数由 508 提升至 **524 项**，0 失败、0 跳过）；服务端 **3 项通过**。
- 两端 Release solution 构建均 **0 警告、0 错误**。

## 社区双目录事务性回滚与后台候选进程 GPU 指标注入 - 2026-10-08

### 新增与优化

- `CommunityCatalogSynchronizer` 新增双目录同步事务保护：在写入 Profile 目录前预取快照，当设备策略落盘发生异常或被取消时，以强制非取消令牌执行补偿回滚，确保双目录本地缓存一致性。
- `WindowsResourceArbitrationCandidateProvider` 接入 `IWindowsGpuPerformanceCounterSource`，为符合条件的后台进程采集并填充 `GpuUsagePercent`；支持优雅容错与取消传播。
- `AppRuntime` 统一组装单一共享的 `WindowsGpuPerformanceCounterSource`，为后台仲裁候选提供者与前台遥测观测服务复用同一 GPU 计数器。

### 验证

- 新增 **5 项**回归测试（客户端测试总数由 503 提升至 **508 项**，0 失败、0 跳过）；服务端 **3 项通过**。
- 两端 Release solution 构建均 **0 警告、0 错误**。

## 运行时多应用资源仲裁注入与端到端协调 - 2026-10-08

### 新增与优化

- `PriorityPreference` 新增 `UseResourceArbitration`（默认 `false`），保持严格显式 opt-in 契约。
- `SchedulerRunResult` 新增 `ResourceArbitration` 回显字段，调度决策过程全透明。
- 核心层引入 `IResourceArbitrationProvider` 与 `ResourceArbitrationProvider`，整合后台候选进程枚举、遥测历史争用证据与仲裁评估，异常或证据不足时安全降级。
- `SchedulerOrchestrator` 接入动态资源仲裁注入：仅在 Profile 开启 `UseResourceArbitration` 时注入摘要并施加动作门禁过滤；未开启时完全保留既有静态后台策略。
- `ProfileDraftViewModel` 完成 `UseResourceArbitration` 往返持久化与重置，UI 布局与菜单结构完全保持不变。
- `AppRuntime` 共享单一候选提供者实例以保持进程 CPU 两次采样基线，并将仲裁提供者注入调度编排器。
- 同步更新多应用资源仲裁边界设计文档，将调度事务注入从待实现移至已实现。

### 验证

- 新增 **12 项**回归测试（客户端测试总数由 491 提升至 **503 项**，0 失败、0 跳过）；服务端 **3 项通过**。
- 两端 Release solution 构建均 **0 警告、0 错误**。

## 供电补偿与 GPU 自检协调 - 2026-10-08

### 修复

- 供电守卫首次观察到电池即请求限频；相同供电下的失败默认间隔 1 秒重试，供电变化立即处理。读取异常或独立取消继续监测，未知供电保留现有限制并阻止新自检。
- 门控按当前状态补偿，复位失败后不再把全部 GPU 当作仍已限频；再次切电池会重新请求锁频。
- 供电动作、GPU 自检完整锁频/复位及启动/手动/退出恢复统一经过门控操作锁。自检仅在确认 AC 且没有待恢复状态时写入，期间供电变化会在检查或清理后协调处理。
- 急停模拟改用隔离模型，不调用真实 GPU Provider；GPU 跳过结果明确展示，全项报告为 Warning。取消先清理再传播，底层返回不支持/无结果也不能掩盖调用方取消。
- 供电守卫并发启动保持单循环，重复释放共用完成任务，释放后禁止重启。保持现有 UI 布局与菜单。
- 同步交接第 17 节、API、安全、NVML 专题与规划；稳定设备身份、第三方原锁频、写后对账及实机效果仍待验证。

### 验证

- 新增 **32 项**回归测试；客户端完整 Release 测试 **491 项通过**（0 失败、0 跳过），服务端 **3 项通过**。
- 两端 Release solution 构建均 **0 警告、0 错误**；本轮 11 个 C# 文件格式校验通过。
- 未执行真实 GPU 写入、拔电或 WPF 交互验收；逻辑模拟不能代替硬件效果验证。

## GPU 恢复意图与失败保护加固 - 2026-10-08

### 修复与新增

- GPU 锁频前保存独立恢复标记；失败、部分失败或取消保留记录。无本应用恢复记录时不再无条件调用 NVML 复位，全部设备复位及标记清理成功后才解除恢复责任。
- 多卡命令由“任意一张成功”改为“全部枚举设备成功”，某张失败仍尝试后续设备，避免漏报部分恢复失败。
- 启动独立检查 GPU 恢复，不依赖脏会话标记；返回 false、记录不可读或异常时暂停调度和供电轮询，取消立即传播。正常关闭标记前再次检查恢复结果。
- 手动回滚接入 GPU 重试，GPU、普通调度及状态修复保护分别解除；普通回滚成功不能绕过 GPU 恢复失败。
- 供电门控串行处理转换和释放，保留失败锁频的恢复责任；自检锁频失败也尝试清理，清理使用独立取消令牌，能力查询失败不会盲目复位。
- 更新交接、API、安全、NVML 与规划文档。恢复标记不包含第三方原锁频或完整 GPU 基线，150ms 轮询周期也不是避免断电的保证。

### 验证

- 新增 35 项回归测试，覆盖持久化顺序、坏记录、取消、部分失败、多卡结果、并发锁频/释放、恢复门禁及自检清理。
- 客户端 Release 测试 **459 项通过**（0 失败、0 跳过）；服务端 **3 项通过**。
- 两端 Release solution 构建均 **0 警告、0 错误**；未执行真实 GPU 写入、拔电或桌面交互验收。

## 目标体验温度指标统一与社区网络同步模块落地 - 2026-10-08

### 新增

- **目标体验温度评估聚合**：在 `TargetExperienceEvaluator` 中将 `NvidiaGpuTemperatureCelsius` 与 `ThermalZoneTemperatureCelsius` 统一纳入约束评估，GPU 核心高温同样触发 `NeedsCoolingOrPower` 与散热降频保护，避免仅检查主板温区漏判独立显卡过热。
- **模块化社区网络客户端与同步服务**：
  - 在 `PerformanceScheduler.Core.Abstractions` 设计 `ICommunityApiClient` 与 `ICommunityCatalogSynchronizer`。
  - 在 `PerformanceScheduler.Core.Models` 补充 `CommunityCatalogManifest`、`CommunityApiHealth`、`CommunityApiException`、`CommunitySyncResult`。
  - 在 `PerformanceScheduler.Core.Abstractions` 与 `PerformanceScheduler.Infrastructure.Persistence` 中为 `ICommunityProfileCatalog` 与 `ICommunityDeviceStrategyCatalog` 增加带模式校验的原子安全持久化能力 `SaveProfilesAsync` / `SaveStrategiesAsync`。
  - 在 `PerformanceScheduler.Infrastructure.Community` 实现 `HttpCommunityApiClient`，支持 `/api/v1/health`、`/api/v1/community/manifest`、`/api/v1/community/profiles`、`/api/v1/community/device-strategies` 的参数化检索与单个条目查询，具备超时保护、404 安全返回与友好网络异常包装。
  - 在 `PerformanceScheduler.Infrastructure.Community` 实现 `CommunityCatalogSynchronizer`，在“刷新社区”时支持从远端同步最新 Profile 和机型策略包，具备离线优雅回退（网络或服务端不可用时不损坏现有本地缓存）与严格的内容模式合法性校验。
  - 严格保持安全不变量：所有从社区网络同步并导入用户本地库的 Profile 保持默认禁用（`isEnabled: false`），需用户主动确认后方可开启。
- 在 `AppSettings` 中提供 `CommunitySyncEnabled` 与 `CommunityServerUrl` 配置项。

### 验证

- 新增 `TargetExperienceEvaluatorTests.Evaluate_WhenNvidiaGpuTemperatureExceedsLimit_PrioritizesCooling`（1 项）。
- 新增 `HttpCommunityApiClientTests`（8 项），覆盖健康检查、清单读取、带参搜索、条目查询、404 返回及 503 异常包装。
- 新增 `CommunityCatalogSynchronizerTests`（6 项），覆盖未配置跳过、禁用跳过、健康检查失败优雅降级、全量同步写入、远端异常离线容灾及损坏条目过滤。
- 新增 `JsonCommunityProfileCatalogTests.SaveProfilesAsync_PersistsValidProfiles` 与 `JsonCommunityDeviceStrategyCatalogTests.SaveStrategiesAsync_PersistsValidStrategies`（2 项）。
- 客户端测试通过数提升至 **424 项**（0 失败，0 跳过），服务端 **3 项**通过。
- 客户端与服务端 Release 构建均为 **0 警告、0 错误**。

## 硬件自检与保护功能测试套件落地 - 2026-10-08

### 新增

- 在 `PerformanceScheduler.Core` 新增 `IHardwareSelfTestService` 与 `HardwareSelfTestService`，提供非破坏性硬件自检与保护链路测试功能：
  - **GPU 锁频与复位自检**：下发 210-800MHz 锁频并立即复位验证，非 NVIDIA 或不支持锁频的显卡安全退化，异常自动兜底复位。
  - **电源急停门控模拟**：模拟离电（AC->DC）急停锁定与插电（DC->AC）恢复全链路。
  - **电源方案与 CPU 状态查询**：非破坏性验证活动电源方案及 AC/DC 处理器状态百分比。
  - **进程安全策略自检**：验证 RealTime 拦截及系统核心关键进程（`csrss`/`dwm`/`services` 等）保护规则。
  - **一键全项自检**：聚合输出毫秒级响应时延与综合状态。
- 在 `PerformanceScheduler.App` 的 `AppRuntime` 装配自检服务，并在 `MainWindowViewModel` 与 `DiagnosticsPage` 诊断中心顶部提供“硬件与安全自检测试”操作区与实时日志输出。
- 补齐多语言字典（`zh-CN.json` 与 `en-US.json`）自检相关文案。

### 验证

- 新增 `HardwareSelfTestServiceTests`（6 项单元测试），全部通过。
- 客户端测试通过数提升至 **407 项**（0 失败，0 跳过），服务端 **3 项**通过。
- 客户端与服务端 Release 构建均为 **0 警告、0 错误**。

## 电源瞬态急停门控与崩溃/退出复位安全网落地 - 2026-10-08

### 新增

- 在 `PerformanceScheduler.Core` 实现 `IPowerEmergencyGate` 与 `PowerEmergencyGate`，支持在交流电转电池（AC -> Battery）瞬间将 GPU 核心锁频在防御区间（210MHz - 800MHz），防止电池瞬态压降（Vdroop）触发关机（Kernel-Power 41），插回电源自动释放。
- 在 `PerformanceScheduler.Infrastructure` 实现 `Win32PowerChangeWatcher`，基于 150ms 高频周期性轮询捕获供电状态切换并直通急停门控。
- 在 `FileStartupRecoveryManager` 增加启动阶段未正常关闭会话（脏退出）的 GPU 恢复委托回调，开机自动清除残留的锁频。
- 在 `AppRuntime` 组装层挂载 `ProcessExit` 钩子与注销清理，实现退出与异常终止的无条件锁频复位。
- 制定 UI 瘦身与策略架构重构路线图，计划清理过度设计的边缘动效配置（动效时长、错落入场、语言翻转过渡）及 `ImageCropWindow`，重心全面回归硬件控制、电源安全与轻量化看板。

### 验证

- 新增 `PowerEmergencyGateTests`（6 项）与 `Win32PowerChangeWatcherTests`（1 项），扩展 `FileStartupRecoveryManagerTests`。
- 客户端测试通过数提升至 **401 项**（0 失败，0 跳过），服务端 **3 项**通过。
- 客户端与服务端 Release 构建均为 **0 警告、0 错误**。

## GPU 官方受控控频与能力流水线落地 - 2026-10-08

### 新增

- 在 `NvidiaNvmlSource` 中接入官方开放接口 `nvmlDeviceSetGpuLockedClocks` 与 `nvmlDeviceResetGpuLockedClocks`，动态检测导出符号，实现显卡核心频率受控锁定与重置。
- 在 `INvidiaNvmlSource` 及其实现中引入 `TrySetLockedClocksAsync` 与 `TryResetLockedClocksAsync`。
- 实现 `NvidiaGpuTuningProvider`，对接真实 NVML 状态；支持锁频时开放 `CoreClockLimit` 与 `ResetToDefault` 特性，状态置为 `Capability.GpuReversibleLimitReady`；在只读或缺失时优雅降级。
- 增加 `NvidiaGpuTuningProviderTests` 单元测试组，更新 `GpuTuningProviderRegistryTests` 与 `NvidiaNvmlTelemetryProviderTests` 测试桩。

### 验证

- 客户端测试通过数提升至 **393 项**（0 失败，0 跳过），服务端 **3 项**通过。
- 客户端与服务端 Release 构建均为 **0 警告、0 错误**。

## 首批稳定性修复与能力校正 - 2026-10-08

### 修复

- 进程身份、前台检查和后台候选改用 `QueryFullProcessImageNameW` 读取映像路径，避免启动阶段把主模块 `ntdll.dll` 误作可执行文件，导致同一进程身份校验失败。
- 优先级读取与写后确认先清除 `Process.PriorityClass` 缓存，确认实际系统状态；读取失败仍按现有失败与回滚规则处理。
- 为原偶发失败的后台策略测试增加失败消息、身份和优先级诊断；新增暂停启动进程和跨句柄缓存变化的两项回归测试，保持 PID/启动时间保护。
- 调度恢复补齐 GPU 锁频清除调用，防止异常退出后遗留硬锁。

### 验证

- 客户端测试通过数提升至 **388 项**（0 失败，0 跳过），服务端 **3 项**通过。
- 客户端与服务端 Release 构建均为 **0 警告、0 错误**。

## 开发接手核验 - 2026-10-08

- 新增 [开发接手说明](docs/development-handoff.md) 与 [领域术语](CONTEXT.md)，并更新文档索引；本次未修改生产代码。
- 核验客户端与服务端构建通过，HTTP 接口 smoke 通过，测试基线分别为 386 项与 3 项。
- 明确记录未实现的硬件能力承诺，作为后续开发的基准。
