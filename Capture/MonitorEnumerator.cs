using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BareRecord.Capture;

internal readonly struct MonitorInfo
{
    public readonly IntPtr Handle;
    public readonly string DeviceName;
    public readonly int Left, Top, Right, Bottom;
    public readonly bool IsPrimary;

    public MonitorInfo(IntPtr handle, string deviceName, int l, int t, int r, int b, bool primary)
    {
        Handle = handle;
        DeviceName = deviceName;
        Left = l; Top = t; Right = r; Bottom = b;
        IsPrimary = primary;
    }

    public int Width  => Right - Left;
    public int Height => Bottom - Top;

    /// <summary>Friendly label for the UI: "Primary  (1920×1080)" / "Display 2  (3840×2160)".</summary>
    public string FriendlyName(int index1Based)
    {
        var role = IsPrimary ? "Primary" : ("Display " + index1Based);
        return $"{role}  ({Width}×{Height})";
    }
}

/// <summary>
/// EnumDisplayMonitors-based monitor list. Returned in EnumDisplayMonitors
/// order; the primary monitor is flagged via the MONITORINFOF_PRIMARY bit.
/// </summary>
internal static class MonitorEnumerator
{
    private const uint MONITORINFOF_PRIMARY     = 0x1;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x, y;
        public POINT(int x, int y) { this.x = x; this.y = y; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc lpfn, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFOEX info);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    /// <summary>Always returns at least one entry — primary monitor as fallback.</summary>
    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>(4);
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(h, ref mi))
                {
                    list.Add(new MonitorInfo(
                        h, mi.szDevice ?? string.Empty,
                        mi.rcMonitor.L, mi.rcMonitor.T, mi.rcMonitor.R, mi.rcMonitor.B,
                        (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { /* fall through to primary fallback below */ }

        if (list.Count == 0)
        {
            var hmon = GetPrimaryMonitor();
            if (hmon != IntPtr.Zero) list.Add(new MonitorInfo(hmon, "", 0, 0, 0, 0, true));
        }
        return list;
    }

    public static IntPtr GetPrimaryMonitor() =>
        MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY);
}
