namespace Colmon;

internal sealed class ColmonApplicationContext : ApplicationContext
{
    private readonly JsonLog _log;
    private readonly TaskbarWindowManager _windows;
    private readonly NotifyIconController _tray;
    private System.Windows.Forms.Timer? _controlSmokeTimer;
    private readonly List<TaskbarMetricForm> _diagnosticWindows = [];
    private int _controlSmokeStep;
    private bool _exiting;

    public ColmonApplicationContext(JsonLog log)
    {
        _log = log;
        _windows = new TaskbarWindowManager(log);
        _tray = new NotifyIconController(_windows, log, RequestExit);
    }

    public void RegisterTaskbarWindow(Form window)
    {
        _windows.Register(window);
        if (window is TaskbarMetricForm taskbarHost) _diagnosticWindows.Add(taskbarHost);
    }

    public void Start(bool controlSmoke)
    {
        _windows.ShowAll();
        if (!controlSmoke) return;

        _controlSmokeTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _controlSmokeTimer.Tick += RunControlSmokeStep;
        _controlSmokeTimer.Start();
    }

    private void RunControlSmokeStep(object? sender, EventArgs eventArgs)
    {
        _controlSmokeStep++;
        switch (_controlSmokeStep)
        {
            case 1:
                _tray.PerformVisibilityCommandForDiagnostics();
                _log.Write("control-smoke.hidden", new
                {
                    windowCount = _windows.WindowCount,
                    visibleCount = _windows.VisibleCount,
                    allVisible = _windows.AllVisible,
                    menuAction = _tray.VisibilityCommandForDiagnostics,
                    menuText = _tray.VisibilityCommandTextForDiagnostics
                });
                break;
            case 2:
                _tray.PerformVisibilityCommandForDiagnostics();
                _log.Write("control-smoke.shown", new
                {
                    windowCount = _windows.WindowCount,
                    visibleCount = _windows.VisibleCount,
                    allVisible = _windows.AllVisible,
                    menuAction = _tray.VisibilityCommandForDiagnostics,
                    menuText = _tray.VisibilityCommandTextForDiagnostics
                });
                break;
            case 3:
                if (_diagnosticWindows.Count < 3) throw new InvalidOperationException("Control smoke windows were not registered.");
                var progress = _diagnosticWindows.OfType<TaskbarHostForm>().Single();
                var fiveHour = _diagnosticWindows.OfType<TaskbarCodexLimitHostForm>().SingleOrDefault();
                var count = _diagnosticWindows.OfType<TaskbarCountHostForm>().Single();
                progress.ApplyOptionsForDiagnostics(new WindowOptions("Reusable progress", 30), persist: true);
                fiveHour?.ApplyOptionsForDiagnostics(new WindowOptions("Reusable 5h progress", 35), persist: true);
                count.ApplyOptionsForDiagnostics(new WindowOptions("Reusable count", 45), persist: true);
                var pomodoro = _diagnosticWindows.OfType<TaskbarPomodoroHostForm>().Single();
                pomodoro.ApplyPomodoroOptionsForDiagnostics(new PomodoroOptions(false, false, 30, 10), persist: true);
                var initialPomodoro = pomodoro.PomodoroSnapshotForDiagnostics;
                var initialRunMenuText = pomodoro.RunStateMenuTextForDiagnostics;
                var initialRunAction = pomodoro.RunStateActionForDiagnostics;
                pomodoro.PerformRunStateCommandForDiagnostics();
                var runningPomodoro = pomodoro.PomodoroSnapshotForDiagnostics;
                var runningMenuText = pomodoro.RunStateMenuTextForDiagnostics;
                var runningAction = pomodoro.RunStateActionForDiagnostics;
                pomodoro.PerformRunStateCommandForDiagnostics();
                var pausedPomodoro = pomodoro.PomodoroSnapshotForDiagnostics;
                var pausedMenuText = pomodoro.RunStateMenuTextForDiagnostics;
                var pausedAction = pomodoro.RunStateActionForDiagnostics;
                pomodoro.PerformResetInitialForDiagnostics();
                pomodoro.PerformSkipStageForDiagnostics();
                var trayWindowIds = _tray.WindowToggleWindowIdsForDiagnostics;
                var trayHideSequence = new List<int>();
                foreach (var windowId in trayWindowIds)
                {
                    _tray.PerformWindowToggleForDiagnostics(windowId);
                    trayHideSequence.Add(_windows.VisibleCount);
                }

                var trayHiddenVisibleCount = _windows.VisibleCount;
                var trayShowSequence = new List<int>();
                foreach (var windowId in trayWindowIds)
                {
                    _tray.PerformWindowToggleForDiagnostics(windowId);
                    trayShowSequence.Add(_windows.VisibleCount);
                }

                _log.Write("control-smoke.tray-window-toggles", new
                {
                    itemCount = _tray.WindowToggleItemCountForDiagnostics,
                    windowIds = trayWindowIds,
                    itemTexts = _tray.WindowToggleTextsForDiagnostics,
                    hideSequence = trayHideSequence,
                    hiddenVisibleCount = trayHiddenVisibleCount,
                    showSequence = trayShowSequence,
                    restoredVisibleCount = _windows.VisibleCount
                });
                _log.Write("control-smoke.window-options", new
                {
                    allMenusHaveRequiredCommands = _diagnosticWindows.All(window => window.HasRequiredWindowMenuCommandsForDiagnostics),
                    menuItemCounts = _diagnosticWindows.Select(window => window.WindowMenuItemsForDiagnostics.Count),
                    progress = progress.OptionsForDiagnostics,
                    fiveHour = fiveHour?.OptionsForDiagnostics,
                    count = count.OptionsForDiagnostics,
                    pomodoro = new
                    {
                        options = pomodoro.PomodoroOptionsForDiagnostics,
                        pomodoro.HasPomodoroMenuCommandsForDiagnostics,
                        snapshot = pomodoro.PomodoroSnapshotForDiagnostics,
                        runState = new
                        {
                            initialPomodoro,
                            initialRunMenuText,
                            initialRunAction,
                            runningPomodoro,
                            runningMenuText,
                            runningAction,
                            pausedPomodoro,
                            pausedMenuText,
                            pausedAction
                        }
                    }
                });
                break;
            case 4:
                _diagnosticWindows.OfType<TaskbarCountHostForm>().Single().PerformHideCommandForDiagnostics();
                _log.Write("control-smoke.window-hidden", _windows.Snapshot());
                _windows.ShowAll();
                break;
            case 5:
                foreach (var window in _diagnosticWindows.ToArray()) window.PerformCloseCommandForDiagnostics();
                _log.Write("control-smoke.window-closed", _windows.Snapshot());
                break;
            default:
                _controlSmokeTimer?.Stop();
                _tray.PerformExitCommandForDiagnostics();
                break;
        }
    }

    private void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        _log.Write("application.exit-requested", _windows.Snapshot());
        _controlSmokeTimer?.Stop();
        _tray.Dispose();
        _windows.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _controlSmokeTimer?.Dispose();
            _tray.Dispose();
            _windows.Dispose();
        }
        base.Dispose(disposing);
    }
}
