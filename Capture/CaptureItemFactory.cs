using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using WinRT;

namespace BareRecord.Capture;

internal static class CaptureItemFactory
{
    private static readonly Guid IID_IGraphicsCaptureItem =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static readonly Guid IID_IGraphicsCaptureItemInterop =
        new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    /// <summary>
    /// Acquires <c>IGraphicsCaptureItemInterop</c> directly from
    /// <c>RoGetActivationFactory</c>. This bypasses CsWinRT's internal
    /// activation-factory helpers, which aren't part of the public surface
    /// in our SDK.NET version.
    /// </summary>
    private static IntPtr GetInteropFactory()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, (uint)className.Length, out var hstring);
        try
        {
            int hr = RoGetActivationFactory(hstring, in IID_IGraphicsCaptureItemInterop, out var factory);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (factory == IntPtr.Zero)
                throw new InvalidOperationException("RoGetActivationFactory returned null for IGraphicsCaptureItemInterop.");
            return factory;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    public static GraphicsCaptureItem ForPrimaryMonitor()
    {
        var hmon = MonitorEnumerator.GetPrimaryMonitor();
        if (hmon == IntPtr.Zero)
            throw new InvalidOperationException("Could not locate the primary monitor.");
        return CreateForMonitor(hmon);
    }

    /// <summary>
    /// Build a capture item from an arbitrary <c>HMONITOR</c>. Caller is
    /// responsible for ensuring the handle came from EnumDisplayMonitors or
    /// MonitorFromPoint — a stale/invalid handle yields an HRESULT failure.
    /// </summary>
    public static GraphicsCaptureItem ForMonitor(IntPtr hmonitor)
    {
        if (hmonitor == IntPtr.Zero)
            throw new ArgumentException("Monitor handle is null.", nameof(hmonitor));
        return CreateForMonitor(hmonitor);
    }

    /// <summary>
    /// IGraphicsCaptureItemInterop : IUnknown — vtable[4] = CreateForMonitor.
    /// </summary>
    private static unsafe GraphicsCaptureItem CreateForMonitor(IntPtr hmonitor)
    {
        IntPtr factory = GetInteropFactory();
        try
        {
            var vtbl = *(void***)factory;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[4];

            IntPtr itemPtr;
            Guid iid = IID_IGraphicsCaptureItem;
            int hr = fn(factory, hmonitor, &iid, &itemPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (itemPtr == IntPtr.Zero)
                throw new InvalidOperationException("CreateForMonitor returned a null item.");

            try { return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    public static async Task<GraphicsCaptureItem?> PickWindowAsync(IntPtr ownerHwnd)
    {
        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
        return await picker.PickSingleItemAsync();
    }
}
