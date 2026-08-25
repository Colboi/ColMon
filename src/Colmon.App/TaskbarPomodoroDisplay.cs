using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Colmon;

internal sealed class TaskbarPomodoroDisplay : Control, ITaskbarMetricView
{
    public const int DefaultCharacterColumns = 15;
    public const int DotDiameterLogical = 6;
    public const int DotGapLogical = 10;
    private PomodoroSnapshot _snapshot = new(
        PomodoroPhase.Work,
        TimeSpan.FromMinutes(25),
        TimeSpan.FromMinutes(25),
        0,
        true,
        new PomodoroOptions());

    public TaskbarPomodoroDisplay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.UserPaint, true);
        ForeColor = Color.White;
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PomodoroSnapshot TimerSnapshot
    {
        get => _snapshot;
        set
        {
            _snapshot = value;
            Invalidate();
        }
    }

    public string TimeText => FormatTime(_snapshot.Remaining);
    public string DotsText => FormatDots(_snapshot.CompletedWorkPeriods);
    public Color BarColor => _snapshot.Phase == PomodoroPhase.Work
        ? Color.FromArgb(0, 120, 215)
        : Color.FromArgb(55, 185, 115);

    public static string FormatTime(TimeSpan remaining)
    {
        var seconds = Math.Max(0L, (long)Math.Ceiling(remaining.TotalSeconds));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    public static string FormatDots(int completed) => string.Concat(
        Enumerable.Range(0, 4).Select(index => index < Math.Clamp(completed, 0, 4) ? "●" : "○"));

    string ITaskbarMetricView.Title { get => "Pomodoro"; set { } }
    Control ITaskbarMetricView.Control => this;
    int ITaskbarMetricView.CharacterColumns => DefaultCharacterColumns;
    int ITaskbarMetricView.LogicalHeight => 42;
    void ITaskbarMetricView.SetSourceText(string text) { }

    public object Snapshot(int characterCellWidth, int pixelWidth, int refreshIntervalSeconds) => new
    {
        phase = _snapshot.Phase.ToString().ToLowerInvariant(),
        timeText = TimeText,
        remainingSeconds = Math.Max(0, (int)Math.Ceiling(_snapshot.Remaining.TotalSeconds)),
        durationSeconds = (int)_snapshot.Duration.TotalSeconds,
        remainingRatio = _snapshot.RemainingRatio,
        completedWorkPeriods = _snapshot.CompletedWorkPeriods,
        dotsText = DotsText,
        running = _snapshot.IsRunning,
        options = _snapshot.Options,
        refreshIntervalSeconds = 1,
        characterColumns = DefaultCharacterColumns,
        characterCellWidth,
        pixelWidth,
        logicalHeight = 42,
        dotDiameterLogical = DotDiameterLogical,
        dotGapLogical = DotGapLogical
    };

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var firstHeight = Height * 15 / 42;
        var secondHeight = Math.Max(5, Height * 10 / 42);
        var thirdTop = firstHeight + secondHeight;

        TextRenderer.DrawText(eventArgs.Graphics, TimeText, Font,
            new Rectangle(0, 0, Width, firstHeight), ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        var scale = Math.Max(1F, DeviceDpi / 96F);
        var barHeight = Math.Max(4, (int)Math.Round(5 * scale));
        var barRectangle = new Rectangle(
            0,
            firstHeight + (secondHeight - barHeight) / 2,
            Width,
            barHeight);
        using var trackBrush = new SolidBrush(Color.FromArgb(82, 82, 82));
        eventArgs.Graphics.FillRectangle(trackBrush, barRectangle);
        var fillWidth = Math.Clamp(
            (int)Math.Round(barRectangle.Width * (double)_snapshot.RemainingRatio),
            0,
            barRectangle.Width);
        if (fillWidth > 0)
        {
            using var fillBrush = new SolidBrush(BarColor);
            eventArgs.Graphics.FillRectangle(fillBrush,
                new Rectangle(barRectangle.Left, barRectangle.Top, fillWidth, barRectangle.Height));
        }

        DrawCompletionDots(eventArgs.Graphics, new Rectangle(0, thirdTop, Width, Height - thirdTop), scale);
    }

    private void DrawCompletionDots(Graphics graphics, Rectangle bounds, float scale)
    {
        var diameter = Math.Max(5F, DotDiameterLogical * scale);
        var gap = Math.Max(6F, DotGapLogical * scale);
        var totalWidth = diameter * 4 + gap * 3;
        var left = bounds.Left + (bounds.Width - totalWidth) / 2F;
        var top = bounds.Top + (bounds.Height - diameter) / 2F;
        var previousSmoothingMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(ForeColor);
        using var pen = new Pen(ForeColor, Math.Max(1.2F, 1.2F * scale));
        for (var index = 0; index < 4; index++)
        {
            var circle = new RectangleF(left + index * (diameter + gap), top, diameter, diameter);
            if (index < _snapshot.CompletedWorkPeriods) graphics.FillEllipse(brush, circle);
            else graphics.DrawEllipse(pen, circle);
        }
        graphics.SmoothingMode = previousSmoothingMode;
    }
}
