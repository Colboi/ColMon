using System.Text.Json;

namespace Colmon;

internal sealed record WindowOptions(string Title, int RefreshIntervalSeconds)
{
    public const int MinimumRefreshIntervalSeconds = 10;
    public const int MaximumRefreshIntervalSeconds = 3600;

    public WindowOptions Normalize(string fallbackTitle = "Progress") => new(
        string.IsNullOrWhiteSpace(Title) ? fallbackTitle : Title.Trim(),
        Math.Clamp(RefreshIntervalSeconds, MinimumRefreshIntervalSeconds, MaximumRefreshIntervalSeconds));
}

internal sealed class WindowOptionsStore(string path, JsonLog log)
{
    public static string DefaultPath(string windowId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safeId = new string(windowId.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        if (string.IsNullOrWhiteSpace(safeId)) safeId = "window";
        return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Colmon",
        safeId + ".json");
    }

    public WindowOptions Load(WindowOptions defaults)
    {
        if (!File.Exists(path)) return defaults.Normalize(defaults.Title);

        try
        {
            return (JsonSerializer.Deserialize<WindowOptions>(File.ReadAllText(path), JsonDefaults.Indented) ?? defaults)
                .Normalize(defaults.Title);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Write("window.options.load.error", new { path, exception.Message });
            return defaults.Normalize(defaults.Title);
        }
    }

    public void Save(WindowOptions options)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Options path has no directory.");
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(options.Normalize(), JsonDefaults.Indented));
            log.Write("window.options.saved", new { path, options.Title, options.RefreshIntervalSeconds });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Write("window.options.save.error", new { path, exception.Message });
            throw new InvalidOperationException("窗口设置无法保存。", exception);
        }
    }
}
