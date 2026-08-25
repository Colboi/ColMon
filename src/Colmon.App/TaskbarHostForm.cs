using System.Text.Json;

namespace Colmon;

internal abstract class TaskbarMetricForm : Form
{
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;

    private readonly string _windowId;
    private readonly int _offsetX;
    private readonly int _offsetY;
    private readonly int _slotIndex;
    private readonly SourceCoordinator? _coordinator;
    private readonly string _statePath;
    private readonly JsonLog _log;
    private readonly WindowOptionsStore _optionsStore;
    private readonly ITaskbarMetricView _view;
    private readonly ContextMenuStrip _windowMenu = new();
    private readonly ToolStripMenuItem _optionsItem = new("窗口选项");
    private readonly ToolStripMenuItem _hideItem = new("隐藏该窗口");
    private readonly ToolStripMenuItem _closeItem = new("关闭该窗口");
    private readonly System.Windows.Forms.Timer _recoveryTimer = new() { Interval = 1000 };
    private readonly uint _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private nint _taskbar;
    private nint _notifyArea;
    private string _sourceText = string.Empty;
    private bool _attached;
    private WindowOptions _options;

    protected TaskbarMetricForm(
        string windowId,
        string defaultTitle,
        int offsetX,
        int offsetY,
        int slotIndex,
        string stateFileName,
        ITaskbarMetricView view,
        int initialRefreshSeconds,
        SourceCoordinator? coordinator,
        string artifactDirectory,
        JsonLog log)
    {
        _windowId = windowId;
        _offsetX = offsetX;
        _offsetY = offsetY;
        _slotIndex = Math.Max(0, slotIndex);
        _view = view;
        _coordinator = coordinator;
        _statePath = Path.Combine(artifactDirectory, stateFileName);
        _log = log;
        _optionsStore = new WindowOptionsStore(WindowOptionsStore.DefaultPath(windowId), log);
        _options = _optionsStore.Load(new WindowOptions(defaultTitle, initialRefreshSeconds));

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(1, 0, 0);
        TransparencyKey = BackColor;
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _view.Control.Dock = DockStyle.Fill;
        _view.Control.Font = Font;
        _view.Control.ForeColor = ForeColor;
        _view.Title = _options.Title;
        Controls.Add(_view.Control);

        _windowMenu.Items.AddRange([_optionsItem, new ToolStripSeparator(), _hideItem, _closeItem]);
        _windowMenu.Opening += (_, _) => _log.Write("window.menu.opened", new
        {
            windowId = _windowId,
            title = _view.Title,
            items = _windowMenu.Items.OfType<ToolStripItem>().Select(item => item.Text ?? "").Where(text => text.Length > 0)
        });
        _optionsItem.Click += (_, _) => ShowOptionsDialog();
        _hideItem.Click += (_, _) =>
        {
            _log.Write("window.hide.clicked", new { windowId = _windowId, title = _view.Title });
            Hide();
        };
        _closeItem.Click += (_, _) =>
        {
            _log.Write("window.close.clicked", new { windowId = _windowId, title = _view.Title });
            Close();
        };
        ContextMenuStrip = _windowMenu;
        _view.Control.ContextMenuStrip = _windowMenu;

        if (_coordinator is not null)
        {
            _coordinator.SetPollInterval(TimeSpan.FromSeconds(_options.RefreshIntervalSeconds));
            _coordinator.TextChanged += OnTextChanged;
        }
        _recoveryTimer.Tick += (_, _) => AttachAndPlace("recovery-timer");
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= (int)(NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate);
            return parameters;
        }
    }

    protected override void OnLoad(EventArgs eventArgs)
    {
        base.OnLoad(eventArgs);
        AttachAndPlace("load");
        _recoveryTimer.Start();
    }

    private void OnTextChanged(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => OnTextChanged(text));
            return;
        }

        _sourceText = text;
        _view.SetSourceText(text);
        AttachAndPlace("data-change");
    }

    protected virtual void ShowOptionsDialog()
    {
        using var dialog = new TaskbarWindowOptionsDialog(_options);
        _log.Write("window.options.opened", new { windowId = _windowId, title = _view.Title });
        if (dialog.ShowDialog() != DialogResult.OK) return;

        try
        {
            ApplyOptions(dialog.Options, persist: true);
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(exception.Message, "Colmon", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ApplyOptions(WindowOptions options, bool persist)
    {
        _options = options.Normalize(_view.Title);
        if (persist) _optionsStore.Save(_options);
        _view.Title = _options.Title;
        _coordinator?.SetPollInterval(TimeSpan.FromSeconds(_options.RefreshIntervalSeconds));
        _log.Write("window.options.applied", new
        {
            _options.Title,
            _options.RefreshIntervalSeconds,
            persisted = persist
        });
        AttachAndPlace("options-change");
        _view.Control.Invalidate();
    }

    protected ContextMenuStrip WindowMenu => _windowMenu;
    protected JsonLog Log => _log;
    protected string WindowId => _windowId;
    protected ITaskbarMetricView View => _view;
    protected void RefreshPlacement(string reason) => AttachAndPlace(reason);

    private void AttachAndPlace(string reason)
    {
        if (!IsHandleCreated) return;

        try
        {
            var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar == 0)
                throw new InvalidOperationException("Shell_TrayWnd was not found.");

            if (!_attached || taskbar != _taskbar || NativeMethods.GetAncestor(Handle, NativeMethods.GaParent) != taskbar)
            {
                _taskbar = taskbar;
                _notifyArea = NativeMethods.FindWindowEx(taskbar, 0, "TrayNotifyWnd", null);
                NativeMethods.SetParent(Handle, taskbar);
                _attached = NativeMethods.GetAncestor(Handle, NativeMethods.GaParent) == taskbar;
                _log.Write("taskbar.attach", new { reason, attached = _attached, taskbar = $"0x{taskbar:X}" });
            }

            Place(reason);
        }
        catch (Exception exception)
        {
            _attached = false;
            _log.Write("taskbar.place.error", new { reason, exception.Message });
        }
    }

    private void Place(string reason)
    {
        var taskbarRect = NativeMethods.Rect(_taskbar);
        var notifyRect = _notifyArea == 0 ? default : NativeMethods.Rect(_notifyArea);
        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(_taskbar));
        var scale = dpi / 96F;
        var width = MeasureWindowWidth(Font, dpi, _view.CharacterColumns, out var characterCellWidth);
        var height = Math.Clamp((int)(_view.LogicalHeight * scale), 24, Math.Max(24, taskbarRect.Height - (int)(4 * scale)));
        var notifyLeft = _notifyArea == 0 ? taskbarRect.Right - (int)(110 * scale) : notifyRect.Left;
        var gap = Math.Max(2, (int)(4 * scale));
        var occupiedWindows = TaskbarPlacement.ExternalWindows(_taskbar, Handle, taskbarRect);
        var anchorX = TaskbarPlacement.FindAvailableX(
            taskbarRect.Left,
            taskbarRect.Width,
            notifyLeft,
            width,
            gap,
            (int)(_offsetX * scale),
            occupiedWindows.Select(window => window.Rectangle));
        var x = anchorX - _slotIndex * (width + gap);
        var y = (taskbarRect.Height - height) / 2 + (int)(_offsetY * scale);

        x = Math.Clamp(x, 0, Math.Max(0, taskbarRect.Width - width));
        y = Math.Clamp(y, 0, Math.Max(0, taskbarRect.Height - height));

        NativeMethods.MoveWindow(Handle, x, y, width, height, true);
        var appRect = NativeMethods.Rect(Handle);
        var overlapsExternalWindow = TaskbarPlacement.Overlaps(
            taskbarRect.Left,
            x,
            width,
            occupiedWindows.Select(window => window.Rectangle));
        var state = new
        {
            timestamp = DateTimeOffset.Now,
            reason,
            attached = _attached,
            processId = Environment.ProcessId,
            windowId = _windowId,
            sourceText = _sourceText,
            display = _view.Snapshot(characterCellWidth, width, _options.RefreshIntervalSeconds),
            dpi,
            placement = new
            {
                mode = "avoid-external-taskbar-windows",
                slotIndex = _slotIndex,
                gap,
                overlapsExternalWindow,
                occupiedWindows = occupiedWindows.Select(window => new
                {
                    handle = $"0x{window.Handle:X}",
                    processId = window.ProcessId,
                    window.ProcessName,
                    rectangle = window.Rectangle
                })
            },
            handles = new { app = $"0x{Handle:X}", taskbar = $"0x{_taskbar:X}", notificationArea = $"0x{_notifyArea:X}" },
            rectangles = new { app = appRect, taskbar = taskbarRect, notificationArea = _notifyArea == 0 ? (NativeRect?)null : notifyRect },
            relative = new { x, y, width, height }
        };
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state, JsonDefaults.Indented));
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        var messageId = message.Msg;
        if ((uint)messageId == _taskbarCreatedMessage || messageId is WmDisplayChange or WmSettingChange or WmDpiChanged)
            BeginInvoke(() => AttachAndPlace($"window-message-{messageId:X}"));
    }

    private static int MeasureWindowWidth(Font font, uint dpi, int characterColumns, out int characterCellWidth)
    {
        using var bitmap = new Bitmap(8, 8);
        bitmap.SetResolution(Math.Max(96, dpi), Math.Max(96, dpi));
        using var graphics = Graphics.FromImage(bitmap);
        characterCellWidth = Math.Max(1, TextRenderer.MeasureText(graphics, "0", font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
        return characterCellWidth * characterColumns;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _recoveryTimer.Stop();
            _recoveryTimer.Dispose();
            if (_coordinator is not null) _coordinator.TextChanged -= OnTextChanged;
            _windowMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class TaskbarHostForm : TaskbarMetricForm
{
    public TaskbarHostForm(
        AppConfig config,
        SourceCoordinator coordinator,
        string artifactDirectory,
        JsonLog log)
        : base(
            config.Sources.FirstOrDefault()?.Name ?? "codex-weekly",
            config.Title,
            config.OffsetX,
            config.OffsetY,
            0,
            "state.json",
            new TaskbarProgressBar(),
            config.Sources.Count == 0 ? 60 : Math.Max(1, config.Sources.Min(source => source.PollMilliseconds) / 1000),
            coordinator,
            artifactDirectory,
            log)
    {
    }
}

internal sealed class TaskbarCountHostForm : TaskbarMetricForm
{
    public TaskbarCountHostForm(
        string title,
        int offsetX,
        int offsetY,
        SourceConfig source,
        SourceCoordinator coordinator,
        string artifactDirectory,
        JsonLog log)
        : base(
            source.Name,
            title,
            offsetX,
            offsetY,
            1,
            "tokens-today.state.json",
            new TaskbarCountDisplay(),
            Math.Max(1, source.PollMilliseconds / 1000),
            coordinator,
            artifactDirectory,
            log)
    {
    }
}
