using System.Text.Json;

namespace Colmon;

internal enum PomodoroPhase
{
    Work,
    Rest
}

internal sealed record PomodoroOptions(
    bool AutoRest = true,
    bool AutoNextCycle = true,
    int WorkMinutes = 25,
    int RestMinutes = 5)
{
    public const int MinimumMinutes = 1;
    public const int MaximumMinutes = 1440;

    public PomodoroOptions Normalize() => this with
    {
        WorkMinutes = Math.Clamp(WorkMinutes, MinimumMinutes, MaximumMinutes),
        RestMinutes = Math.Clamp(RestMinutes, MinimumMinutes, MaximumMinutes)
    };
}

internal sealed record PomodoroPersistentState(
    PomodoroOptions Options,
    PomodoroPhase Phase,
    DateTimeOffset StageStartedAt,
    DateTimeOffset? EndsAt,
    int CompletedWorkPeriods,
    double PausedRemainingSeconds = 0);

internal sealed record PomodoroSnapshot(
    PomodoroPhase Phase,
    TimeSpan Remaining,
    TimeSpan Duration,
    int CompletedWorkPeriods,
    bool IsRunning,
    PomodoroOptions Options)
{
    public decimal RemainingRatio => Duration <= TimeSpan.Zero
        ? 0M
        : Math.Clamp((decimal)(Remaining.TotalMilliseconds / Duration.TotalMilliseconds), 0M, 1M);
}

internal sealed class PomodoroTimer
{
    private PomodoroOptions _options;
    private PomodoroPhase _phase;
    private DateTimeOffset _stageStartedAt;
    private DateTimeOffset? _endsAt;
    private TimeSpan _pausedRemaining;
    private int _completedWorkPeriods;

    public PomodoroTimer(PomodoroOptions options, DateTimeOffset now)
    {
        _options = options.Normalize();
        ResetInitial(now);
    }

    public PomodoroOptions Options => _options;

    public bool Advance(DateTimeOffset now)
    {
        var transitioned = false;
        var transitionLimit = 10_000;
        while (_endsAt is { } boundary && now >= boundary && transitionLimit-- > 0)
        {
            transitioned = true;
            if (_phase == PomodoroPhase.Work)
            {
                CompleteWorkPeriod();
                PreparePhase(PomodoroPhase.Rest, boundary);
                if (_options.AutoRest) Start(boundary);
                else break;
            }
            else
            {
                FinishRest(boundary);
                if (_endsAt is null) break;
            }
        }

        if (transitionLimit <= 0)
            throw new InvalidOperationException("Pomodoro catch-up exceeded the transition safety limit.");
        return transitioned;
    }

    public void ResetInitial(DateTimeOffset now)
    {
        _completedWorkPeriods = 0;
        PreparePhase(PomodoroPhase.Work, now);
    }

    public void ResetCurrentStage(DateTimeOffset now) => PreparePhase(_phase, now);

    public void Start(DateTimeOffset now)
    {
        if (_endsAt is not null) return;
        if (_pausedRemaining <= TimeSpan.Zero) _pausedRemaining = DurationFor(_phase);
        _stageStartedAt = now;
        _endsAt = now + _pausedRemaining;
    }

    public void Pause(DateTimeOffset now)
    {
        Advance(now);
        if (_endsAt is not { } end) return;
        _pausedRemaining = end - now;
        if (_pausedRemaining < TimeSpan.Zero) _pausedRemaining = TimeSpan.Zero;
        _endsAt = null;
    }

    public void SkipCurrentStage(DateTimeOffset now)
    {
        if (_phase == PomodoroPhase.Work)
        {
            CompleteWorkPeriod();
            PreparePhase(PomodoroPhase.Rest, now);
            if (_options.AutoRest) Start(now);
        }
        else FinishRest(now);
    }

    public void ApplyOptions(PomodoroOptions options, DateTimeOffset now)
    {
        _options = options.Normalize();
        ResetCurrentStage(now);
    }

    public PomodoroSnapshot Snapshot(DateTimeOffset now)
    {
        var duration = DurationFor(_phase);
        var remaining = _endsAt is { } end
            ? end - now
            : _pausedRemaining;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        if (remaining > duration) remaining = duration;
        return new PomodoroSnapshot(
            _phase,
            remaining,
            duration,
            _completedWorkPeriods,
            _endsAt is not null,
            _options);
    }

    public PomodoroPersistentState PersistentState() => new(
        _options,
        _phase,
        _stageStartedAt,
        _endsAt,
        _completedWorkPeriods,
        _pausedRemaining.TotalSeconds);

    private void FinishRest(DateTimeOffset now)
    {
        if (_completedWorkPeriods >= 4)
        {
            ResetInitial(now);
            return;
        }

        PreparePhase(PomodoroPhase.Work, now);
        if (_options.AutoNextCycle) Start(now);
    }

    private void CompleteWorkPeriod() =>
        _completedWorkPeriods = Math.Min(4, _completedWorkPeriods + 1);

    private void PreparePhase(PomodoroPhase phase, DateTimeOffset now)
    {
        _phase = phase;
        _stageStartedAt = now;
        _endsAt = null;
        _pausedRemaining = DurationFor(phase);
    }

    private TimeSpan DurationFor(PomodoroPhase phase) => TimeSpan.FromMinutes(
        phase == PomodoroPhase.Work ? _options.WorkMinutes : _options.RestMinutes);
}

internal sealed class PomodoroStore(string path, JsonLog log)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Colmon",
        "pomodoro.json");

    public PomodoroPersistentState? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<PomodoroPersistentState>(File.ReadAllText(path), JsonDefaults.Indented);
            if (state?.Options is null)
            {
                log.Write("pomodoro.state.load.invalid", new { path, reason = "missing-options" });
                return null;
            }
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Write("pomodoro.state.load.error", new { path, exception.Message });
            return null;
        }
    }

    public void Save(PomodoroPersistentState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Pomodoro state path has no directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonDefaults.Indented));
            File.Move(temporaryPath, path, true);
            log.Write("pomodoro.state.saved", new
            {
                path,
                phase = state.Phase.ToString(),
                state.CompletedWorkPeriods,
                running = state.EndsAt is not null
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Write("pomodoro.state.save.error", new { path, exception.Message });
            throw new InvalidOperationException("番茄钟状态无法保存。", exception);
        }
    }
}
