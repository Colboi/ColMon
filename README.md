# Colmon

Windows 11 任务栏信息宿主。当前版本使用 .NET 10 WinForms/GDI，默认通过本机 Codex App Server 显示 weekly 剩余额度，并支持时钟、HTTP/JSON 和 TCP 行协议数据源。

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

视觉冒烟测试会构建、启动、等待任务栏附着、抓取真实任务栏截图、执行几何断言并退出：

```powershell
.\scripts\visual-smoke.ps1
.\scripts\visual-smoke.ps1 -LiveCodex
```

`-LiveCodex` 使用默认双击启动路径，验证真实额度、TrafficMonitor 检测和无重叠定位。

通知栏总控冒烟测试通过真实菜单命令处理路径执行“隐藏全部 → 显示全部 → 退出”，并验证窗口计数、图标释放和进程退出：

```powershell
.\scripts\control-smoke.ps1
```

证据输出到 `artifacts/visual-smoke/<timestamp>/`：

- `taskbar.png`：真实任务栏截图。
- `taskbar-detail.png`：Colmon 与通知区附近的局部对比图。
- `state.json`：应用、任务栏、通知区 HWND/DPI/矩形。
- `result.json`：附着与包含关系断言。
- `colmon.jsonl`：结构化生命周期和数据源错误日志。

详细选型、兼容性边界和测试矩阵见 [docs/architecture.md](docs/architecture.md)。
本次实机环境与验收结果见 [docs/validation.md](docs/validation.md)。

## Codex weekly 窗口

任务栏窗口使用 15 字符网格。在 96 DPI、Microsoft YaHei 9pt 下，单个数字字符宽 7 px，窗口为 105x32 px。第一行居中显示 `Codex weekly`；第二行左侧显示剩余整数百分比，右侧显示比例条。剩余量低于 10% 时，数字和填充条变为红色。

运行非视觉边界检查：

```powershell
.\scripts\layout-smoke.ps1
```
"# ColMon" 
