using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;

namespace Colmon;

internal sealed record InfoSample(string Text, DateTimeOffset CapturedAt, string? Error = null, bool IsStale = false);

internal interface IInfoSource : IDisposable
{
    string Name { get; }
    TimeSpan PollInterval { get; }
    TimeSpan Timeout { get; }
    TimeSpan StaleAfter { get; }
    Task<InfoSample> ReadAsync(CancellationToken cancellationToken);
}

internal abstract class InfoSource(SourceConfig config) : IInfoSource
{
    protected SourceConfig Config { get; } = config;
    public string Name => Config.Name;
    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(Math.Clamp(Config.PollMilliseconds, 100, 60_000));
    public TimeSpan Timeout => TimeSpan.FromMilliseconds(Math.Clamp(Config.TimeoutMilliseconds, 100, 60_000));
    public TimeSpan StaleAfter => TimeSpan.FromMilliseconds(Math.Clamp(Config.StaleAfterMilliseconds, 1000, 3_600_000));
    public abstract Task<InfoSample> ReadAsync(CancellationToken cancellationToken);
    public virtual void Dispose() { }
    protected InfoSample Sample(string value) => new($"{Config.Prefix}{value}", DateTimeOffset.Now);
}

internal sealed class ClockSource(SourceConfig config) : InfoSource(config)
{
    public override Task<InfoSample> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Sample(DateTime.Now.ToString(Config.Format)));
}

internal sealed class HttpTextSource(SourceConfig config) : InfoSource(config)
{
    private readonly HttpClient _client = new() { DefaultRequestHeaders = { UserAgent = { new("Colmon", "0.1") } } };

    public override async Task<InfoSample> ReadAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Config.Url))
            throw new InvalidDataException($"Source '{Name}' requires url.");

        var content = await _client.GetStringAsync(Config.Url, cancellationToken);
        if (string.IsNullOrWhiteSpace(Config.JsonPath))
            return Sample(content.Trim());

        using var document = JsonDocument.Parse(content);
        var node = document.RootElement;
        foreach (var segment in Config.JsonPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            node = node.GetProperty(segment);
        return Sample(node.ValueKind == JsonValueKind.String ? node.GetString() ?? "" : node.ToString());
    }

    public override void Dispose() => _client.Dispose();
}

internal sealed class TcpLineSource(SourceConfig config) : InfoSource(config)
{
    public override async Task<InfoSample> ReadAsync(CancellationToken cancellationToken)
    {
        if (Config.Port is < 1 or > 65535)
            throw new InvalidDataException($"Source '{Name}' requires a valid port.");

        using var client = new TcpClient();
        await client.ConnectAsync(Config.Host, Config.Port, cancellationToken);
        using var reader = new StreamReader(client.GetStream());
        var line = await reader.ReadLineAsync(cancellationToken);
        return Sample(line?.Trim() ?? throw new IOException("The TCP peer closed without a line."));
    }
}

internal sealed class SourceCoordinator : IDisposable
{
    private readonly List<IInfoSource> _sources;
    private readonly ConcurrentDictionary<string, InfoSample> _latest = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly JsonLog _log;
    private readonly string _separator;
    private readonly List<Task> _workers = [];
    private readonly object _scheduleLock = new();
    private TaskCompletionSource _scheduleChanged = NewScheduleSignal();
    private TimeSpan? _pollIntervalOverride;

    public event Action<string>? TextChanged;

    public TimeSpan PollInterval
    {
        get
        {
            lock (_scheduleLock)
                return _pollIntervalOverride ??
                    (_sources.Count == 0 ? TimeSpan.FromMinutes(1) : _sources.Min(source => source.PollInterval));
        }
    }

    public SourceCoordinator(IEnumerable<SourceConfig> configs, JsonLog log, string separator = "  ·  ")
    {
        _log = log;
        _separator = separator;
        _sources = configs.Select(CreateSource).ToList();
        if (_sources.Count == 0)
                _sources.Add(new CodexAppServerSource(new SourceConfig
                {
                    Name = "codex-weekly",
                    Type = "codex-weekly",
                    PollMilliseconds = 60_000,
                    TimeoutMilliseconds = 20_000
                }, _log));
    }

    public void Start()
    {
        foreach (var source in _sources)
            _workers.Add(Task.Run(() => RunSourceAsync(source, _stopping.Token)));
    }

    public void SetPollInterval(TimeSpan interval)
    {
        interval = TimeSpan.FromSeconds(Math.Clamp(
            interval.TotalSeconds,
            WindowOptions.MinimumRefreshIntervalSeconds,
            WindowOptions.MaximumRefreshIntervalSeconds));

        TaskCompletionSource changed;
        lock (_scheduleLock)
        {
            if (_pollIntervalOverride == interval) return;
            _pollIntervalOverride = interval;
            changed = _scheduleChanged;
            _scheduleChanged = NewScheduleSignal();
        }
        changed.TrySetResult();
        _log.Write("source.poll-interval.changed", new { seconds = interval.TotalSeconds });
    }

    private async Task RunSourceAsync(IInfoSource source, CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(source.Timeout);
                var sample = await source.ReadAsync(timeout.Token);
                failures = 0;
                _latest[source.Name] = sample;
                Publish();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                failures++;
                var now = DateTimeOffset.Now;
                var retained = _latest.TryGetValue(source.Name, out var previous) &&
                    ShouldRetain(previous, now, source.StaleAfter);
                _latest[source.Name] = retained
                    ? previous! with { Text = $"~{previous.Text.TrimStart('~')}", Error = exception.Message, IsStale = true }
                    : new InfoSample("--%", now, exception.Message);
                _log.Write("source.error", new { source = source.Name, failures, retained, exception.Message });
                Publish();
            }

            TimeSpan baseInterval;
            lock (_scheduleLock) baseInterval = _pollIntervalOverride ?? source.PollInterval;
            var backoff = failures == 0 ? baseInterval : TimeSpan.FromMilliseconds(
                Math.Min(30_000, baseInterval.TotalMilliseconds * Math.Pow(2, Math.Min(failures, 5))));
            Task scheduleChanged;
            lock (_scheduleLock) scheduleChanged = _scheduleChanged.Task;
            try { await Task.WhenAny(Task.Delay(backoff, stoppingToken), scheduleChanged); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private void Publish()
    {
        var parts = _sources.Select(source => _latest.TryGetValue(source.Name, out var sample) ? sample.Text : $"… {source.Name}");
        TextChanged?.Invoke(string.Join(_separator, parts));
    }

    internal static bool ShouldRetain(InfoSample previous, DateTimeOffset now, TimeSpan staleAfter) =>
        previous.Text != "--%" && now >= previous.CapturedAt && now - previous.CapturedAt <= staleAfter;

    private static TaskCompletionSource NewScheduleSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IInfoSource CreateSource(SourceConfig config) => config.Type.ToLowerInvariant() switch
    {
        "clock" => new ClockSource(config),
        "codex" or "codex-weekly" or "codex-five-hour" or "codex-5h" => new CodexAppServerSource(config, _log),
        "codex-token-today" or "tokens-today" => new CodexTokenTodaySource(config, _log),
        "http" or "http-json" => new HttpTextSource(config),
        "tcp" or "tcp-line" => new TcpLineSource(config),
        _ => throw new InvalidDataException($"Unsupported source type '{config.Type}' for '{config.Name}'.")
    };

    public void Dispose()
    {
        _stopping.Cancel();
        try { Task.WaitAll(_workers.ToArray(), TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        foreach (var source in _sources) source.Dispose();
        _stopping.Dispose();
    }
}
