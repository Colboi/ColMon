namespace Colmon;

internal sealed class TaskbarWindowManager : IDisposable
{
    private readonly List<Form> _windows = [];
    private readonly JsonLog _log;
    private bool _disposed;

    public TaskbarWindowManager(JsonLog log) => _log = log;

    public event EventHandler? VisibilityChanged;

    public int WindowCount => _windows.Count(window => !window.IsDisposed);
    public int VisibleCount => _windows.Count(window => !window.IsDisposed && window.Visible);
    public bool AllVisible => WindowCount > 0 && VisibleCount == WindowCount;
    public IReadOnlyList<Form> RegisteredWindows =>
        _windows.Where(window => !window.IsDisposed).ToArray();

    public void Register(Form window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);
        if (_windows.Contains(window)) return;

        _windows.Add(window);
        window.VisibleChanged += OnWindowVisibilityChanged;
        window.FormClosed += OnWindowClosed;
        _log.Write("taskbar.window.registered", new { window.GetType().Name, windowCount = WindowCount });
        RaiseVisibilityChanged();
    }

    public void ShowAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var window in _windows.Where(window => !window.IsDisposed))
            window.Show();
        _log.Write("taskbar.windows.show-all", Snapshot());
        RaiseVisibilityChanged();
    }

    public void HideAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var window in _windows.Where(window => !window.IsDisposed))
            window.Hide();
        _log.Write("taskbar.windows.hide-all", Snapshot());
        RaiseVisibilityChanged();
    }

    public void ToggleAll() => SetAllVisible(!AllVisible);

    public void SetVisible(Form window, bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);
        if (!_windows.Contains(window) || window.IsDisposed) return;
        if (window.Visible == visible)
        {
            RaiseVisibilityChanged();
            return;
        }

        if (visible) window.Show();
        else window.Hide();
    }

    public void SetAllVisible(bool visible)
    {
        if (visible) ShowAll();
        else HideAll();
    }

    public object Snapshot() => new { windowCount = WindowCount, visibleCount = VisibleCount, allVisible = AllVisible };

    private void OnWindowVisibilityChanged(object? sender, EventArgs eventArgs) => RaiseVisibilityChanged();

    private void OnWindowClosed(object? sender, FormClosedEventArgs eventArgs)
    {
        if (sender is not Form window) return;
        window.VisibleChanged -= OnWindowVisibilityChanged;
        window.FormClosed -= OnWindowClosed;
        _windows.Remove(window);
        _log.Write("taskbar.window.closed", new { window.GetType().Name, windowCount = WindowCount });
        RaiseVisibilityChanged();
    }

    private void RaiseVisibilityChanged() => VisibilityChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var window in _windows.ToArray())
        {
            window.VisibleChanged -= OnWindowVisibilityChanged;
            window.FormClosed -= OnWindowClosed;
            if (!window.IsDisposed) window.Dispose();
        }
        _windows.Clear();
    }
}
