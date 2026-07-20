namespace Colmon;

internal sealed class NotifyIconController : IDisposable
{
    private readonly TaskbarWindowManager _windows;
    private readonly JsonLog _log;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _visibilityItem = new();
    private readonly ToolStripMenuItem _exitItem = new("退出");
    private readonly NotifyIcon _notifyIcon;
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
        _menu.Items.AddRange([_visibilityItem, new ToolStripSeparator(), _exitItem]);
        _menu.Opening += (_, _) => UpdateMenu();

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = SystemIcons.Application,
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

    private void OnVisibilityChanged(object? sender, EventArgs eventArgs) => UpdateMenu();

    private void UpdateMenu()
    {
        _visibilityItem.Text = _windows.AllVisible ? "隐藏所有任务栏窗口" : "显示所有任务栏窗口";
        _visibilityItem.Enabled = _windows.WindowCount > 0;
        _notifyIcon.Text = $"Colmon · {_windows.VisibleCount}/{_windows.WindowCount} 个窗口可见";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _windows.VisibilityChanged -= OnVisibilityChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _log.Write("tray.disposed", new { });
    }
}
