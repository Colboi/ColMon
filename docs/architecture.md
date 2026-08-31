# Colmon 技术选型与任务栏架构

## 结论

首选技术栈为 **.NET 10 LTS + C# + WinForms 自绘（GDI/TextRenderer）+ 少量 Win32 P/Invoke**。任务栏宿主兼容 TrafficMonitor 的 Win11 实现：发现 `Shell_TrayWnd`，通过 `SetParent` 建立有效祖先关系，依据 `TrayNotifyWnd` 计算位置，并监听 Explorer 重建、DPI、显示器和系统设置变化。

.NET 10 是当前 LTS，支持到 2028-11；Windows 11 x64/x86/Arm64 均在支持矩阵中。WinForms 提供直接 HWND、成熟 GDI 文本、高 DPI 与低依赖发布能力，适合任务栏内几十像素高的轻量界面。

## 为什么选择 WinForms/GDI

| 方案 | HWND 控制 | 小尺寸文字 | 开发效率 | 运行负担 | 结论 |
|---|---:|---:|---:|---:|---|
| C# WinForms + GDI | 强 | 强 | 高 | 低 | 首选 |
| C++ Win32/Direct2D | 最强 | 强 | 中低 | 最低 | 性能或兼容性遇到硬瓶颈时再迁移 |
| WPF | 中 | 强 | 高 | 中 | 顶层/子 HWND 与透明合成复杂度偏高 |
| WinUI 3 | 中 | 强 | 中 | 中高 | 任务栏嵌入收益有限，部署和合成链更复杂 |
| WebView/Electron | 弱 | 中 | 高 | 高 | 不适合常驻任务栏小部件 |

用户当前运行的 TrafficMonitor 1.86 配置提供了直接证据：`disable_d2d = true`、微软雅黑 9pt、透明色开启，实际窗口约 176×32 px，仍能获得清晰美观的效果。Colmon 因此先使用 GDI 路径，把 Direct2D 保留为大量图形、动画或特殊字体需求出现后的可选渲染后端。

## 参考实现中可复用的逻辑

- 主任务栏：`Shell_TrayWnd`；副屏任务栏：`Shell_SecondaryTrayWnd`。
- Win11 主屏通知区域：`TrayNotifyWnd`；左侧锚点可参考 `Start`。
- `SetParent` 是兼容性实现，Explorer 窗口类属于未承诺稳定的内部结构。
- 数据采集放在后台线程，UI 定时触发重绘。
- 位置需持续自愈，并处理 Explorer 重启、多屏数量、DPI 与通知区域宽度变化。

Colmon 的调整：

- 数据源各自独立运行，带超时、取消、指数退避和错误快照。
- 收到数据时主动重绘；1 秒定时器只负责宿主关系/几何自愈。
- 接收 `TaskbarCreated`、`WM_DISPLAYCHANGE`、`WM_DPICHANGED`、`WM_SETTINGCHANGE` 后立即重新附着。
- 每次定位写出 `state.json`，记录 HWND、DPI、屏幕矩形和附着结果。

## 数据源边界

当前内置四种适配器：

- `codex` / `codex-weekly` / `codex-five-hour`：启动短生命周期、只读且 approval policy 优先为 `never` 的 Codex App Server，通过逐行 JSON-RPC 读取 `account/rateLimits/read`；weekly 选择 10080 分钟窗口，5h 选择 300 分钟窗口；旧版 CLI 在明确拒绝 `never` 时兼容回退 `untrusted`，默认每 60 秒刷新。
- `clock`：本地可重复视觉验证。
- `http` / `http-json`：互联网 REST/文本接口，可用点分路径读取 JSON 属性。
- `tcp` / `tcp-line`：连接本地或远程端口并读取一行文本。

Codex 适配器不读取 `auth.json`，也不记录完整 RPC 响应；认证由 Codex App Server 自身处理。协议字段同时兼容 camelCase、snake_case 和无歧义的备用额度桶，并在同一 RPC 会话中处理账户读取与空额度重试。读取失败时，最近一次有效值最多保留 10 分钟并加 `~` 标记。后续建议增加命名管道和 WebSocket。数据源输出统一为不可变文本快照；凭据应从 Windows Credential Manager 或环境注入，配置文件只保存引用。

