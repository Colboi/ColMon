# Colmon

Windows 11 任务栏信息宿主。当前版本使用 .NET 10 WinForms/GDI，默认通过本机 Codex App Server 显示 weekly 与 5h limit 剩余额度，并支持时钟、HTTP/JSON 和 TCP 行协议数据源。

应用使用紫罗兰色 C/M 组合图标。Windows 多尺寸资源位于 `src/Colmon.App/Assets/colmon.ico`；构建时 ICO 会嵌入 EXE，并由通知栏图标显式加载。

## 构建与探针

```powershell
dotnet build .\Colmon.slnx -c Debug
dotnet .\src\Colmon.App\bin\Debug\net10.0-windows\Colmon.dll --probe
```

## 运行

构建后可直接双击：

```text
src\Colmon.App\bin\Debug\net10.0-windows\Colmon.exe
```

也可以从 PowerShell 启动；默认配置会立即读取真实 Codex weekly 数据，此后每 60 秒刷新：

```powershell
dotnet .\src\Colmon.App\bin\Debug\net10.0-windows\Colmon.dll
```

只验证额度协议且不启动窗口：

```powershell
dotnet .\src\Colmon.App\bin\Debug\net10.0-windows\Colmon.dll --codex-probe
```

发布包使用自包含 Windows x64 portable ZIP，可通过 `scripts/package-portable.ps1` 生成；发布资产不包含用户设置、日志、测试结果或构建机隐私数据。

详细选型、兼容性边界和架构说明见 [docs/architecture.md](docs/architecture.md)。

## Codex weekly 窗口

任务栏窗口使用 15 字符网格。在 96 DPI、Microsoft YaHei 9pt 下，单个数字字符宽 7 px，窗口为 105x42 px。三行依次显示标题、占满窗口宽度的比例条、单行的“剩余百分比 + 4 个空格 + 紧凑重置时间”。百分比使用和进度条相同的颜色：正常额度为蓝色，低于 10% 时为红色；没有重置时间或数据已 stale 时显示 `--`。重置时间来自 Codex App Server 的 `resetsAt`，界面在两次 API 轮询之间本地更新倒计时；第三行不使用 `d h m`，按窗口类型使用 `d h` 或 `h m`（不足一小时则显示分钟）。

## Codex 5h limit 窗口

默认同时显示 `Codex 5hlimit`。它复用 weekly 进度条窗口的三行渲染和任务栏定位逻辑，RPC 解析会精确选择 `windowDurationMins: 300` 的 5 小时窗口，并显示该窗口的重置倒计时。第三行格式为百分比、四个空格和 `h m` 时间；百分比与进度条保持相同颜色。可在配置中使用 `showCodexFiveHourLimit: false` 关闭，标题和刷新频率也可通过 `codexFiveHourTitle`、`codexFiveHourPollMilliseconds` 调整。

在任务栏窗口上单击右键，可以打开“窗口选项”、隐藏或关闭当前窗口。“窗口选项”可修改第一行文字和 10–3600 秒的刷新频率；设置按数据源名称保存在 `%LocalAppData%\Colmon\<source-name>.json`，不会写入仓库配置。

右键单击通知栏图标，可以使用“隐藏所有任务栏窗口/显示所有任务栏窗口”，也可以分别勾选或取消勾选 weekly、5h、Tokens today 和番茄钟窗口。

## Tokens today 窗口

默认同时显示 `Tokens today` 数量窗口。第二行是本地当天的 Codex token 总用量，并按 `1,234,567` 的形式分组。数据源沿用 Token Monitor/tokscale 的 Codex 统计口径：只读扫描 `CODEX_HOME/sessions`（默认 `%USERPROFILE%\.codex\sessions`）中的 `token_count` 事件，按本地日期累加每轮 `last_token_usage.total_tokens`；扫描在后台执行，只读取会话文件新增字节，并将读取偏移保存到 `%LocalAppData%\Colmon\cache`。文件正在写入、出现部分尾行或单个文件暂时不可读时，日志会记录结构化诊断并保留 stale 标记。

数量窗口拥有独立的“窗口选项”“隐藏该窗口”“关闭该窗口”菜单，标题和刷新频率保存在 `%LocalAppData%\Colmon\codex-tokens-today.json`。可在配置中使用 `showTokensToday: false` 关闭该窗口。

## 番茄钟窗口

番茄钟默认以第四个任务栏窗口显示，并每秒更新。三行依次为 `mm:ss` 倒计时、无百分比的剩余时间条、四个工作阶段完成点；每完成一个工作阶段，完成点从左到右由 `○` 变为 `●`。第四个工作阶段对应的休息结束后开始新循环，完成点清空。

右键菜单会根据状态显示“启动”或“暂停”，并提供“复原至初始”“复原至该阶段起始”“跳过该阶段”“窗口选项”“隐藏该窗口”“关闭该窗口”。窗口选项可调整自动休息、自动进入下一循环、工作分钟数和休息分钟数。应用每次启动时回到完整工作时长、四个空心点并保持暂停；设置保存在 `%LocalAppData%\Colmon\pomodoro.json`。可用 `showPomodoro: false` 关闭该窗口。

工作阶段完成后点亮一个圆点并进入休息；自动休息控制休息是否立即启动。休息结束时仍有空心点，自动进入下一循环会启动下一个工作阶段；四个点全部完成后，休息结束会清空圆点并回到暂停的初始状态。
