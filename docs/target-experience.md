# 目标体验控制

目标体验不是直接填写“给 CPU 分配多少 MHz”，而是把用户真正关心的体验约束交给调度系统评估：稳定帧率、帧时间、温度、功耗和噪音偏好。硬件和 Windows 接口不支持某个约束时，系统必须明确降级。

## 数据入口

目标体验配置位于 `PerformanceProfile.TargetExperience`，模型定义在 [`TargetExperiencePreference`](../客户端/src/PerformanceScheduler.Core/Models/TargetExperiencePreference.cs)。

| 字段 | 含义 | 默认值 |
| --- | --- | --- |
| `Enabled` | 是否参与目标体验分析 | `false` |
| `TargetFramesPerSecond` | 目标 FPS，可选 | 空 |
| `MaximumFrameTimeP95Milliseconds` | 95 分位帧时间上限，可选 | 空 |
| `MaximumTemperatureCelsius` | 温度上限，可选 | 空 |
| `MaximumPowerWatts` | 功耗上限，可选 | 空 |
| `Priority` | 平衡、稳帧、安静、省电或性能优先 | `Balanced` |
| `ControlMode` | 仅观察、仅建议或自动模式 | `ObservationOnly` |
| `NoisePreference` | 忽略、安静、平衡或性能 | `Ignore` |
| `ObservationWindowSeconds` | 观察窗口 | `10` 秒 |
| `MinimumStableSamples` | 生成稳定建议前所需样本数 | `3` |
| `HysteresisPercent` | 进入/退出约束的滞回比例 | `5%` |

## 处理链路

```text
Profile.TargetExperience
    -> TelemetryAnalysisEvaluator
        -> TargetExperienceEvaluator      当前样本的约束判断
        -> TargetExperienceController      多次样本的稳定性判断
    -> TelemetryAnalysisSummary
        -> 诊断中心显示状态、原因和建议
```

- `TargetExperienceEvaluator` 只读取有效遥测，判断当前是达标、需要更多性能、需要降温/降功耗或证据不足。
- `TargetExperienceController` 保存观察窗口内的样本状态，使用最小稳定样本数和滞回避免单次尖峰触发建议。
- 不同前台进程、不同 Profile 或进程重启后的旧证据不能混用；同名进程还必须检查 PID。

## 当前模式语义

### 仅观察

只显示约束状态和证据数量，不生成调节建议。这是默认模式，适合首次使用和硬件验证。

### 仅建议

在稳定证据满足后生成“提高性能”或“降低温度/功耗”的建议，但不会调用电源、进程或 GPU 写入接口。

### 自动模式

在满足安全包络与冷却限制的前提下，自动模式现已实现轻量闭环自适应微调：

- 仅在 Profile 显式配置 `ControlMode = TargetExperienceControlMode.Automatic` 时生效；
- 控制器识别持续稳定性能缺口或降温诉求后，生成 `CanApplyAutomatically = true` 的建议；
- 经过 `TargetExperienceAutoTuningEnvelope` 安全包络约束（处理器最大频率百分比安全下限 60%、上限 100%、单次步进 5%、冷却时间 10 秒防振荡）；
- 调度层自动建立写前基线快照，通过 `IPowerPlanManager.ApplyAdvancedSettingsAsync` 递进施加电源计划高级状态微调；
- 发生前台窗口失焦、目标进程退出或调度停用时，无条件执行逆序回滚并恢复原始电源基线。

## 证据规则

- Provider 返回失败、超时、预热或无权限时，不把缺失值当成达标或超标。
- 只有有效的帧时间、温度、功耗等指标才参与对应约束。
- 没有足够样本时状态为证据不足或等待稳定，不触发动作。
- 目标体验是约束系统，不是物理资源精确分配器；Windows 用户态接口不能保证把某个核心固定分配到某个 MHz。

## 当前实现边界

已完成：

- 目标帧率、帧时间、温度、功耗和噪音偏好的 Profile 字段。
- 约束评估、观察窗口、最小稳定样本数和滞回。
- 可解释的状态、原因键和建议模型。
- 默认只读，以及目标体验与能力检测的安全校验。
- 自动模式下的轻量闭环自适应微调（基于电源高级状态处理器百分比、防振荡冷却与写前基线恢复）。

尚未开放：

- GPU 电压曲线、降压超频或厂商私有写入。
- 未完成真实设备矩阵前的跨 CPU、GPU、驱动组合支持承诺。

## 验收重点

1. 同一应用在低负载、稳定负载、突发尖峰和持续过热四种场景下观察状态是否符合预期。
2. 切换前台窗口或重启同名进程后，旧目标体验证据是否被清除。
3. 缺少温度/功耗/FPS Provider 时，界面是否明确显示证据不足而不是显示 0。
4. 自动模式即使被配置，也不能绕过能力检测、回滚和异常保护。
