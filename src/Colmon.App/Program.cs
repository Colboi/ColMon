using System.Diagnostics;
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
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                dpi = 96,
                characterColumns = CodexWeeklyLayout.CharacterColumns,
                characterCellWidth,
                pixelWidth = width,
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
                        """)
                },
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
            using var context = new ColmonApplicationContext(log);
            context.RegisterTaskbarWindow(new TaskbarHostForm(config, coordinator, options.ArtifactDirectory, log));
            coordinator.Start();
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

    private static decimal ParseCodexFixture(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CodexAppServerSource.ParseRemainingPercent(document.RootElement);
    }
}

internal sealed record AppOptions(
    string? ConfigPath,
    string ArtifactDirectory,
    bool ProbeOnly,
    bool ControlSmoke,
    bool LayoutProbe,
    bool CodexProbe)
{
    public static AppOptions Parse(string[] args)
    {
        string? config = null;
        var artifactDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "runtime"));
        var probe = false;
        var controlSmoke = false;
        var layoutProbe = false;
        var codexProbe = false;

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
            }
        }

        return new AppOptions(config, artifactDirectory, probe, controlSmoke, layoutProbe, codexProbe);
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
