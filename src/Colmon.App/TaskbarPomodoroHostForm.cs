namespace Colmon;

internal sealed class TaskbarPomodoroHostForm : TaskbarMetricForm
{
    private readonly ToolStripMenuItem _runStateItem = new("启动");
    private readonly ToolStripMenuItem _resetInitialItem = new("复原至初始");
    private readonly ToolStripMenuItem _resetStageItem = new("复原至该阶段起始");
    private readonly ToolStripMenuItem _skipStageItem = new("跳过该阶段");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly PomodoroStore _store;
    private readonly PomodoroTimer _pomodoro;
    private readonly TaskbarPomodoroDisplay _display;

    public TaskbarPomodoroHostForm(
        int offsetX,
        int offsetY,
        PomodoroOptions defaults,
        int slotIndex,
        string artifactDirectory,
        JsonLog log,
        string? stateStorePath = null)
        : base(
            "pomodoro",
            "Pomodoro",
            offsetX,
            offsetY,
            slotIndex,
            "pomodoro.state.json",
            new TaskbarPomodoroDisplay(),
            1,
            null,
            artifactDirectory,
            log,
            (stateStorePath ?? PomodoroStore.DefaultPath) + ".window")
    {
        _display = (TaskbarPomodoroDisplay)View.Control;
        _store = new PomodoroStore(stateStorePath ?? PomodoroStore.DefaultPath, log);
        var saved = _store.Load();
        _pomodoro = new PomodoroTimer(saved?.Options ?? defaults, DateTimeOffset.Now);

        WindowMenu.Items.Insert(0, _runStateItem);
        WindowMenu.Items.Insert(1, new ToolStripSeparator());
        WindowMenu.Items.Insert(2, _resetInitialItem);
        WindowMenu.Items.Insert(3, _resetStageItem);
        WindowMenu.Items.Insert(4, _skipStageItem);
        WindowMenu.Items.Insert(5, new ToolStripSeparator());
        _runStateItem.Click += (_, _) => ToggleRunning();
        _resetInitialItem.Click += (_, _) => ExecuteCommand("reset-initial", now => _pomodoro.ResetInitial(now));
        _resetStageItem.Click += (_, _) => ExecuteCommand("reset-stage", now => _pomodoro.ResetCurrentStage(now));
        _skipStageItem.Click += (_, _) => ExecuteCommand("skip-stage", now => _pomodoro.SkipCurrentStage(now));
        _timer.Tick += (_, _) => TickTimer();

        UpdateDisplay(DateTimeOffset.Now, persistState: true);
    }

    internal PomodoroOptions PomodoroOptionsForDiagnostics => _pomodoro.Options;
    internal PomodoroSnapshot PomodoroSnapshotForDiagnostics => _pomodoro.Snapshot(DateTimeOffset.Now);
    internal bool HasPomodoroMenuCommandsForDiagnostics =>
        WindowMenuItemsForDiagnostics.SequenceEqual([
            _pomodoro.Snapshot(DateTimeOffset.Now).IsRunning ? "暂停" : "启动",
            "复原至初始",
            "复原至该阶段起始",
            "跳过该阶段",
            "窗口选项",
            "隐藏该窗口",
            "关闭该窗口"]);
    internal string RunStateMenuTextForDiagnostics => _runStateItem.Text ?? string.Empty;
    internal string RunStateActionForDiagnostics =>
        _pomodoro.Snapshot(DateTimeOffset.Now).IsRunning ? "pause" : "start";

    internal void ApplyPomodoroOptionsForDiagnostics(PomodoroOptions options, bool persist = false)
    {
        _pomodoro.ApplyOptions(options, DateTimeOffset.Now);
        UpdateDisplay(DateTimeOffset.Now, persist);
    }

    internal void PerformResetInitialForDiagnostics() => _resetInitialItem.PerformClick();
    internal void PerformResetStageForDiagnostics() => _resetStageItem.PerformClick();
    internal void PerformSkipStageForDiagnostics() => _skipStageItem.PerformClick();
    internal void PerformRunStateCommandForDiagnostics() => _runStateItem.PerformClick();

    protected override void OnLoad(EventArgs eventArgs)
    {
        base.OnLoad(eventArgs);
        _timer.Start();
    }

    protected override void ShowOptionsDialog()
    {
        using var dialog = new PomodoroOptionsDialog(_pomodoro.Options);
        Log.Write("pomodoro.options.opened", new { windowId = WindowId });
        if (dialog.ShowDialog() != DialogResult.OK) return;

        try
        {
            _pomodoro.ApplyOptions(dialog.Options, DateTimeOffset.Now);
            UpdateDisplay(DateTimeOffset.Now, persistState: true);
            Log.Write("pomodoro.options.applied", dialog.Options);
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(exception.Message, "Colmon", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void TickTimer() => UpdateDisplay(DateTimeOffset.Now, persistState: false);

    private void ToggleRunning()
    {
        var now = DateTimeOffset.Now;
        if (_pomodoro.Snapshot(now).IsRunning) _pomodoro.Pause(now);
        else _pomodoro.Start(now);
        UpdateDisplay(now, persistState: true);
        Log.Write("pomodoro.command", new
        {
            command = _pomodoro.Snapshot(now).IsRunning ? "start" : "pause",
            phase = _pomodoro.Snapshot(now).Phase.ToString()
        });
    }

    private void ExecuteCommand(string command, Action<DateTimeOffset> action)
    {
        var now = DateTimeOffset.Now;
        action(now);
        UpdateDisplay(now, persistState: true);
        Log.Write("pomodoro.command", new
        {
            command,
            phase = _pomodoro.Snapshot(now).Phase.ToString(),
            completedWorkPeriods = _pomodoro.Snapshot(now).CompletedWorkPeriods
        });
    }

    private void UpdateDisplay(DateTimeOffset now, bool persistState)
    {
        var transitioned = _pomodoro.Advance(now);
        var snapshot = _pomodoro.Snapshot(now);
        _display.TimerSnapshot = snapshot;
        _runStateItem.Text = snapshot.IsRunning ? "暂停" : "启动";
        if (persistState || transitioned) SaveState(showError: false);
        RefreshPlacement(transitioned ? "pomodoro-transition" : "pomodoro-tick");
    }

    private void SaveState(bool showError)
    {
        try
        {
            _store.Save(_pomodoro.PersistentState());
        }
        catch (InvalidOperationException exception)
        {
            if (showError)
                MessageBox.Show(exception.Message, "Colmon", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            SaveState(showError: false);
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
