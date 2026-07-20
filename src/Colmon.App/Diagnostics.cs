using System.Text.Json;

namespace Colmon;

internal sealed class JsonLog(string path) : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer = new(path, append: true) { AutoFlush = true };

    public void Write(string name, object data)
    {
        var line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, name, data });
        lock (_gate) _writer.WriteLine(line);
    }

    public void Dispose() => _writer.Dispose();
}

internal static class TaskbarProbe
{
    public static object Capture()
    {
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        var notify = taskbar == 0 ? 0 : NativeMethods.FindWindowEx(taskbar, 0, "TrayNotifyWnd", null);
        var start = taskbar == 0 ? 0 : NativeMethods.FindWindowEx(taskbar, 0, "Start", null);
        return new
        {
            timestamp = DateTimeOffset.Now,
            taskbar = Window(taskbar),
            notificationArea = Window(notify),
            startButton = Window(start),
            os = Environment.OSVersion.VersionString
        };
    }

    public static object Window(nint handle) => new
    {
        handle = $"0x{handle:X}",
        found = handle != 0,
        rect = handle == 0 ? (NativeRect?)null : NativeMethods.Rect(handle),
        dpi = handle == 0 ? 0 : NativeMethods.GetDpiForWindow(handle)
    };
}
