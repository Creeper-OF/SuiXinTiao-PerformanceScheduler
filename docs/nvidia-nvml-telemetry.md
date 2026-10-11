# NVIDIA NVML 遥测与受控限制边界

核验日期：2026-10-08。`nvidia.nvml` 遥测 Provider 只读；同一 Source 已封装官方 GPU 锁频和复位函数，供电门控及硬件自检可以调用。GPU 限制尚未进入普通 Profile 调度事务，也未完成实机验收。

## 当前实现

- [NvidiaNvmlSource](../客户端/src/PerformanceScheduler.Infrastructure/Hardware/Gpu/NvidiaNvml/NvidiaNvmlSource.cs) 从 Windows 系统目录或 `Program Files\NVIDIA Corporation\NVSMI` 的绝对路径加载驱动提供的 `nvml.dll`；项目不携带该二进制，也不从任意工作目录加载同名库。
- Source 动态解析遥测、`nvmlDeviceSetGpuLockedClocks` 和 `nvmlDeviceResetGpuLockedClocks`。每次操作初始化 NVML，结束时关闭和释放库；导出符号存在不能证明每张卡支持写入或当前用户有权限。
- [NvidiaNvmlTelemetryProvider](../客户端/src/PerformanceScheduler.Infrastructure/Telemetry/NvidiaNvml/NvidiaNvmlTelemetryProvider.cs) 声明最小采样间隔为 2 秒，受遥测协调器预算控制；没有 100ms 急停采样模式。
- 缺失库、初始化失败或无设备时明确降级。某项设备读取失败时该项保持缺失；所有指标均缺失时返回无可用样本，不伪造零值。
- [NvidiaGpuTuningProvider](../客户端/src/PerformanceScheduler.Infrastructure/Hardware/Gpu/Providers/NvidiaGpuTuningProvider.cs) 拒绝零频率和倒置区间，检查 NVML 候选能力，串行执行锁频/复位。写入前先保存恢复标记；失败、部分失败或取消都保留恢复意图。
- 多卡命令逐个尝试，任一设备取句柄或调用失败都使批次失败，不把一张卡成功当作整批成功。
- 未实现功耗墙写入、nvidia-smi 回退、原 GPU 锁频区间读取或写后区间确认；`SchedulerState` 仍没有 GPU 字段。

## 指标及多 GPU 语义

| 当前指标 | 读取函数 | 多 GPU 聚合 |
| --- | --- | --- |
| GPU 使用率（%） | `nvmlDeviceGetUtilizationRates` | 有效设备值取最大 |
| GPU 温度（°C） | `nvmlDeviceGetTemperature`，GPU 温度传感器 | 有效设备值取最大 |
| GPU 功耗（W） | `nvmlDeviceGetPowerUsage`，毫瓦转换为瓦 | 有效设备值求和 |
| 图形频率（MHz） | `nvmlDeviceGetClockInfo`，Graphics 类型 | 有效设备值取最大 |

这些结果没有绑定到前台应用使用的某一张显卡；某些设备读取失败时，聚合也只包含读到的设备。GPU 温度不能标作热点温度，GPU 功耗不能标作整机功耗。当前未采集显存频率和限频原因掩码。

目标体验温度评估已取系统热区与 NVML GPU 温度的有效最大值，功耗仍为 NVML GPU 功耗；结果只产生建议。进入控制链路前仍须明确传感器对象、设备身份、聚合和缺失语义，见 [目标体验控制](target-experience.md)。

## GPU 恢复意图与失败保护

AppRuntime 为 NVIDIA Provider 注入 [FileGpuClockRecoveryStore](../客户端/src/PerformanceScheduler.Infrastructure/Persistence/FileGpuClockRecoveryStore.cs)，路径为 `%LOCALAPPDATA%\PerformanceScheduler\runtime\gpu-clock-recovery.json`。未注入存储时仅有进程内标记，不具备跨进程恢复能力。

