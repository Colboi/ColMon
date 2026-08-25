using System.ComponentModel;

namespace Colmon;

internal sealed class TaskbarProgressBar : Control, ITaskbarMetricView
{
    private string _title = "Progress";
    private decimal? _value;

    public TaskbarProgressBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.UserPaint, true);
        ForeColor = Color.White;
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title
    {
        get => _title;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Progress" : value.Trim();
            if (_title == normalized) return;
            _title = normalized;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal? Value
    {
        get => _value;
        set
        {
            decimal? normalized = value is null ? null : Math.Clamp(value.Value, 0M, 100M);
            if (_value == normalized) return;
            _value = normalized;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal LowThreshold { get; set; } = 10M;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color NormalColor { get; set; } = Color.FromArgb(0, 120, 215);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color LowColor { get; set; } = Color.FromArgb(255, 76, 76);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TrackColor { get; set; } = Color.FromArgb(82, 82, 82);

    public bool IsLow => Value is >= 0M && Value < LowThreshold;
    public Color ValueColor => IsLow ? LowColor : NormalColor;
    public string PercentageText => FormatPercentage(Value);

    internal static string FormatPercentage(decimal? value) =>
        value is null ? "--%" : $"{decimal.Floor(Math.Clamp(value.Value, 0M, 100M)):0}%";

    Control ITaskbarMetricView.Control => this;
    int ITaskbarMetricView.CharacterColumns => CodexWeeklyLayout.CharacterColumns;
    int ITaskbarMetricView.LogicalHeight => 32;

    public void SetSourceText(string text) => Value = CodexWeeklyLayout.ParseRemainingPercent(text);

    public object Snapshot(int characterCellWidth, int pixelWidth, int refreshIntervalSeconds) => new
    {
        title = Title,
        remainingPercent = Value,
        percentageText = PercentageText,
        isLow = IsLow,
        progressColor = ColorTranslator.ToHtml(ValueColor),
        refreshIntervalSeconds,
        characterColumns = CodexWeeklyLayout.CharacterColumns,
        characterCellWidth,
        pixelWidth
    };

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var scale = Math.Max(1F, DeviceDpi / 96F);
        var titleHeight = Height / 2;
        var titleRectangle = new Rectangle(0, 0, Width, titleHeight);
        var secondLine = new Rectangle(0, titleHeight, Width, Height - titleHeight);

        TextRenderer.DrawText(eventArgs.Graphics, Title, Font, titleRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        var percentageWidth = TextRenderer.MeasureText(eventArgs.Graphics, "100%", Font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        var valueRectangle = new Rectangle(0, secondLine.Top, percentageWidth, secondLine.Height);
        TextRenderer.DrawText(eventArgs.Graphics, PercentageText, Font, valueRectangle,
            Value is null ? ForeColor : ValueColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding);

        var gap = Math.Max(3, (int)Math.Round(4 * scale));
        var barHeight = Math.Max(4, (int)Math.Round(6 * scale));
        var barRectangle = new Rectangle(
            percentageWidth + gap,
            secondLine.Top + (secondLine.Height - barHeight) / 2,
            Math.Max(0, Width - percentageWidth - gap),
            barHeight);
        if (barRectangle.Width <= 0) return;

        using var trackBrush = new SolidBrush(TrackColor);
        eventArgs.Graphics.FillRectangle(trackBrush, barRectangle);
        if (Value is not > 0M) return;

        var fillWidth = Math.Clamp(
            (int)Math.Round(barRectangle.Width * (double)(Value.Value / 100M)),
            1,
            barRectangle.Width);
        using var fillBrush = new SolidBrush(ValueColor);
        eventArgs.Graphics.FillRectangle(fillBrush,
            new Rectangle(barRectangle.Left, barRectangle.Top, fillWidth, barRectangle.Height));
    }
}
