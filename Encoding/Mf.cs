using System;
using System.Runtime.InteropServices;

namespace BareRecord.Encoding;

/// <summary>
/// Minimal Media Foundation interop. Functions and GUID constants are P/Invoked
/// directly; COM methods are invoked via vtable indices using function pointers,
/// which avoids hand-declaring 80+ unused method signatures across IMFAttributes,
/// IMFMediaType, IMFSample, IMFMediaBuffer and IMFSinkWriter.
///
/// Vtable indices below are *absolute* (so IUnknown::QI/AddRef/Release occupy
/// 0/1/2). For derived interfaces the IMFAttributes block runs from 3 to 32.
/// </summary>
internal static unsafe class Mf
{
    public const uint MF_VERSION = 0x00020070;
    public const uint MFSTARTUP_FULL = 0;

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateAttributes(out IntPtr attrs, uint initial);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateMediaType(out IntPtr type);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int MFCreateSample(out IntPtr sample);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url,
        IntPtr byteStream,
        IntPtr attributes,
        out IntPtr writer);

    // ── major types ─────────────────────────────────────────────────────────
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");

    // ── video subtypes ──────────────────────────────────────────────────────
    public static readonly Guid MFVideoFormat_H264  = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_NV12  = new("3231564E-0000-0010-8000-00AA00389B71");

    // ── audio subtypes ──────────────────────────────────────────────────────
    public static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");

    // ── media-type attribute keys ───────────────────────────────────────────
    public static readonly Guid MF_MT_MAJOR_TYPE          = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    public static readonly Guid MF_MT_SUBTYPE             = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    public static readonly Guid MF_MT_AVG_BITRATE         = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    public static readonly Guid MF_MT_FRAME_SIZE          = new("1652C33D-D6B2-4012-B834-72030849A37D");
    public static readonly Guid MF_MT_FRAME_RATE          = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO  = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
    public static readonly Guid MF_MT_INTERLACE_MODE      = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    public static readonly Guid MF_MT_DEFAULT_STRIDE      = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
    public static readonly Guid MF_MT_VIDEO_PROFILE       = new("AD76A80B-2D5C-4E0B-B375-64E520137036");

    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS         = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND   = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE      = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
    public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
    public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT      = new("322DE230-9EEB-43BD-AB7A-FF412251541D");

    // ── sink writer attributes ──────────────────────────────────────────────
    public static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING       = new("08B845D8-2B74-4AFE-9D53-BE16D2D5AE4F");
    public static readonly Guid MF_LOW_LATENCY                          = new("9C27891A-ED7A-40e1-88E8-B22727A024EE");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("A634A91C-822B-41B9-A494-4DE4643612B0");

    public const int MFVideoInterlace_Progressive = 2;
    public const int eAVEncH264VProfile_Main      = 77;
    public const int eAVEncH264VProfile_High      = 100;

    // ──────────────────────────────────────────────────────────────────────
    //  IMFAttributes — vtable[21]=SetUINT32, [22]=SetUINT64, [24]=SetGUID
    // ──────────────────────────────────────────────────────────────────────

    public static int Attr_SetUINT32(IntPtr attrs, in Guid key, uint value)
    {
        fixed (Guid* pk = &key)
        {
            var vtbl = *(void***)attrs;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)vtbl[21];
            return fn(attrs, pk, value);
        }
    }

    public static int Attr_SetUINT64(IntPtr attrs, in Guid key, ulong value)
    {
        fixed (Guid* pk = &key)
        {
            var vtbl = *(void***)attrs;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)vtbl[22];
            return fn(attrs, pk, value);
        }
    }

    public static int Attr_SetGUID(IntPtr attrs, in Guid key, in Guid value)
    {
        fixed (Guid* pk = &key)
        fixed (Guid* pv = &value)
        {
            var vtbl = *(void***)attrs;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)vtbl[24];
            return fn(attrs, pk, pv);
        }
    }

    public static int Attr_SetSize(IntPtr attrs, in Guid key, int width, int height)
        => Attr_SetUINT64(attrs, in key, ((ulong)(uint)width << 32) | (uint)height);

    public static int Attr_SetRatio(IntPtr attrs, in Guid key, int numerator, int denominator)
        => Attr_SetUINT64(attrs, in key, ((ulong)(uint)numerator << 32) | (uint)denominator);

    // ──────────────────────────────────────────────────────────────────────
    //  IMFSample — adds 14 methods after the IMFAttributes block (33..46)
    // ──────────────────────────────────────────────────────────────────────

    public static int Sample_SetSampleTime(IntPtr sample, long hns)
    {
        var vtbl = *(void***)sample;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)vtbl[36];
        return fn(sample, hns);
    }

    public static int Sample_SetSampleDuration(IntPtr sample, long hns)
    {
        var vtbl = *(void***)sample;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)vtbl[38];
        return fn(sample, hns);
    }

    public static int Sample_AddBuffer(IntPtr sample, IntPtr buffer)
    {
        var vtbl = *(void***)sample;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtbl[42];
        return fn(sample, buffer);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  IMFMediaBuffer — vtable[3]=Lock [4]=Unlock [6]=SetCurrentLength
    // ──────────────────────────────────────────────────────────────────────

    public static int Buffer_Lock(IntPtr buffer, out IntPtr ptr, out uint maxLength, out uint currentLength)
    {
        var vtbl = *(void***)buffer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, int>)vtbl[3];
        IntPtr p; uint maxL, curL;
        int hr = fn(buffer, &p, &maxL, &curL);
        ptr = p; maxLength = maxL; currentLength = curL;
        return hr;
    }

    public static int Buffer_Unlock(IntPtr buffer)
    {
        var vtbl = *(void***)buffer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[4];
        return fn(buffer);
    }

    public static int Buffer_SetCurrentLength(IntPtr buffer, uint len)
    {
        var vtbl = *(void***)buffer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vtbl[6];
        return fn(buffer, len);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  IMFSinkWriter — vtable[3]=AddStream [4]=SetInputMediaType
    //                 [5]=BeginWriting [6]=WriteSample
    //                 [10]=Flush [11]=Finalize
    // ──────────────────────────────────────────────────────────────────────

    public static int Writer_AddStream(IntPtr writer, IntPtr targetMediaType, out int streamIndex)
    {
        var vtbl = *(void***)writer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, int>)vtbl[3];
        int idx;
        int hr = fn(writer, targetMediaType, &idx);
        streamIndex = idx;
        return hr;
    }

    public static int Writer_SetInputMediaType(IntPtr writer, int streamIndex, IntPtr inputMediaType, IntPtr encoderParams)
    {
        var vtbl = *(void***)writer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, int>)vtbl[4];
        return fn(writer, streamIndex, inputMediaType, encoderParams);
    }

    public static int Writer_BeginWriting(IntPtr writer)
    {
        var vtbl = *(void***)writer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[5];
        return fn(writer);
    }

    public static int Writer_WriteSample(IntPtr writer, int streamIndex, IntPtr sample)
    {
        var vtbl = *(void***)writer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)vtbl[6];
        return fn(writer, streamIndex, sample);
    }

    public static int Writer_Flush(IntPtr writer, int streamIndex)
    {
        var vtbl = *(void***)writer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)vtbl[10];
        return fn(writer, streamIndex);
    }

    public static int Writer_Finalize(IntPtr writer)
    {
        var vtbl = *(void***)writer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[11];
        return fn(writer);
    }

    // ──────────────────────────────────────────────────────────────────────
    public static void CheckHr(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public static void Release(ref IntPtr p)
    {
        if (p != IntPtr.Zero)
        {
            Marshal.Release(p);
            p = IntPtr.Zero;
        }
    }
}
