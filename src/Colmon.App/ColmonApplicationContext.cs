namespace Colmon;

internal sealed class ColmonApplicationContext : ApplicationContext
{
    private readonly JsonLog _log;
    private readonly TaskbarWindowManager _windows;
    private readonly NotifyIconController _tray;
    private System.Windows.Forms.Timer? _controlSmokeTimer;
    private int _controlSmokeStep;
    private bool _exiting;

    public ColmonApplicationContext(JsonLog log)
    {
        _log = log;
        _windows = new TaskbarWindowManager(log);
        _tray = new NotifyIconController(_windows, log, RequestExit);
    }

    public void RegisterTaskbarWindow(Form window) => _windows.Register(window);

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
