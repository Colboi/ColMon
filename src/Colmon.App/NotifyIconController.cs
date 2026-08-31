namespace Colmon;

internal sealed class NotifyIconController : IDisposable
{
    private readonly TaskbarWindowManager _windows;
    private readonly JsonLog _log;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _visibilityItem = new();
    private readonly ToolStripSeparator _windowSeparator = new();
    private readonly ToolStripMenuItem _exitItem = new("退出");
    private readonly Dictionary<Form, ToolStripMenuItem> _windowItems = [];
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _appIcon;
    private bool _disposed;

    public NotifyIconController(TaskbarWindowManager windows, JsonLog log, Action exitRequested)
    {
        _windows = windows;
        _log = log;

        _visibilityItem.Click += (_, _) =>
        {
            _log.Write("tray.visibility.clicked", _windows.Snapshot());
            _windows.ToggleAll();
        };
        _exitItem.Click += (_, _) =>
        {
            _log.Write("tray.exit.clicked", _windows.Snapshot());
            exitRequested();
        };
        _menu.Items.AddRange([_visibilityItem, _windowSeparator, _exitItem]);
        _menu.Opening += (_, _) => UpdateMenu();

        _appIcon = LoadApplicationIcon(log);
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _appIcon,
            Text = "Colmon",
            Visible = true
        };
        _windows.VisibilityChanged += OnVisibilityChanged;
        UpdateMenu();
        _log.Write("tray.created", _windows.Snapshot());
    }

    internal void PerformVisibilityCommandForDiagnostics() => _visibilityItem.PerformClick();
    internal void PerformExitCommandForDiagnostics() => _exitItem.PerformClick();
    internal string VisibilityCommandTextForDiagnostics => _visibilityItem.Text ?? string.Empty;
    internal string VisibilityCommandForDiagnostics => _windows.AllVisible ? "hide" : "show";
    internal int WindowToggleItemCountForDiagnostics => _windowItems.Count;
    internal IReadOnlyList<string> WindowToggleWindowIdsForDiagnostics =>
        _windows.RegisteredWindows.Select(GetWindowId).ToArray();
    internal IReadOnlyList<string> WindowToggleTextsForDiagnostics =>
        _windows.RegisteredWindows.Select(window => _windowItems.TryGetValue(window, out var item)
            ? item.Text ?? string.Empty
            : string.Empty).ToArray();

    internal void PerformWindowToggleForDiagnostics(string windowId)
    {
        var window = _windows.RegisteredWindows.Single(window => GetWindowId(window) == windowId);
        if (!_windowItems.TryGetValue(window, out var item))
            throw new InvalidOperationException($"Tray toggle item was not created for {windowId}.");
        item.PerformClick();
    }

    private void OnVisibilityChanged(object? sender, EventArgs eventArgs) => UpdateMenu();

    private void UpdateMenu()
    {
        _visibilityItem.Text = _windows.AllVisible ? "隐藏所有任务栏窗口" : "显示所有任务栏窗口";
        _visibilityItem.Enabled = _windows.WindowCount > 0;
        var registeredWindows = _windows.RegisteredWindows;
        foreach (var staleWindow in _windowItems.Keys.Where(window => !registeredWindows.Contains(window)).ToArray())
        {
            var staleItem = _windowItems[staleWindow];
            _menu.Items.Remove(staleItem);
            staleItem.Dispose();
            _windowItems.Remove(staleWindow);
        }

        foreach (var window in registeredWindows)
        {
            if (!_windowItems.TryGetValue(window, out var item))
            {
                var targetWindow = window;
                item = new ToolStripMenuItem
                {
                    CheckOnClick = false
                };
                item.Click += (_, _) => ToggleWindow(targetWindow);
                _windowItems.Add(targetWindow, item);
                _menu.Items.Insert(_menu.Items.IndexOf(_exitItem), item);
            }

            item.Text = GetWindowTitle(window);
            item.Checked = window.Visible;
            item.Enabled = !window.IsDisposed;
        }

        _windowSeparator.Visible = registeredWindows.Count > 0;
        _notifyIcon.Text = $"Colmon · {_windows.VisibleCount}/{_windows.WindowCount} 个窗口可见";
    }

    private void ToggleWindow(Form window)
    {
        if (window.IsDisposed) return;
        var visible = !window.Visible;
        _log.Write("tray.window.visibility.clicked", new
        {
            windowId = GetWindowId(window),
            title = GetWindowTitle(window),
            visible
        });
        _windows.SetVisible(window, visible);
    }

    private static string GetWindowId(Form window) => window is TaskbarMetricForm taskbar
        ? taskbar.WindowIdForTray
        : string.IsNullOrWhiteSpace(window.Name) ? window.GetType().Name : window.Name;

    private static string GetWindowTitle(Form window) => window is TaskbarMetricForm taskbar
        ? taskbar.WindowTitleForTray
        : string.IsNullOrWhiteSpace(window.Text) ? GetWindowId(window) : window.Text;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _windows.VisibilityChanged -= OnVisibilityChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        foreach (var item in _windowItems.Values) item.Dispose();
        _windowItems.Clear();
        _menu.Dispose();
        _log.Write("tray.disposed", new { });
    }

    private static Icon LoadApplicationIcon(JsonLog log)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "colmon.ico");
        try
        {
            var icon = new Icon(path);
            log.Write("tray.icon.loaded", new { path, icon.Width, icon.Height });
            return icon;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.Write("tray.icon.load.error", new { path, exception.Message });
            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
