using System.Globalization;
using System.Text.RegularExpressions;

namespace Colmon;

internal static partial class CodexWeeklyLayout
{
    public const int CharacterColumns = 15;
    public const decimal LowRemainingThreshold = 10M;
    public const string Title = "Codex weekly";

    public static readonly Color NormalColor = Color.FromArgb(0, 120, 215);
    public static readonly Color LowColor = Color.FromArgb(255, 76, 76);

    public static decimal? ParseRemainingPercent(string text)
    {
        var match = PercentagePattern().Match(text ?? string.Empty);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
            return null;

        return Math.Clamp(value, 0M, 100M);
    }

    public static string FormatRemainingPercent(decimal? value) => TaskbarProgressBar.FormatPercentage(value);

    public static bool IsLow(decimal? value) => value is >= 0M and < LowRemainingThreshold;

    public static Color ValueColor(decimal? value) => IsLow(value) ? LowColor : NormalColor;

    public static int MeasureWindowWidth(Font font, uint dpi, out int characterCellWidth)
    {
        using var bitmap = new Bitmap(8, 8);
        bitmap.SetResolution(Math.Max(96, dpi), Math.Max(96, dpi));
        using var graphics = Graphics.FromImage(bitmap);
        characterCellWidth = Math.Max(1, TextRenderer.MeasureText(graphics, "0", font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
        return characterCellWidth * CharacterColumns;
    }

    [GeneratedRegex(@"(?<!\d)(100(?:\.0+)?|\d{1,2}(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentagePattern();
}
