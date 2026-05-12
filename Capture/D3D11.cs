using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace BareRecord.Capture;

/// <summary>
/// Hand-rolled D3D11 interop. We need a tiny slice of the API surface
/// (CreateDevice, CreateTexture2D, CopyResource, Map, Unmap), so calling
/// the vtable directly is a few hundred bytes lighter than pulling in the
/// full Vortice.Direct3D11 binding.
///
/// Vtable absolute indices (counting QI/AddRef/Release at 0/1/2):
///   ID3D11Device::CreateTexture2D       = 5
///   ID3D11Device::GetImmediateContext   = 40
///   ID3D11DeviceContext::Map            = 14
///   ID3D11DeviceContext::Unmap          = 15
///   ID3D11DeviceContext::CopyResource   = 47
///   ID3D11Multithread::SetMultithreadProtected = 5
///   IDirect3DDxgiInterfaceAccess::GetInterface = 3
/// </summary>
internal static unsafe class D3D11
{
    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const int D3D_DRIVER_TYPE_WARP     = 5;

    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const uint D3D11_SDK_VERSION                = 7;

    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;

    public const uint D3D11_USAGE_STAGING        = 3;
    public const uint D3D11_CPU_ACCESS_READ      = 0x20000;
    public const uint D3D11_MAP_READ             = 1;

    public const int D3D_FEATURE_LEVEL_10_0 = 0xa000;
    public const int D3D_FEATURE_LEVEL_10_1 = 0xa100;
    public const int D3D_FEATURE_LEVEL_11_0 = 0xb000;
    public const int D3D_FEATURE_LEVEL_11_1 = 0xb100;

    private static readonly Guid IID_IDXGIDevice                  = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IID_ID3D11Texture2D              = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid IID_ID3D11Multithread            = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_SAMPLE_DESC
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint   RowPitch;
        public uint   DepthPitch;
    }

    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr software,
        uint flags,
        int[]? pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out IntPtr ppDevice,
        out int featureLevel,
        out IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>
    /// Creates a hardware-backed (or WARP-fallback) device + immediate context,
    /// enables multithread protection (WGC fires frames on its own thread, so
    /// the encoder MFT may touch the context concurrently with our consumer),
    /// and projects the device into a WinRT IDirect3DDevice for handing to
    /// Direct3D11CaptureFramePool.
    /// </summary>
    public static (IntPtr device, IntPtr context, IDirect3DDevice winrtDevice) CreateDevice()
    {
        int[] levels = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
        const uint flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;

        int hr = D3D11CreateDevice(
            IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, flags,
            levels, (uint)levels.Length, D3D11_SDK_VERSION,
            out var device, out _, out var context);

        if (hr < 0 || device == IntPtr.Zero)
        {
            hr = D3D11CreateDevice(
                IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, flags,
                levels, (uint)levels.Length, D3D11_SDK_VERSION,
                out device, out _, out context);
        }
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        EnableMultithreadProtection(device);

        // Project to a WinRT IDirect3DDevice for Windows.Graphics.Capture.
        Guid iidDxgi = IID_IDXGIDevice;
        int qi = Marshal.QueryInterface(device, in iidDxgi, out IntPtr dxgi);
        if (qi < 0)
        {
            Marshal.Release(context); Marshal.Release(device);
            Marshal.ThrowExceptionForHR(qi);
        }

        IntPtr inspectable;
        try
        {
            int wr = CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable);
            if (wr < 0)
            {
                Marshal.Release(context); Marshal.Release(device);
                Marshal.ThrowExceptionForHR(wr);
            }
        }
        finally
        {
            Marshal.Release(dxgi);
        }

        IDirect3DDevice winrt;
        try { winrt = MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }

        return (device, context, winrt);
    }

    private static void EnableMultithreadProtection(IntPtr device)
    {
        Guid iid = IID_ID3D11Multithread;
        if (Marshal.QueryInterface(device, in iid, out IntPtr mt) < 0) return;
        try
        {
            var vtbl = *(void***)mt;
            // ID3D11Multithread::SetMultithreadProtected: BOOL SetMultithreadProtected(BOOL bMTProtect).
            // Returns the previous value; we ignore it.
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)vtbl[5];
            fn(mt, 1);
        }
        finally
        {
            Marshal.Release(mt);
        }
    }

    public static IntPtr Device_CreateTexture2D(IntPtr device, in D3D11_TEXTURE2D_DESC desc)
    {
        var vtbl = *(void***)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)vtbl[5];
        IntPtr texture;
        int hr;
        fixed (D3D11_TEXTURE2D_DESC* pDesc = &desc)
        {
            hr = fn(device, pDesc, IntPtr.Zero, &texture);
        }
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return texture;
    }

    public static int Context_Map(IntPtr context, IntPtr resource, uint subresource, uint mapType, uint mapFlags, out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        var vtbl = *(void***)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)vtbl[14];
        D3D11_MAPPED_SUBRESOURCE m;
        int hr = fn(context, resource, subresource, mapType, mapFlags, &m);
        mapped = m;
        return hr;
    }

    public static void Context_Unmap(IntPtr context, IntPtr resource, uint subresource)
    {
        var vtbl = *(void***)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)vtbl[15];
        fn(context, resource, subresource);
    }

    public static void Context_CopyResource(IntPtr context, IntPtr dst, IntPtr src)
    {
        var vtbl = *(void***)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)vtbl[47];
        fn(context, dst, src);
    }

    /// <summary>
    /// Get the raw <c>ID3D11Texture2D</c> pointer behind a captured surface.
    /// We avoid the <c>(IDirect3DDxgiInterfaceAccess)(object)surface</c>
    /// cast because it goes through <c>IDynamicInterfaceCastable</c>, which
    /// has historically been fragile under aggressive trimming and AOT.
    /// Instead we get the raw IInspectable, QI for IDxgiInterfaceAccess,
    /// and call vtable[3] directly.
    /// </summary>
    public static IntPtr GetTexture2DPtr(IDirect3DSurface surface)
    {
        IntPtr inspectable = MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        if (inspectable == IntPtr.Zero)
            throw new InvalidOperationException("Capture surface has no native pointer.");

        IntPtr access = IntPtr.Zero;
        try
        {
            Guid iidAccess = IID_IDirect3DDxgiInterfaceAccess;
            int qi = Marshal.QueryInterface(inspectable, in iidAccess, out access);
            if (qi < 0) Marshal.ThrowExceptionForHR(qi);

            var vtbl = *(void***)access;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
            Guid iidTex = IID_ID3D11Texture2D;
            IntPtr tex;
            int hr = fn(access, &iidTex, &tex);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return tex;
        }
        finally
        {
            if (access != IntPtr.Zero) Marshal.Release(access);
            Marshal.Release(inspectable);
        }
    }

    public static void Release(ref IntPtr p)
    {
        if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; }
    }
}
