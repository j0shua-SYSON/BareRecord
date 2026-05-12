using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BareRecord.Capture;

/// <summary>
/// Walks every relevant Windows display-enumeration API and writes a plain
/// text dump to <c>%TEMP%\BareRecord-displays.log</c>. The intent is to make
/// "what does Windows think you have plugged in" inspectable from outside
/// the app — when a user reports "I have N monitors but BareRecord only sees
/// M", this is the first thing to check.
/// </summary>
internal static unsafe class DisplayDiagnostics
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int l, t, r, b; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC_RAW
    {
        public fixed char DeviceName[32];
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DC_RATIONAL { public uint Num, Den; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DC_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DC_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public int outputTechnology;
        public int rotation;
        public int scaling;
        public DC_RATIONAL refreshRate;
        public int scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DC_PATH_INFO
    {
        public DC_PATH_SOURCE_INFO sourceInfo;
        public DC_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DC_MODE_PADDING { }

    [StructLayout(LayoutKind.Sequential)]
    private struct DC_MODE_INFO
    {
        public int infoType;
        public uint id;
        public LUID adapterId;
        public DC_MODE_PADDING modeData;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc lpfn, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint i, ref DISPLAY_DEVICE d, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathArrayElements,
        [Out] DC_PATH_INFO[] pathArray,
        ref uint modeInfoArrayElements,
        [Out] DC_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid iid, out IntPtr factory);

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private const uint QDC_ALL_PATHS = 0x1;
    private const uint DC_PATH_ACTIVE = 0x1;

    /// <summary>Returns the path the diagnostics were written to, or null on failure.</summary>
    public static string? WriteToTempLog()
    {
        var sb = new StringBuilder();
        sb.AppendLine("BareRecord display diagnostics");
        sb.AppendLine($"timestamp: {DateTime.Now:O}");
        sb.AppendLine();

        DumpEnumDisplayMonitors(sb);
        DumpEnumDisplayDevices(sb);
        DumpDxgi(sb);
        DumpQueryDisplayConfig(sb);

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "BareRecord-displays.log");
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch
        {
            return null;
        }
    }

    [ThreadStatic] private static StringBuilder? t_sb;
    [ThreadStatic] private static int t_n;

    private static void DumpEnumDisplayMonitors(StringBuilder sb)
    {
        sb.AppendLine("=== EnumDisplayMonitors ===");
        t_sb = sb;
        t_n = 0;
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, EnumDisplayMonitorsProc, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  FAILED: {ex.Message}");
        }
        sb.AppendLine($"  total: {t_n}");
        sb.AppendLine();
        t_sb = null;
    }

    private static bool EnumDisplayMonitorsProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (GetMonitorInfo(hMonitor, ref info))
        {
            t_n++;
            t_sb?.AppendLine($"  [{t_n}] hmon=0x{hMonitor.ToInt64():X} flags=0x{info.dwFlags:X} dev='{info.szDevice}' rect={info.rcMonitor.l},{info.rcMonitor.t}-{info.rcMonitor.r},{info.rcMonitor.b}");
        }
        return true;
    }

    private static void DumpEnumDisplayDevices(StringBuilder sb)
    {
        sb.AppendLine("=== EnumDisplayDevices ===");
        try
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                sb.AppendLine($"  [{i}] '{dd.DeviceName}' state=0x{dd.StateFlags:X} adapter='{dd.DeviceString}'");
                var md = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                for (uint j = 0; EnumDisplayDevices(dd.DeviceName, j, ref md, 0); j++)
                {
                    sb.AppendLine($"      monitor[{j}] '{md.DeviceName}' state=0x{md.StateFlags:X} model='{md.DeviceString}' id='{md.DeviceID}'");
                    md = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                }
                dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  FAILED: {ex.Message}");
        }
        sb.AppendLine();
    }

    private static void DumpDxgi(StringBuilder sb)
    {
        sb.AppendLine("=== DXGI EnumAdapters / EnumOutputs ===");
        IntPtr factory = IntPtr.Zero;
        try
        {
            Guid iid = IID_IDXGIFactory1;
            int hr = CreateDXGIFactory1(in iid, out factory);
            if (hr < 0 || factory == IntPtr.Zero)
            {
                sb.AppendLine($"  CreateDXGIFactory1 hr=0x{hr:X8}");
                return;
            }
            var fvtbl = *(void***)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)fvtbl[12];
            for (uint a = 0; ; a++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, a, &adapter) < 0) break;
                sb.AppendLine($"  adapter[{a}] = 0x{adapter.ToInt64():X}");
                try
                {
                    var avtbl = *(void***)adapter;
                    var enumOutputs = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)avtbl[7];
                    for (uint o = 0; ; o++)
                    {
                        IntPtr output;
                        if (enumOutputs(adapter, o, &output) < 0) break;
                        try
                        {
                            var ovtbl = *(void***)output;
                            var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC_RAW*, int>)ovtbl[7];
                            DXGI_OUTPUT_DESC_RAW desc = default;
                            getDesc(output, &desc);
                            string name = new string(desc.DeviceName);
                            int nul = name.IndexOf('\0');
                            if (nul >= 0) name = name.Substring(0, nul);
                            sb.AppendLine($"    output[{o}] dev='{name}' attached={desc.AttachedToDesktop} hmon=0x{desc.Monitor.ToInt64():X} rect={desc.DesktopCoordinates.l},{desc.DesktopCoordinates.t}-{desc.DesktopCoordinates.r},{desc.DesktopCoordinates.b}");
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  FAILED: {ex.Message}");
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
        sb.AppendLine();
    }

    private static void DumpQueryDisplayConfig(StringBuilder sb)
    {
        sb.AppendLine("=== QueryDisplayConfig (QDC_ALL_PATHS) ===");
        try
        {
            int hr = GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out uint paths, out uint modes);
            if (hr != 0)
            {
                sb.AppendLine($"  GetDisplayConfigBufferSizes hr={hr}");
                return;
            }
            var pathArr = new DC_PATH_INFO[paths];
            var modeArr = new DC_MODE_INFO[modes];
            hr = QueryDisplayConfig(QDC_ALL_PATHS, ref paths, pathArr, ref modes, modeArr, IntPtr.Zero);
            if (hr != 0)
            {
                sb.AppendLine($"  QueryDisplayConfig hr={hr}");
                return;
            }
            int active = 0;
            for (int i = 0; i < paths; i++)
            {
                var p = pathArr[i];
                bool isActive = (p.flags & DC_PATH_ACTIVE) != 0;
                if (isActive) active++;
                sb.AppendLine($"  path[{i}] active={isActive,-5} sourceId={p.sourceInfo.id} targetId={p.targetInfo.id} outputTech={DescribeOutputTech(p.targetInfo.outputTechnology)} avail={p.targetInfo.targetAvailable}");
            }
            sb.AppendLine($"  -> {active} active path(s) of {paths} total");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  FAILED: {ex.Message}");
        }
        sb.AppendLine();
    }

    private static string DescribeOutputTech(int t) => t switch
    {
        unchecked((int)0x80000000) => "INTERNAL",
         0 => "OTHER",
         1 => "HD15(VGA)",
         2 => "S_VIDEO",
         3 => "COMPOSITE",
         4 => "COMPONENT",
         5 => "DVI",
         6 => "HDMI",
         7 => "LVDS",
         8 => "D_JPN",
         9 => "SDI",
        10 => "DISPLAYPORT_EXTERNAL",
        11 => "DISPLAYPORT_EMBEDDED",
        12 => "UDI_EXTERNAL",
        13 => "UDI_EMBEDDED",
        14 => "SDTVDONGLE",
        15 => "MIRACAST",
        16 => "INDIRECT_WIRED",
        17 => "INDIRECT_VIRTUAL",
         _ => $"unk({t})",
    };
}
