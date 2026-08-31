using System.ComponentModel;

namespace Colmon;

internal sealed class TaskbarProgressBar : Control, ITaskbarMetricView
{
    private const int LogicalHeight = CodexWeeklyLayout.ThreeLineHeight;
    private const int QuotaLineSeparatorSpaces = 4;
    private string _title = "Progress";
    private decimal? _value;
    private DateTimeOffset? _resetAt;
    private bool _sampleIsStale;
    private readonly System.Windows.Forms.Timer _countdownTimer = new() { Interval = 30_000 };

    public TaskbarProgressBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.UserPaint, true);
        ForeColor = Color.White;
        BackColor = Color.Transparent;
        _countdownTimer.Tick += (_, _) => Invalidate();
        _countdownTimer.Start();
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
    public Color PercentageTextColor => ValueColor;
    public string PercentageText => FormatPercentage(Value);
    public DateTimeOffset? ResetAt => _resetAt;
    public bool SampleIsStale => _sampleIsStale;
    public string ResetRemainingText => GetResetRemainingText(DateTimeOffset.UtcNow);

    internal static string FormatPercentage(decimal? value) =>
        value is null ? "--%" : $"{decimal.Floor(Math.Clamp(value.Value, 0M, 100M)):0}%";

    Control ITaskbarMetricView.Control => this;
    int ITaskbarMetricView.CharacterColumns => CodexWeeklyLayout.CharacterColumns;
    int ITaskbarMetricView.LogicalHeight => LogicalHeight;

    public void SetSourceText(string text) => SetSourceSample(new InfoSample(text, DateTimeOffset.Now));

    public void SetSourceSample(InfoSample sample)
    {
        Value = CodexWeeklyLayout.ParseRemainingPercent(sample.Text);
        var resetAt = sample.ResetAt?.ToUniversalTime();
        if (_resetAt == resetAt && _sampleIsStale == sample.IsStale) return;
        _resetAt = resetAt;
        _sampleIsStale = sample.IsStale;
        Invalidate();
    }

    public object Snapshot(int characterCellWidth, int pixelWidth, int refreshIntervalSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var resetRemainingText = GetResetRemainingText(now);
        return new
        {
            title = Title,
            remainingPercent = Value,
            percentageText = PercentageText,
            isLow = IsLow,
            resetAt = _resetAt,
            resetAtUnixSeconds = _resetAt?.ToUnixTimeSeconds(),
            resetRemainingSeconds = CodexWeeklyLayout.ResetRemainingSeconds(_resetAt, now, _sampleIsStale),
            resetRemainingText,
            resetTextFormat = "compact",
            quotaLineText = FormatQuotaLine(resetRemainingText),
            quotaLineSeparatorSpaces = QuotaLineSeparatorSpaces,
            quotaLineHasBackground = false,
            resetDataIsStale = _sampleIsStale,
            progressColor = ColorTranslator.ToHtml(ValueColor),
            refreshIntervalSeconds,
            characterColumns = CodexWeeklyLayout.CharacterColumns,
            characterCellWidth,
            pixelWidth,
            percentageTextColor = ColorTranslator.ToHtml(PercentageTextColor),
            progressBarUsesFullWidth = true,
            logicalHeight = LogicalHeight
        };
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var scale = Math.Max(1F, DeviceDpi / 96F);
        var titleHeight = Height * 15 / LogicalHeight;
        var quotaHeight = Height * 10 / LogicalHeight;
        var titleRectangle = new Rectangle(0, 0, Width, titleHeight);
        var secondLine = new Rectangle(0, titleHeight, Width, quotaHeight);
        var resetLine = new Rectangle(0, titleHeight + quotaHeight, Width,
            Math.Max(0, Height - titleHeight - quotaHeight));

        TextRenderer.DrawText(eventArgs.Graphics, Title, Font, titleRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        var barHeight = Math.Max(4, (int)Math.Round(6 * scale));
        var barRectangle = new Rectangle(
            0,
            secondLine.Top + (secondLine.Height - barHeight) / 2,
            Math.Max(0, Width),
            barHeight);
        if (barRectangle.Width > 0)
        {
            using var trackBrush = new SolidBrush(TrackColor);
            eventArgs.Graphics.FillRectangle(trackBrush, barRectangle);
            if (Value is > 0M)
            {
                var fillWidth = Math.Clamp(
                    (int)Math.Round(barRectangle.Width * (double)(Value.Value / 100M)),
                    1,
                    barRectangle.Width);
                using var fillBrush = new SolidBrush(ValueColor);
                eventArgs.Graphics.FillRectangle(fillBrush,
                    new Rectangle(barRectangle.Left, barRectangle.Top, fillWidth, barRectangle.Height));
            }
        }

        if (resetLine.Height <= 0) return;

        var resetText = GetResetRemainingText(DateTimeOffset.UtcNow);
        var quotaLineText = FormatQuotaLine(resetText);
        var textFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var percentageTextSize = TextRenderer.MeasureText(eventArgs.Graphics, PercentageText, Font, Size.Empty,
            textFlags);
        var separatorSize = TextRenderer.MeasureText(eventArgs.Graphics,
            new string(' ', QuotaLineSeparatorSpaces), Font, Size.Empty, textFlags);
        var resetTextSize = TextRenderer.MeasureText(eventArgs.Graphics, resetText, Font, Size.Empty, textFlags);
        var lineWidth = TextRenderer.MeasureText(eventArgs.Graphics, quotaLineText, Font, Size.Empty, textFlags).Width;
        var lineLeft = Math.Max(0, (Width - lineWidth) / 2);
        var percentageRectangle = new Rectangle(
            lineLeft,
            resetLine.Top,
            percentageTextSize.Width,
            resetLine.Height);
        if (percentageRectangle.Width > 0)
        {
            TextRenderer.DrawText(eventArgs.Graphics, PercentageText, Font, percentageRectangle,
                PercentageTextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | textFlags);
        }

        var resetTextLeft = lineLeft + percentageTextSize.Width + separatorSize.Width;
        var resetTextRectangle = new Rectangle(
            resetTextLeft,
            resetLine.Top,
            Math.Max(0, Math.Min(resetTextSize.Width, Width - resetTextLeft)),
            resetLine.Height);
        if (resetTextRectangle.Width > 0)
        {
            TextRenderer.DrawText(eventArgs.Graphics, resetText, Font, resetTextRectangle,
                _sampleIsStale ? Color.FromArgb(160, 160, 160) : ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | textFlags);
        }
    }

    private string GetResetRemainingText(DateTimeOffset now) =>
        CodexWeeklyLayout.FormatResetRemaining(_resetAt, now, _sampleIsStale, compact: true);

    private string FormatQuotaLine(string resetText) =>
        $"{PercentageText}{new string(' ', QuotaLineSeparatorSpaces)}{resetText}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _countdownTimer.Stop();
            _countdownTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
