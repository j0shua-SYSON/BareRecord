using System;
using System.Runtime.InteropServices;

namespace BareRecord.Capture;

internal static class MonitorEnumerator
{
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x, y;
        public POINT(int x, int y) { this.x = x; this.y = y; }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    public static IntPtr GetPrimaryMonitor() =>
        MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY);
}
