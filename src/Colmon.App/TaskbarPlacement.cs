using System.Diagnostics;

namespace Colmon;

internal sealed record OccupiedTaskbarWindow(nint Handle, uint ProcessId, string ProcessName, NativeRect Rectangle);

internal static class TaskbarPlacement
{
    public static IReadOnlyList<OccupiedTaskbarWindow> ExternalWindows(
        nint taskbar,
        nint ownWindow,
        NativeRect taskbarRectangle)
    {
        NativeMethods.GetWindowThreadProcessId(taskbar, out var explorerProcessId);
        var windows = new List<OccupiedTaskbarWindow>();
        for (nint child = 0; ;)
        {
            child = NativeMethods.FindWindowEx(taskbar, child, null, null);
            if (child == 0) break;
            if (child == ownWindow || !NativeMethods.IsWindowVisible(child)) continue;

            NativeMethods.GetWindowThreadProcessId(child, out var processId);
            if (processId == 0 || processId == explorerProcessId || processId == Environment.ProcessId) continue;
            if (!NativeMethods.GetWindowRect(child, out var rectangle) || !Intersects(rectangle, taskbarRectangle)) continue;

            windows.Add(new OccupiedTaskbarWindow(child, processId, ProcessName(processId), rectangle));
        }
        return windows;
    }

    public static int FindAvailableX(
        int taskbarLeft,
        int taskbarWidth,
        int notificationLeft,
        int windowWidth,
        int gap,
        int offsetX,
        IEnumerable<NativeRect> occupiedAbsoluteRectangles)
    {
        var occupied = occupiedAbsoluteRectangles
            .Select(rectangle => new NativeRect(
                rectangle.Left - taskbarLeft,
                rectangle.Top,
                rectangle.Right - taskbarLeft,
                rectangle.Bottom))
            .Where(rectangle => rectangle.Width > 0)
            .ToArray();
        var right = notificationLeft - taskbarLeft - gap + offsetX;

        for (var attempts = 0; attempts <= occupied.Length; attempts++)
        {
            var left = right - windowWidth;
            var conflict = occupied
                .Where(rectangle => left < rectangle.Right && right > rectangle.Left)
                .OrderByDescending(rectangle => rectangle.Left)
                .FirstOrDefault();
            if (conflict.Width <= 0)
                return Math.Clamp(left, 0, Math.Max(0, taskbarWidth - windowWidth));
            right = conflict.Left - gap;
        }

        return Math.Clamp(right - windowWidth, 0, Math.Max(0, taskbarWidth - windowWidth));
    }

    public static bool Overlaps(int taskbarLeft, int x, int width, IEnumerable<NativeRect> occupiedAbsoluteRectangles) =>
        occupiedAbsoluteRectangles.Any(rectangle =>
            x < rectangle.Right - taskbarLeft && x + width > rectangle.Left - taskbarLeft);

    private static bool Intersects(NativeRect left, NativeRect right) =>
        left.Left < right.Right && left.Right > right.Left && left.Top < right.Bottom && left.Bottom > right.Top;

    private static string ProcessName(uint processId)
    {
        try { return Process.GetProcessById((int)processId).ProcessName; }
        catch { return "unknown"; }
    }
}
