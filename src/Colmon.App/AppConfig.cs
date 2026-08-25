using System.Text.Json;

namespace Colmon;

internal sealed class AppConfig
{
    public string Title { get; init; } = CodexWeeklyLayout.Title;
    public string Separator { get; init; } = "  ·  ";
    public int HorizontalPadding { get; init; } = 10;
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
    public bool ShowTokensToday { get; init; } = true;
    public string TokensTodayTitle { get; init; } = "Tokens today";
    public int TokensTodayPollMilliseconds { get; init; } = 60_000;
    public bool ShowPomodoro { get; init; } = true;
    public bool PomodoroAutoRest { get; init; } = true;
    public bool PomodoroAutoNextCycle { get; init; } = true;
    public int PomodoroWorkMinutes { get; init; } = 25;
    public int PomodoroRestMinutes { get; init; } = 5;
    public List<SourceConfig> Sources { get; init; } =
    [
        new()
        {
            Name = "codex-weekly",
            Type = "codex-weekly",
            PollMilliseconds = 60_000,
            TimeoutMilliseconds = 20_000,
            StaleAfterMilliseconds = 600_000
        }
    ];

    public static AppConfig Load(string? path, JsonLog log)
    {
        if (path is null || !File.Exists(path))
        {
            log.Write("config.default", new { path });
            return new AppConfig();
        }

        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonDefaults.Indented)
            ?? throw new InvalidDataException($"Configuration is empty: {path}");
        log.Write("config.loaded", new { path, sourceCount = config.Sources.Count });
        return config;
    }
}

internal sealed class SourceConfig
{
    public string Name { get; init; } = "source";
    public string Type { get; init; } = "clock";
    public string? Url { get; init; }
    public string? JsonPath { get; init; }
    public string? Command { get; init; }
    public string? DataPath { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; }
    public string Format { get; init; } = "HH:mm:ss";
    public string Prefix { get; init; } = "";
    public int PollMilliseconds { get; init; } = 1000;
    public int TimeoutMilliseconds { get; init; } = 2000;
    public int StaleAfterMilliseconds { get; init; } = 600_000;
}
