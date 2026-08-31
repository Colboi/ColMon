using System.Globalization;
using System.Text.RegularExpressions;

namespace Colmon;

internal static partial class CodexWeeklyLayout
{
    public const int CharacterColumns = 15;
    public const int ThreeLineHeight = 42;
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

    public static long? ResetRemainingSeconds(
        DateTimeOffset? resetAt,
        DateTimeOffset now,
        bool isStale = false)
    {
        if (isStale || resetAt is null) return null;
        return Math.Max(0L, (long)Math.Ceiling((resetAt.Value - now).TotalSeconds));
    }

    public static string FormatResetRemaining(
        DateTimeOffset? resetAt,
        DateTimeOffset now,
        bool isStale = false,
        bool compact = false)
    {
        var seconds = ResetRemainingSeconds(resetAt, now, isStale);
        if (seconds is null) return "--";
        if (seconds <= 0) return "0m";

        var totalMinutes = Math.Max(1L, (long)Math.Ceiling(seconds.Value / 60D));
        var days = totalMinutes / (24 * 60);
        var hours = totalMinutes / 60 % 24;
        var minutes = totalMinutes % 60;
        if (days > 0)
        {
            if (compact) return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
            return FormatUnits(($"{days}d", days), ($"{hours}h", hours), ($"{minutes}m", minutes));
        }

        if (hours > 0)
        {
            return FormatUnits(($"{hours}h", hours), ($"{minutes}m", minutes));
        }

        return $"{minutes}m";
    }

    private static string FormatUnits(params (string Text, long Value)[] units) =>
        string.Join(" ", units.Where(unit => unit.Value > 0).Select(unit => unit.Text));

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
