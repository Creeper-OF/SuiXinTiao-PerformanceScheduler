# PresentMon 可选集成

随芯调通过可选的 PresentMon Console Provider 观察前台图形应用的帧时间。该功能只在用户手动点击“观察当前前台”时运行，不参与自动调度写入，也不会修改 Profile、电源方案、进程优先级或 GPU 设置。

## 安装

随芯调不在源码仓库中分发 PresentMon 二进制。请从 [GameTechDev/PresentMon 官方发布页](https://github.com/GameTechDev/PresentMon/releases) 获取控制台版本，并使用以下任一方式配置：

1. 推荐：打开“设置 -> PresentMon 帧采集”，点击“选择控制台 EXE”。选择后会立即刷新能力缓存，不需要重启随芯调。
2. 将控制台 EXE 放入程序目录下的 `tools\PresentMon`。支持 `PresentMon.exe`、`PresentMonConsole.exe` 和 `PresentMon-*-x64.exe`。
3. 开发或自动部署环境可将环境变量 `SUIXINTIAO_PRESENTMON_PATH` 设置为控制台 EXE 的绝对路径，然后重启随芯调。

图形化选择只接受受支持的控制台文件名，不接受 `PresentMonService.exe`、`PresentMonCli.exe` 或任意其他 EXE。自定义路径属于本机设置，不会随迁移包复制到另一台电脑。

第三方 EXE 和 DLL 已由 `.gitignore` 排除，不应提交到随芯调仓库。分发打包前还需要单独复核 PresentMon 的许可证、版本和完整性。

## 安全边界

- 按目标 PID 采集，当前短时观察窗口为 1 秒。
- 每次使用唯一 ETW 会话名，不停止或接管其他 PresentMon 会话。
- 不使用 `--restart_as_admin`，不会为了采集自动请求管理员权限。
- 发现 PresentMon、CapFrameX、OCAT 或 FrameView 等潜在采集器时，本次观察降级并显示原因。
- 取消或超时只终止由随芯调启动的子进程树。
- 临时 CSV 位于运行数据目录，解析完成后立即尽力删除；持久化历史只保存汇总指标。

## 权限与准确性

PresentMon 的官方说明指出，ETW 采集可能需要账户具备 Performance Log Users 权限；权限不足时随芯调只显示“无法采集”，不会自动提权。官方也说明 HAGS、图形 API 和驱动路径可能影响部分 GPU/延迟指标，因此当前只输出能够从有效帧间隔计算的汇总值，缺失数据不会伪装成 0。

## 当前指标

- 平均帧率
- 平均帧时间
- 95% 帧时间
- 99% 帧时间
- 有效帧样本数

当前实现是手动短时观察基座，不等同于完整性能评测。后续持续采样和调度前后对比必须继续遵守采集预算、冲突检测、可停止性和隐私边界。