三行配额视图由可复用的 `TaskbarProgressBar` 控件绘制，第一行显示标题，第二行使用完整窗口宽度绘制比例条，第三行绘制一条“百分比 + 4 个空格 + 紧凑重置时间”的文本。百分比使用和比例条相同的蓝色或低额度红色；重置时间固定使用 `d h`、`h m` 或不足一小时的分钟格式，不使用 `d h m`。任务栏宿主只处理附着、定位和单窗口菜单。`InfoSample` 以不可变字段携带可选的 `ResetAt`，控件在 UI 线程上根据本地时间计算倒计时，数据源仍按原有频率读取。每个窗口可以独立修改标题与刷新频率；非敏感设置按数据源名称保存在 `%LocalAppData%\Colmon\`，刷新频率变更会唤醒后台协调器并立即开始下一次读取。

通用任务栏外壳为 `TaskbarMetricForm`，配额、数量和番茄钟控件都实现 `ITaskbarMetricView`，并由视图声明 32 或 42 逻辑像素高度。`TaskbarProgressBar` 使用 42 逻辑像素绘制标题、全宽比例条、单行配额文本和重置倒计时；重置时间缺失或数据 stale 时显示 `--`。`TaskbarCountDisplay` 负责两行数量绘制及不变文化的三位逗号分组。`CodexTokenTodaySource` 对齐 Token Monitor 的 `tokscale --client codex --today` 口径，在后台只读扫描 Codex 会话 JSONL 中当天的 `token_count`，累加 `last_token_usage.total_tokens`，按文件保存字节偏移、部分尾行和文件签名，并将索引持久化到 `%LocalAppData%\Colmon\cache`。文件时间戳只用于读取优化，事件时间负责日期判断。三个 15 字符宽窗口使用稳定的逻辑槽位并排放置，仍共同避让 TrafficMonitor 等外部任务栏窗口。

番茄钟由独立的 `PomodoroTimer` 状态机负责，UI 的一秒定时器只以当前时间生成不可变快照并触发重绘。正在运行的阶段使用绝对结束时间，因此隐藏窗口或短暂 UI 延迟不会累计漂移；暂停状态保存明确的剩余时长。`TaskbarPomodoroDisplay` 绘制倒计时、剩余时间条和四个完成点。每次进程启动都回到完整工作时长、四空心点和暂停状态，只从 `%LocalAppData%\Colmon\pomodoro.json` 恢复自动选项与阶段分钟数。

## 应用总控与窗口生命周期

进程由 `ColmonApplicationContext` 驱动，不依赖任意一个任务栏窗体维持消息循环。`TaskbarWindowManager` 可以注册多个窗体，并提供统一显示、隐藏、计数和释放操作。`NotifyIconController` 在通知区域提供动态右键菜单：

- 全部可见时显示“隐藏所有任务栏窗口”。
- 存在隐藏窗口时显示“显示所有任务栏窗口”。
- 菜单中为每个已注册任务栏窗口提供带勾选状态的独立开关；默认四个窗口分别对应 weekly、5h、Tokens today 和番茄钟。
- “退出”会先移除通知图标并释放全部任务栏窗体，随后结束消息循环。

Windows 可能按用户的“其他系统托盘图标”设置把新通知图标放入折叠区；应用不应绕过该系统级选择。

## 兼容性风险与回退

微软文档明确说明跨进程 `SetParent` 可能重置子进程 DPI 感知状态，且 `SetParent` 不自动修改 `WS_CHILD`/`WS_POPUP`。当前尖峰刻意保留 TrafficMonitor 已验证的 popup 样式，以实际任务栏表现为准，并通过 `GetAncestor(GA_PARENT)` 验证附着。

发布前至少覆盖：Win11 23H2/24H2/25H2/26H1、100/125/150/200% DPI、主副屏、自动隐藏、左右对齐、浅色/深色、Explorer 重启、RDP 重连。若嵌入失败，回退为无激活、置顶、贴靠任务栏的独立窗口，并在日志/设置页明确显示降级状态。

## 资料

- [Microsoft: .NET releases and support](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)
- [Microsoft: Install .NET on Windows / supported Windows versions](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [Microsoft: SetParent](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent)
- [Microsoft: Windows taskbar and TaskbarCreated](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar)
- 本地参考：`ref/TrafficMonitor/TrafficMonitor/Win11TaskbarDlg.cpp`、`TaskBarDlg.cpp`、`TrafficMonitorDlg.cpp`