记录保存 `schemaVersion: 1` 和 `resetRequired: true`，表示本应用尝试过锁频，需要复位到驱动默认值。它**不包含原锁频区间、设备 UUID 或 original/applied/current 对账**，不能称为完整 GPU 回滚基线，也不能还原其他工具此前的设置。

没有记录和进程内待恢复状态时，复位为无操作成功，不调用 NVML reset。存在标记时，全部枚举设备复位及文件清理成功后才解除；失败或取消保留记录。未知版本、损坏内容、路径冲突或重解析路径阻止写入与盲目恢复，不覆盖原始证据。

启动始终独立检查 GPU 恢复，即使会话文件缺失或记录正常退出。失败时返回 `GpuRecoveryFailed`，暂停自动调度和供电轮询。手动回滚同时尝试普通调度与 GPU 恢复，两类保护分别解除；正常关闭标记前再次检查 GPU 恢复。

`ProcessExit` 只做尽力清理；强制终止、崩溃和断电可能不执行回调。旧版本没有恢复标记的锁频也不会被新版本自动认领或无条件清除。

GPU 自检会调用真实锁频/复位，但完整操作对与供电补偿、启动/手动/退出恢复由同一门控协调；只有已确认 AC 且没有待恢复状态时才写入，其他情况报告 `Skipped`。锁频失败或取消也使用独立令牌清理，再传播调用方取消。急停模拟使用隔离模型，不调用真实 GPU，结果只证明逻辑模拟；稳定设备身份、原锁频、写后对账及第三方冲突仍待补齐。

## 官方接口及支持条件

接口实现状态如下；封装完成不代表已通过设备与权限实测：

| 能力 | NVML API | 项目状态 |
| --- | --- | --- |
| GPU 频率范围锁定 | `nvmlDeviceSetGpuLockedClocks` | 已封装，供电门控/自检可调用 |
| GPU 频率复位 | `nvmlDeviceResetGpuLockedClocks` | 已封装，失败保留恢复标记 |
| 功耗限制 | `nvmlDeviceSetPowerManagementLimit` | 未实现 |

NVML 文档将 GPU 锁频适用范围描述为充分支持的 Volta 及更新设备，并要求管理员权限；调用仍可能返回不支持、无权限、参数错误或设备丢失。功耗限制必须位于设备报告的允许范围内，API 单位为毫瓦。详见 [NVML 设备命令](https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceCommands.html) 和 [nvidia-smi](https://docs.nvidia.com/deploy/nvidia-smi/index.html)。

锁频接受最小和最大频率，设置不当可能提高频率下限；不能默认把任意锁频参数称为防御性上限约束。固定频率也不能保证固定电压或特定功耗，更不能据此承诺解决硬件断电。系统重启或驱动重载后的复位行为不等于应用崩溃时立即恢复。[NVML 设备命令](https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceCommands.html)

## 后续开放的验收条件

受控限制以减少功耗或温度压力为目的，不加入加压超频或绕过驱动限制。开放前必须逐项完成：

1. 按稳定设备身份检测读取、限制、权限和恢复能力，明确设备与驱动范围。
2. 确定基线、预期应用状态、写后确认和恢复判定；无法读取或验证所需状态时拒绝写入。
3. 把动作、结果与回滚字段接入现有调度事务，写入前持久化恢复依据。
4. 验证正常退出、手动恢复、取消、部分失败和重启后遗留恢复；崩溃或设备丢失时保留未解决状态，不承诺无条件即时复位。
5. 尊重其他软件已有的 GPU 设置；复位到驱动默认值不等于恢复用户原来设置，无法判定所有权时不得盲目复位。
6. 在 [硬件验证矩阵](hardware-validation-matrix.md) 记录实机支持、效果和恢复证据，再更新能力契约及 UI。

当前供电门控使用默认 150ms 的轮询周期，覆盖初始电池；相同状态下的失败默认间隔 1 秒重试，变化立即处理，读取异常继续监测。未知供电保留限制。未实现电源事件订阅，也不承诺端到端实时响应。频率与实际供电效果仍须实测，见 [安全基座](safety-baseline.md)。
