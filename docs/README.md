# 文档索引

这个目录用于沉淀项目方向、架构说明、安全规则和功能结构。随芯调是国内优先项目，中文文档是主线；英文 README 只作为开源传播和外部协作的辅助入口。

## 核心文档

- [开发接手说明](development-handoff.md)：源码链路、实际能力、文档差异、构建测试基线和后续改动入口。
- [领域术语](../CONTEXT.md)：统一配置、调度、恢复、遥测和控制的用语。
- [项目总规划](project-plan.md)：产品目标、架构原则、风险清单和阶段路线图。
- [API 与模块契约](api-contracts.md)：接口边界、关键数据模型、默认行为和审查速读路径。
- [目标体验控制](target-experience.md)：目标帧率、温度、功耗、噪音偏好和只读控制边界。
- [更新日志](../CHANGELOG.md)：每次更新的功能、安全影响、验证结果和已知风险。
- [安全基座](safety-baseline.md)：调度事务顺序、回滚状态、进程优先级保护和扩展规则。
- [公开仓库维护清单](open-source-checklist.md)：公开提交前的隐私、构建产物和文档同步检查。
- [功能结构](功能结构.md)：当前桌面端的用户任务结构。
- [PresentMon 可选集成](presentmon-integration.md)：外部帧采集器的安装、权限、会话隔离和降级规则。
- [Windows GPU 进程遥测](windows-gpu-telemetry.md)：系统性能计数器的数据语义、采样预算和已知限制。
- [Windows 系统硬件遥测](windows-system-telemetry.md)：CPU 频率、系统热区温度和标准计数器的降级边界。
- [NVIDIA NVML 遥测与受控限制边界](nvidia-nvml-telemetry.md)：当前只读指标、公开 API 支持条件及后续写入验收要求。
- [资源压力摘要](resource-pressure-diagnostics.md)：整机 CPU/GPU 压力分级的阈值和只读边界。
- [多应用资源仲裁边界](resource-arbitration.md)：后台候选、稳定采样、保护分类和动作门禁。
- [硬件兼容性验证矩阵](hardware-validation-matrix.md)：真实设备覆盖范围、证据记录格式和安全验收标准。
- [社区服务端](community-server.md)：社区 API 的职责边界、只读目录接口和后续审核演进。

## 维护规则

- 修改调度行为、回滚行为、Profile 语义或用户可见安全承诺时，必须同步更新相关文档。
- 修改接口签名、DTO 字段、默认值或动作门禁时，必须同步更新 `api-contracts.md`、对应测试和根目录 `CHANGELOG.md`。
- 产品方向、阶段计划和风险清单写入 `project-plan.md`。
- 底层安全不变量写入 `safety-baseline.md`。
- UI 结构和用户任务结构写入 `功能结构.md`。
- 公开仓库提交边界和脱敏检查写入 `open-source-checklist.md`。
- 优先写清楚、可验证的约束，不写泛泛的“优化”“增强”“更智能”。
- 文档应优先记录稳定契约和验证结果，避免把临时实现细节扩散到多个文件。
