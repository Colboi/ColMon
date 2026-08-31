using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Colmon;

internal sealed partial class TaskbarCountDisplay : Control, ITaskbarMetricView
{
    public const int DefaultCharacterColumns = 15;
    private string _title = "Count";
    private long? _value;

    public TaskbarCountDisplay()
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
            var normalized = string.IsNullOrWhiteSpace(value) ? "Count" : value.Trim();
            if (_title == normalized) return;
            _title = normalized;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public long? Value
    {
        get => _value;
        set
        {
            long? normalized = value is null ? null : Math.Max(0, value.Value);
            if (_value == normalized) return;
            _value = normalized;
            Invalidate();
        }
    }

    public string NumberText => FormatNumber(Value);
    Control ITaskbarMetricView.Control => this;
    int ITaskbarMetricView.CharacterColumns => DefaultCharacterColumns;
    int ITaskbarMetricView.LogicalHeight => 32;

    public static string FormatNumber(long? value) =>
        value is null ? "--" : Math.Max(0, value.Value).ToString("N0", CultureInfo.InvariantCulture);

    public void SetSourceText(string text)
    {
        var match = IntegerPattern().Match(text ?? string.Empty);
        Value = match.Success && long.TryParse(match.Value.Replace(",", ""), NumberStyles.None,
            CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    public void SetSourceSample(InfoSample sample) => SetSourceText(sample.Text);

    public object Snapshot(int characterCellWidth, int pixelWidth, int refreshIntervalSeconds) => new
    {
        title = Title,
        count = Value,
        numberText = NumberText,
        refreshIntervalSeconds,
        characterColumns = DefaultCharacterColumns,
        characterCellWidth,
        pixelWidth
    };

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var titleHeight = Height / 2;
        TextRenderer.DrawText(eventArgs.Graphics, Title, Font, new Rectangle(0, 0, Width, titleHeight), ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(eventArgs.Graphics, NumberText, Font,
            new Rectangle(0, titleHeight, Width, Height - titleHeight), ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    [GeneratedRegex(@"(?<!\d)(?:\d{1,3}(?:,\d{3})+|\d+)")]
    private static partial Regex IntegerPattern();
}
