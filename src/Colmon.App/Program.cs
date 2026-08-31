using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Colmon;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        if (options.LayoutProbe)
        {
            ApplicationConfiguration.Initialize();
            using var font = new Font("Microsoft YaHei", 9F, FontStyle.Regular, GraphicsUnit.Point);
            var width = CodexWeeklyLayout.MeasureWindowWidth(font, 96, out var characterCellWidth);
            using var optionsDialog = new TaskbarWindowOptionsDialog(new WindowOptions("Probe title", 30));
            using var pomodoroOptionsDialog = new PomodoroOptionsDialog(new PomodoroOptions());
            var pomodoro = new PomodoroTimer(new PomodoroOptions(), DateTimeOffset.UnixEpoch);
            var pomodoroInitial = pomodoro.Snapshot(DateTimeOffset.UnixEpoch);
            pomodoro.Start(DateTimeOffset.UnixEpoch);
            var pomodoroStarted = pomodoro.Snapshot(DateTimeOffset.UnixEpoch);
            pomodoro.Advance(DateTimeOffset.UnixEpoch.AddMinutes(25));
            var pomodoroAfterWork = pomodoro.Snapshot(DateTimeOffset.UnixEpoch.AddMinutes(25));
            pomodoro.SkipCurrentStage(DateTimeOffset.UnixEpoch.AddMinutes(26));
            var pomodoroAfterSkip = pomodoro.Snapshot(DateTimeOffset.UnixEpoch.AddMinutes(26));
            var manualRestPomodoro = new PomodoroTimer(
                new PomodoroOptions(AutoRest: false), DateTimeOffset.UnixEpoch);
            manualRestPomodoro.Start(DateTimeOffset.UnixEpoch);
            manualRestPomodoro.Advance(DateTimeOffset.UnixEpoch.AddMinutes(25));
            var autoRestDisabled = manualRestPomodoro.Snapshot(DateTimeOffset.UnixEpoch.AddMinutes(25));
            var manualCyclePomodoro = new PomodoroTimer(
                new PomodoroOptions(AutoNextCycle: false), DateTimeOffset.UnixEpoch);
            manualCyclePomodoro.Start(DateTimeOffset.UnixEpoch);
            manualCyclePomodoro.Advance(DateTimeOffset.UnixEpoch.AddMinutes(30));
            var autoNextDisabled = manualCyclePomodoro.Snapshot(DateTimeOffset.UnixEpoch.AddMinutes(30));
            var fourCyclePomodoro = new PomodoroTimer(new PomodoroOptions(), DateTimeOffset.UnixEpoch);
            fourCyclePomodoro.Start(DateTimeOffset.UnixEpoch);
            fourCyclePomodoro.Advance(DateTimeOffset.UnixEpoch.AddMinutes(120));
            var afterFourCycles = fourCyclePomodoro.Snapshot(DateTimeOffset.UnixEpoch.AddMinutes(120));
            var skippedWorkPomodoro = new PomodoroTimer(new PomodoroOptions(), DateTimeOffset.UnixEpoch);
            skippedWorkPomodoro.SkipCurrentStage(DateTimeOffset.UnixEpoch.AddMinutes(1));
            var afterSkippedWork = skippedWorkPomodoro.Snapshot(DateTimeOffset.UnixEpoch.AddMinutes(1));
            var pauseResumePomodoro = new PomodoroTimer(new PomodoroOptions(), DateTimeOffset.UnixEpoch);
            pauseResumePomodoro.Start(DateTimeOffset.UnixEpoch);
            pauseResumePomodoro.Pause(DateTimeOffset.UnixEpoch.AddMinutes(1));
            var afterPause = pauseResumePomodoro.Snapshot(DateTimeOffset.UnixEpoch.AddMinutes(2));
            pauseResumePomodoro.Start(DateTimeOffset.UnixEpoch.AddMinutes(2));
            var afterResume = pauseResumePomodoro.Snapshot(DateTimeOffset.UnixEpoch.AddMinutes(2));
            var countCases = new long?[] { 0, 999, 1000, 1234567, null }
                .Select(value => new { value, formatted = TaskbarCountDisplay.FormatNumber(value) });
            var tokenFixture = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                type = "event_msg",
                payload = new { type = "token_count", info = new { last_token_usage = new { total_tokens = 1234 } } }
            });
            var cases = new[] { "9.9%", "10%", "100%", "unavailable" }.Select(text =>
            {
                var value = CodexWeeklyLayout.ParseRemainingPercent(text);
                return new
                {
                    input = text,
                    value,
                    formatted = CodexWeeklyLayout.FormatRemainingPercent(value),
                    isLow = CodexWeeklyLayout.IsLow(value),
                    color = ColorTranslator.ToHtml(CodexWeeklyLayout.ValueColor(value))
                };
            });
            var resetCases = new
            {
                fiveHour = CodexWeeklyLayout.FormatResetRemaining(
                    DateTimeOffset.UnixEpoch.AddHours(2).AddMinutes(37), DateTimeOffset.UnixEpoch),
                weekly = CodexWeeklyLayout.FormatResetRemaining(
                    DateTimeOffset.UnixEpoch.AddDays(6).AddHours(18).AddMinutes(42), DateTimeOffset.UnixEpoch),
                weeklyCompact = CodexWeeklyLayout.FormatResetRemaining(
                    DateTimeOffset.UnixEpoch.AddDays(6).AddHours(18).AddMinutes(42), DateTimeOffset.UnixEpoch,
                    compact: true),
                minutesOnly = CodexWeeklyLayout.FormatResetRemaining(
                    DateTimeOffset.UnixEpoch.AddMinutes(15), DateTimeOffset.UnixEpoch),
                expired = CodexWeeklyLayout.FormatResetRemaining(
                    DateTimeOffset.UnixEpoch.AddSeconds(-1), DateTimeOffset.UnixEpoch),
                unavailable = CodexWeeklyLayout.FormatResetRemaining(null, DateTimeOffset.UnixEpoch)
            };
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                dpi = 96,
                characterColumns = CodexWeeklyLayout.CharacterColumns,
                characterCellWidth,
                pixelWidth = width,
                optionsDialog = optionsDialog.SnapshotForDiagnostics(),
                pomodoro = new
                {
                    defaultOptions = new PomodoroOptions(),
                    optionsDialog = pomodoroOptionsDialog.SnapshotForDiagnostics(),
                    formattedTime = TaskbarPomodoroDisplay.FormatTime(TimeSpan.FromMinutes(25)),
                    dots = Enumerable.Range(0, 5).Select(TaskbarPomodoroDisplay.FormatDots),
                    initial = pomodoroInitial,
                    started = pomodoroStarted,
                    afterWork = pomodoroAfterWork,
                    afterSkip = pomodoroAfterSkip,
                    autoRestDisabled,
                    autoNextDisabled,
                    afterFourCycles,
                    afterSkippedWork,
                    afterPause,
                    afterResume
                },
                countCases,
                tokenTodayParser = new
                {
                    today = CodexTokenTodaySource.ParseTokenEvent(tokenFixture, DateOnly.FromDateTime(DateTime.Today)),
                    yesterday = CodexTokenTodaySource.ParseTokenEvent(tokenFixture, DateOnly.FromDateTime(DateTime.Today.AddDays(-1)))
                },
                cases,
                placementCases = new
                {
                    noConflictX = TaskbarPlacement.FindAvailableX(0, 2560, 2000, 105, 4, 0, []),
                    conflictX = TaskbarPlacement.FindAvailableX(0, 2560, 1836, 105, 4, 0,
                        [new NativeRect(1660, 1400, 1836, 1432)]),
                    conflictResolved = !TaskbarPlacement.Overlaps(0,
                        TaskbarPlacement.FindAvailableX(0, 2560, 1836, 105, 4, 0,
                            [new NativeRect(1660, 1400, 1836, 1432)]),
                        105,
                        [new NativeRect(1660, 1400, 1836, 1432)])
                },
                rpcParserCases = new
                {
                    camelCaseRemaining = ParseCodexFixture("""
                        {"rateLimits":{"secondary":{"usedPercent":88,"windowDurationMins":10080}}}
                        """),
                    snakeCaseRemaining = ParseCodexFixture("""
                        {"rate_limits_by_limit_id":{"codex":{"secondary":{"used_percent":5,"window_duration_mins":10080}}}}
                        """),
                    alternateRemaining = ParseCodexFixture("""
                        {"rateLimitsByLimitId":{"other":{"secondary":{"usedPercent":25,"windowDurationMins":10080}}}}
                        """),
                    fiveHourRemaining = ParseCodexFixture("""
                        {"rateLimits":{"primary":{"usedPercent":27,"windowDurationMins":300},"secondary":{"usedPercent":40,"windowDurationMins":10080}}}
                        """, CodexAppServerSource.FiveHourWindowMinutes)
                },
                resetCases,
                staleCases = new
                {
                    recentValueRetained = SourceCoordinator.ShouldRetain(
                        new InfoSample("42%", DateTimeOffset.UnixEpoch.AddMinutes(1)),
                        DateTimeOffset.UnixEpoch.AddMinutes(10),
                        TimeSpan.FromMinutes(10)),
                    expiredValueRejected = SourceCoordinator.ShouldRetain(
                        new InfoSample("42%", DateTimeOffset.UnixEpoch),
                        DateTimeOffset.UnixEpoch.AddMinutes(11),
                        TimeSpan.FromMinutes(10)),
                    unavailableRejected = SourceCoordinator.ShouldRetain(
                        new InfoSample("--%", DateTimeOffset.UnixEpoch.AddMinutes(9)),
                        DateTimeOffset.UnixEpoch.AddMinutes(10),
                        TimeSpan.FromMinutes(10))
                }
            }, JsonDefaults.Indented));
            return 0;
        }

        if (options.CodexProbe)
        {
            var started = Stopwatch.GetTimestamp();
            using var source = new CodexAppServerSource(new SourceConfig
            {
                Name = "codex-weekly",
                Type = "codex-weekly",
                TimeoutMilliseconds = 20_000
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var sample = source.ReadAsync(timeout.Token).GetAwaiter().GetResult();
            var value = CodexWeeklyLayout.ParseRemainingPercent(sample.Text);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = value is >= 0M and <= 100M,
                percentageText = CodexWeeklyLayout.FormatRemainingPercent(value),
                elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                candidateCount = CodexCommandLocator.Find(null).Count
            }, JsonDefaults.Indented));
            return 0;
        }

        if (options.TokensTodayProbe)
        {
            Directory.CreateDirectory(options.ArtifactDirectory);
            using var probeLog = new JsonLog(Path.Combine(options.ArtifactDirectory, "tokens-today-probe.jsonl"));
            using var source = new CodexTokenTodaySource(new SourceConfig
            {
                Name = "codex-tokens-today",
                Type = "codex-token-today",
                DataPath = options.TokensDataPath,
                TimeoutMilliseconds = 60_000,
                StaleAfterMilliseconds = 600_000
            }, probeLog);
            var started = Stopwatch.GetTimestamp();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            var sample = source.ReadAsync(timeout.Token).GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = long.TryParse(sample.Text.TrimStart('~'), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out _),
                text = sample.Text,
                stale = sample.IsStale,
                error = sample.Error,
                elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            }, JsonDefaults.Indented));
            return 0;
        }

        if (options.ProbeOnly)
        {
            Console.WriteLine(JsonSerializer.Serialize(TaskbarProbe.Capture(), JsonDefaults.Indented));
            return 0;
        }

        Directory.CreateDirectory(options.ArtifactDirectory);
        using var log = new JsonLog(Path.Combine(options.ArtifactDirectory, "colmon.jsonl"));

        try
        {
            ApplicationConfiguration.Initialize();
            var config = AppConfig.Load(options.ConfigPath, log);
            using var coordinator = new SourceCoordinator(config.Sources, log, config.Separator);
            var configuredCodexCommand = config.Sources
                .FirstOrDefault(source => source.Type.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
                    source.Type.Equals("codex-weekly", StringComparison.OrdinalIgnoreCase))?.Command;
            var fiveHourSource = new SourceConfig
            {
                Name = "codex-five-hour",
                Type = "codex-five-hour",
                Command = configuredCodexCommand,
                PollMilliseconds = config.CodexFiveHourPollMilliseconds,
                TimeoutMilliseconds = 20_000,
                StaleAfterMilliseconds = 600_000,
                WindowDurationMinutes = CodexAppServerSource.FiveHourWindowMinutes
            };
            using var fiveHourCoordinator = config.ShowCodexFiveHourLimit
                ? new SourceCoordinator([fiveHourSource], log, config.Separator)
                : null;
            var tokenTodaySource = new SourceConfig
            {
                Name = "codex-tokens-today",
                Type = "codex-token-today",
                PollMilliseconds = config.TokensTodayPollMilliseconds,
                TimeoutMilliseconds = 60_000,
                StaleAfterMilliseconds = 600_000
            };
            using var tokenTodayCoordinator = config.ShowTokensToday
                ? new SourceCoordinator([tokenTodaySource], log, config.Separator)
                : null;
            using var context = new ColmonApplicationContext(log);
            var windowOptionsPath = options.ControlSmoke
                ? Path.Combine(options.ArtifactDirectory, "codex-weekly.options.json")
                : null;
            context.RegisterTaskbarWindow(new TaskbarHostForm(
                config,
                coordinator,
                options.ArtifactDirectory,
                log,
                windowOptionsPath));
            var nextSlotIndex = 1;
            if (fiveHourCoordinator is not null)
            {
                var fiveHourOptionsPath = options.ControlSmoke
                    ? Path.Combine(options.ArtifactDirectory, "codex-five-hour.options.json")
                    : null;
                context.RegisterTaskbarWindow(new TaskbarCodexLimitHostForm(
                    config.CodexFiveHourTitle,
                    config.OffsetX,
                    config.OffsetY,
                    fiveHourSource,
                    fiveHourCoordinator,
                    nextSlotIndex++,
                    options.ArtifactDirectory,
                    log,
                    fiveHourOptionsPath));
            }
            if (tokenTodayCoordinator is not null)
            {
                var tokenOptionsPath = options.ControlSmoke
                    ? Path.Combine(options.ArtifactDirectory, "codex-tokens-today.options.json")
                    : null;
                context.RegisterTaskbarWindow(new TaskbarCountHostForm(
                    config.TokensTodayTitle,
                    config.OffsetX,
                    config.OffsetY,
                    tokenTodaySource,
                    tokenTodayCoordinator,
                    nextSlotIndex++,
                    options.ArtifactDirectory,
                    log,
                    tokenOptionsPath));
            }
            if (config.ShowPomodoro)
            {
                var pomodoroStatePath = options.ControlSmoke
                    ? Path.Combine(options.ArtifactDirectory, "pomodoro.options.json")
                    : null;
                context.RegisterTaskbarWindow(new TaskbarPomodoroHostForm(
                    config.OffsetX,
                    config.OffsetY,
                    new PomodoroOptions(
                        config.PomodoroAutoRest,
                        config.PomodoroAutoNextCycle,
                        config.PomodoroWorkMinutes,
                        config.PomodoroRestMinutes),
                    nextSlotIndex,
                    options.ArtifactDirectory,
                    log,
                    pomodoroStatePath));
            }
            coordinator.Start();
            fiveHourCoordinator?.Start();
            tokenTodayCoordinator?.Start();
            context.Start(options.ControlSmoke);
            Application.Run(context);
            return 0;
        }
        catch (Exception exception)
        {
            log.Write("fatal", new { exception.Message, exception.StackTrace });
            MessageBox.Show(exception.Message, "Colmon failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static decimal ParseCodexFixture(string json, int? targetWindowMinutes = null)
    {
        using var document = JsonDocument.Parse(json);
        return targetWindowMinutes is null
            ? CodexAppServerSource.ParseRemainingPercent(document.RootElement)
            : CodexAppServerSource.ParseQuotaReading(document.RootElement, targetWindowMinutes.Value).RemainingPercent;
    }
}

internal sealed record AppOptions(
    string? ConfigPath,
    string ArtifactDirectory,
    bool ProbeOnly,
    bool ControlSmoke,
    bool LayoutProbe,
    bool CodexProbe,
    bool TokensTodayProbe,
    string? TokensDataPath)
{
    public static AppOptions Parse(string[] args)
    {
        string? config = null;
        var artifactDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Colmon",
            "artifacts",
            "runtime");
        var probe = false;
        var controlSmoke = false;
        var layoutProbe = false;
        var codexProbe = false;
        var tokensTodayProbe = false;
        string? tokensDataPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config" when index + 1 < args.Length:
                    config = Path.GetFullPath(args[++index]);
                    break;
                case "--artifact-dir" when index + 1 < args.Length:
                    artifactDirectory = Path.GetFullPath(args[++index]);
                    break;
                case "--probe":
                    probe = true;
                    break;
                case "--control-smoke":
                    controlSmoke = true;
                    break;
                case "--layout-probe":
                    layoutProbe = true;
                    break;
                case "--codex-probe":
                    codexProbe = true;
                    break;
                case "--tokens-today-probe":
                    tokensTodayProbe = true;
                    break;
                case "--tokens-data-path" when index + 1 < args.Length:
                    tokensDataPath = Path.GetFullPath(args[++index]);
                    break;
            }
        }

        return new AppOptions(config, artifactDirectory, probe, controlSmoke, layoutProbe, codexProbe,
            tokensTodayProbe, tokensDataPath);
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
