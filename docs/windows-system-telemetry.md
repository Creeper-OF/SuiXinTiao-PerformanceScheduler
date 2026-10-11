# Windows 系统硬件遥测

随芯调的 `windows.system-hardware` Provider 使用 Windows 标准性能计数器做只读观察，当前不修改电源、频率、电压、风扇或驱动设置。

## 当前指标

- 系统 CPU 频率：读取 `Win32_PerfFormattedData_Counters_ProcessorInformation` 的 `ActualFrequency`，优先使用 `_Total` 实例；如果系统没有总实例，则对可用处理器实例取平均值。单位为 MHz。
- 整机 CPU 使用率：读取 `Win32_PerfFormattedData_PerfOS_Processor` 的 `_Total` 实例 `PercentProcessorTime`；没有总实例时对可用实例取平均值。
- 系统热区温度：读取 `Win32_PerfFormattedData_Counters_ThermalZoneInformation`，使用高精度热力学温度换算为摄氏度，并取可用热区的最高值。它是系统热区参考值，不等同于 CPU Package、GPU Junction 或单个核心温度。
- 整机 GPU 使用率：读取 `Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine` 的所有引擎实例，取最忙引擎利用率并限制在 `0%` 到 `100%`。它用于判断整机是否存在 GPU 瓶颈，不等同于所有引擎利用率相加。
- 整机独占/共享显存：读取 `Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory`，汇总当前可见适配器的 `DedicatedUsage` 和 `SharedUsage`，转换为 MiB；多显卡设备的值是适配器汇总，不代表单张显卡。

## 能力与降级

- Provider 最短采样间隔为 2 秒，和其他 Windows 性能计数器遥测保持一致。
- CPU 频率、整机 CPU 使用率、热区温度、整机 GPU 利用率和整机显存分别检测；只提供其中一项时，其他项单独显示为不可用。
- 缺少对应 WMI 类、系统未提供样本或查询失败时，Provider 返回稳定原因码，不生成伪造的零值。
- 功耗计、风扇转速、CPU/GPU 芯片级温度目前没有被这个通用 Provider 假定为可用。它们后续需要厂商 API、驱动接口或硬件监控服务的独立 Provider。

## 使用边界

这些指标用于诊断、趋势记录和后续调度效果对比，不直接作为自动降压、超频、温控或功耗闭环的唯一依据。硬件 Provider 必须先完成设备能力检测、数据有效性检查和失败降级，再考虑可恢复的调节能力。

## 诊断中心展示

诊断中心会单独列出每个遥测 Provider 的能力状态，包括：

- 是否支持当前设备或系统环境。
- 可以返回的指标类型。
- Provider 声明的最短采样间隔。
- 预计采集开销等级。
- 不支持、超时或能力校验失败时的原因。

能力结果会在协调器内缓存，语言切换只重新渲染本地化文本，不会重复访问硬件接口。手动使 Provider 能力失效后，下一次诊断或观察会重新检测；能力探测超时不会阻塞其他 Provider，也不会改变调度配置。
