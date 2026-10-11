# 多应用资源仲裁边界

随芯调的前台优先不是“前台永远加速、后台永远降速”。多应用仲裁必须先确认资源争用，再决定是否有合适的后台候选可以释放资源。

## 当前已实现

- `WindowsResourceArbitrationCandidateProvider` 只读枚举后台进程，记录进程分类、受保护状态、当前 Profile 是否有适用策略。
- CPU 使用率使用 PID、进程启动时间和两次 `TotalProcessorTime` 差值计算，PID 被复用时会丢弃旧基线。
- 动作门禁不仅匹配 PID，还必须匹配候选进程的启动时间；缺少启动时间证据时，即使有负载建议也不会执行后台写入。
- 首次观察没有历史基线，候选 CPU 使用率保持未知；未知值不会被当成高负载。
- `ResourceArbitrationEvaluator` 排除前台进程、受保护进程和系统分类，只对有有效 CPU/GPU 指标且有策略的候选生成建议。
- `ResourceArbitrationActionGate` 是显式 opt-in 的动作门禁。没有仲裁摘要、摘要证据不足或没有推荐候选时，后台写入全部阻止。
- `IResourceArbitrationProvider` 与 `ResourceArbitrationProvider` 统一协调候选进程收集与争用证据解析（结合遥测历史 `ITelemetryHistoryStore` 与动态争用判定）。
- `SchedulerOrchestrator` 正式接入仲裁注入流程：仅在 Profile 的 `Priority.UseResourceArbitration` 显式开启时注入 `ResourceArbitrationSummary`；未开启时完全保留既有静态后台策略。未提供提供者或计算异常时自动回退为缺失证据门禁（阻止所有后台动作）。
- `SchedulerProcessActionHandler` 严格依据 PID + 启动时间白名单应用后台动作，写前必须记录完整回滚基线，执行失败自动回滚；`SchedulerRunResult` 回显仲裁摘要。
- `ProfileDraftViewModel` 完成 `UseResourceArbitration` 的载入、重置与往返持久化。

- 候选进程通过 `IWindowsGpuPerformanceCounterSource` 接入只读 GPU 计数器，为非保护且具备有效策略的进程采集并填充 `GpuUsagePercent`。
- `ResourceArbitrationEvaluator` 依据 CPU 与 GPU 占用最大值计算综合负载分值 `LoadScore`。

## 当前没有实现

- CPU Sets、亲和性、I/O 优先级、Job Object 配额等更强限制。
- 多个受保护应用之间的最低资源预算和冲突合并。

这些能力必须建立在真实硬件采样、可回滚动作计划和长时间稳定性验证之上，不能仅凭一次高负载观察直接修改进程状态。

## 验收顺序

1. 在同一应用、同一 PID 生命周期中完成至少两次采样，确认 CPU 差值和 PID 复用保护正确。
2. 验证录制、语音、下载、编译和系统服务的保护分类，不允许受保护任务被无条件限制。
3. 先以诊断建议模式运行，记录误判率和对前台帧时间的影响。
4. 只有在动作级 baseline、写后复核和回滚均通过实机矩阵后，才考虑开放单项后台策略的自动应用。
