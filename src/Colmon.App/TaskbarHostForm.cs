using System.Text.Json;

namespace Colmon;

internal sealed class TaskbarHostForm : Form
{
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;

    private readonly AppConfig _config;
    private readonly SourceCoordinator _coordinator;
    private readonly string _statePath;
    private readonly JsonLog _log;
    private readonly System.Windows.Forms.Timer _recoveryTimer = new() { Interval = 1000 };
    private readonly uint _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private nint _taskbar;
    private nint _notifyArea;
    private string _sourceText = string.Empty;
    private decimal? _remainingPercent;
    private bool _attached;

    public TaskbarHostForm(AppConfig config, SourceCoordinator coordinator, string artifactDirectory, JsonLog log)
    {
        _config = config;
        _coordinator = coordinator;
        _statePath = Path.Combine(artifactDirectory, "state.json");
        _log = log;

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

        _coordinator.TextChanged += OnTextChanged;
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
        _remainingPercent = CodexWeeklyLayout.ParseRemainingPercent(text);
        AttachAndPlace("data-change");
        Invalidate();
    }

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
        var width = CodexWeeklyLayout.MeasureWindowWidth(Font, dpi, out var characterCellWidth);
        var height = Math.Clamp((int)(32 * scale), 24, Math.Max(24, taskbarRect.Height - (int)(4 * scale)));
        var notifyLeft = _notifyArea == 0 ? taskbarRect.Right - (int)(110 * scale) : notifyRect.Left;
        var gap = Math.Max(2, (int)(4 * scale));
        var occupiedWindows = TaskbarPlacement.ExternalWindows(_taskbar, Handle, taskbarRect);
        var x = TaskbarPlacement.FindAvailableX(
            taskbarRect.Left,
            taskbarRect.Width,
            notifyLeft,
            width,
            gap,
            (int)(_config.OffsetX * scale),
            occupiedWindows.Select(window => window.Rectangle));
        var y = (taskbarRect.Height - height) / 2 + (int)(_config.OffsetY * scale);

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
            sourceText = _sourceText,
            display = new
            {
                title = CodexWeeklyLayout.Title,
                remainingPercent = _remainingPercent,
                percentageText = CodexWeeklyLayout.FormatRemainingPercent(_remainingPercent),
                isLow = CodexWeeklyLayout.IsLow(_remainingPercent),
                progressColor = ColorTranslator.ToHtml(CodexWeeklyLayout.ValueColor(_remainingPercent)),
                characterColumns = CodexWeeklyLayout.CharacterColumns,
                characterCellWidth,
                pixelWidth = width
            },
            dpi,
            placement = new
            {
                mode = "avoid-external-taskbar-windows",
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

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(BackColor);
        var scale = Math.Max(1F, DeviceDpi / 96F);
        var titleHeight = Height / 2;
        var titleRectangle = new Rectangle(0, 0, Width, titleHeight);
        var secondLine = new Rectangle(0, titleHeight, Width, Height - titleHeight);

        TextRenderer.DrawText(eventArgs.Graphics, CodexWeeklyLayout.Title, Font, titleRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        var percentageText = CodexWeeklyLayout.FormatRemainingPercent(_remainingPercent);
        var percentageWidth = TextRenderer.MeasureText(eventArgs.Graphics, "100%", Font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        var valueRectangle = new Rectangle(0, secondLine.Top, percentageWidth, secondLine.Height);
        var valueColor = CodexWeeklyLayout.ValueColor(_remainingPercent);
        TextRenderer.DrawText(eventArgs.Graphics, percentageText, Font, valueRectangle,
            _remainingPercent is null ? ForeColor : valueColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        var gap = Math.Max(3, (int)Math.Round(4 * scale));
        var barHeight = Math.Max(4, (int)Math.Round(6 * scale));
        var barRectangle = new Rectangle(
            percentageWidth + gap,
            secondLine.Top + (secondLine.Height - barHeight) / 2,
            Math.Max(0, Width - percentageWidth - gap),
            barHeight);
        if (barRectangle.Width <= 0) return;

        using var trackBrush = new SolidBrush(CodexWeeklyLayout.TrackColor);
        eventArgs.Graphics.FillRectangle(trackBrush, barRectangle);
        if (_remainingPercent is not > 0M) return;

        var fillWidth = Math.Clamp(
            (int)Math.Round(barRectangle.Width * (double)(_remainingPercent.Value / 100M)),
            1,
            barRectangle.Width);
        using var fillBrush = new SolidBrush(valueColor);
        eventArgs.Graphics.FillRectangle(fillBrush,
            new Rectangle(barRectangle.Left, barRectangle.Top, fillWidth, barRectangle.Height));
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        var messageId = message.Msg;
        if ((uint)messageId == _taskbarCreatedMessage || messageId is WmDisplayChange or WmSettingChange or WmDpiChanged)
            BeginInvoke(() => AttachAndPlace($"window-message-{messageId:X}"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _recoveryTimer.Stop();
            _recoveryTimer.Dispose();
            _coordinator.TextChanged -= OnTextChanged;
        }
        base.Dispose(disposing);
    }
}
