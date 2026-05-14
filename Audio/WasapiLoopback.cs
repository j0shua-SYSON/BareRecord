using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace BareRecord.Audio;

internal enum WasapiCaptureMode
{
    /// <summary>Default render endpoint, loopback flag (= system audio).</summary>
    SystemAudio,
    /// <summary>Default capture endpoint (= microphone / line-in).</summary>
    Microphone,
}

/// <summary>
/// WASAPI capture, normalised to 16-bit / stereo PCM at the device's native
/// sample rate. Two modes: loopback on the render endpoint for system audio,
/// or shared-mode capture on the capture endpoint for mic. We deliberately
/// don't resample: AAC accepts whatever rate the device runs at, and dropping
/// the resampler avoids pulling in a second copy of MFTransform / NAudio.
///
/// Drives a dedicated MTA thread that runs the WASAPI pump in a tight loop
/// and surfaces converted PCM buffers via <see cref="DataAvailable"/>.
/// </summary>
internal sealed partial class WasapiLoopback : IDisposable
{
    private const uint COINIT_MULTITHREADED = 0x0;
    private const uint CLSCTX_ALL           = 0x17;

    private const int eRender   = 0;
    private const int eCapture  = 1;
    private const int eConsole  = 0;

    private const uint AUDCLNT_SHAREMODE_SHARED       = 0;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK   = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_NOPERSIST  = 0x00080000;

    private const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    private const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator  = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioClient         = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient  = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00AA00389B71");

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr unkOuter, uint clsCtx, in Guid iid, out IntPtr ppv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr p);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint   nSamplesPerSec;
        public uint   nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort       wValidBitsPerSample;
        public uint         dwChannelMask;
        public Guid         SubFormat;
    }

    public event Action<byte[], int>? DataAvailable;

    public int SampleRate { get; private set; } = 48000;
    public int Channels    => 2;
    public int BitsPerSample => 16;

    /// <summary>
    /// Linear peak amplitude (0..1) of the most recent packet. Updated from
    /// the capture pump thread and read from the UI thread; on x64 a 32-bit
    /// float read/write is atomic, and Volatile.Read/Write enforces ordering.
    /// Read it as often as you like — the value reflects only the latest packet,
    /// so the UI can apply its own decay / smoothing.
    /// </summary>
    public float Peak => Volatile.Read(ref _peak);
    private float _peak;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _startError;
    private bool _disposed;
    private readonly WasapiCaptureMode _mode;

    public WasapiLoopback(WasapiCaptureMode mode = WasapiCaptureMode.SystemAudio)
    {
        _mode = mode;
    }

    private int   _srcChannels;
    private int   _srcBytesPerSample;
    private bool  _srcIsFloat;
    private byte[] _outBuffer = Array.Empty<byte>();

    public void Start()
    {
        if (_thread != null) return;
        _cts = new CancellationTokenSource();
        _ready.Reset();
        _startError = null;

        _thread = new Thread(Run) { IsBackground = true, Name = "BareRecord.Wasapi" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        bool ready = _ready.Wait(TimeSpan.FromSeconds(5));
        if (_startError != null) throw _startError;
        if (!ready) throw new TimeoutException("Audio device did not become ready within 5 seconds.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _thread?.Join();
        _thread = null;
        _cts?.Dispose();
        _cts = null;
    }

    private unsafe void Run()
    {
        IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero,
               client = IntPtr.Zero, capture = IntPtr.Zero;
        IntPtr pwfx = IntPtr.Zero;
        bool started = false, comInit = false;

        try
        {
            // S_OK / S_FALSE: we either initialised COM or it was already up
            // in a compatible (MTA) mode — either way we own a balance call.
            // RPC_E_CHANGED_MODE: another part of the runtime got here first
            // and put the thread in STA. Bail rather than fighting the host.
            int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            if (hr == unchecked((int)0x80010106))
                throw new InvalidOperationException("Audio thread is in STA but WASAPI requires MTA.");
            if (hr < 0)
                throw Marshal.GetExceptionForHR(hr)!;
            comInit = true;

            CheckHr(CoCreateInstance(in CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_ALL, in IID_IMMDeviceEnumerator, out enumerator));

            // IMMDeviceEnumerator::GetDefaultAudioEndpoint  (vtable[4])
            {
                var vtbl = *(void***)enumerator;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)vtbl[4];
                IntPtr d;
                int dataFlow = _mode == WasapiCaptureMode.Microphone ? eCapture : eRender;
                CheckHr(fn(enumerator, dataFlow, eConsole, &d));
                device = d;
            }

            // IMMDevice::Activate  (vtable[3])
            {
                var vtbl = *(void***)device;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)vtbl[3];
                IntPtr c;
                Guid iid = IID_IAudioClient;
                CheckHr(fn(device, &iid, CLSCTX_ALL, IntPtr.Zero, &c));
                client = c;
            }

            // IAudioClient::GetMixFormat  (vtable[8])
            {
                var vtbl = *(void***)client;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[8];
                IntPtr p;
                CheckHr(fn(client, &p));
                pwfx = p;
            }

            ParseFormat(pwfx);

            // IAudioClient::Initialize  (vtable[3])
            {
                const long hns500ms = 5_000_000;
                var vtbl = *(void***)client;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, long, long, IntPtr, IntPtr, int>)vtbl[3];
                uint flags = _mode == WasapiCaptureMode.Microphone
                    ? AUDCLNT_STREAMFLAGS_NOPERSIST
                    : AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_NOPERSIST;
                CheckHr(fn(client,
                    AUDCLNT_SHAREMODE_SHARED,
                    flags,
                    hns500ms, 0, pwfx, IntPtr.Zero));
            }

            // IAudioClient::GetService  (vtable[14])
            {
                var vtbl = *(void***)client;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[14];
                Guid iid = IID_IAudioCaptureClient;
                IntPtr cap;
                CheckHr(fn(client, &iid, &cap));
                capture = cap;
            }

            // IAudioClient::Start  (vtable[10])
            {
                var vtbl = *(void***)client;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[10];
                CheckHr(fn(client));
                started = true;
            }

            _ready.Set();

            var ct = _cts!.Token;
            while (!ct.IsCancellationRequested)
            {
                PumpOnce(capture);
                Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            _startError = ex;
            _ready.Set();
        }
        finally
        {
            try
            {
                if (started && client != IntPtr.Zero)
                {
                    var vtbl = *(void***)client;
                    var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[11];
                    fn(client);
                }
            }
            catch { }

            if (pwfx != IntPtr.Zero) CoTaskMemFree(pwfx);
            if (capture != IntPtr.Zero) Marshal.Release(capture);
            if (client != IntPtr.Zero) Marshal.Release(client);
            if (device != IntPtr.Zero) Marshal.Release(device);
            if (enumerator != IntPtr.Zero) Marshal.Release(enumerator);
            if (comInit) CoUninitialize();
        }
    }

    /// <summary>Pull every available packet from the capture client.</summary>
    private unsafe void PumpOnce(IntPtr capture)
    {
        var vtbl = *(void***)capture;
        var getNextPacketSize = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)vtbl[5];
        var getBuffer         = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, ulong*, ulong*, int>)vtbl[3];
        var releaseBuffer     = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vtbl[4];

        while (true)
        {
            uint packetFrames;
            int hr = getNextPacketSize(capture, &packetFrames);
            if (hr < 0 || packetFrames == 0) return;

            IntPtr data;
            uint frames; uint flags;
            ulong devPos; ulong qpcPos;
            hr = getBuffer(capture, &data, &frames, &flags, &devPos, &qpcPos);
            if (hr < 0) return;

            try
            {
                if (frames > 0)
                {
                    int outBytes = (int)frames * 4; // 16-bit stereo
                    if (_outBuffer.Length < outBytes)
                        _outBuffer = new byte[outBytes];

                    // Silence flag: emit zeros so AAC keeps a continuous timeline.
                    if ((flags & 0x2) != 0) // AUDCLNT_BUFFERFLAGS_SILENT
                    {
                        Array.Clear(_outBuffer, 0, outBytes);
                        Volatile.Write(ref _peak, 0f);
                    }
                    else
                    {
                        ConvertToStereoInt16((byte*)data, (int)frames, _outBuffer);
                        Volatile.Write(ref _peak, ComputePeak(_outBuffer, outBytes));
                    }

                    DataAvailable?.Invoke(_outBuffer, outBytes);
                }
            }
            finally
            {
                releaseBuffer(capture, frames);
            }
        }
    }

    private unsafe void ConvertToStereoInt16(byte* src, int frames, byte[] dst)
    {
        int srcStride = _srcChannels * _srcBytesPerSample;
        fixed (byte* dstPtr = dst)
        {
            short* d = (short*)dstPtr;

            if (_srcIsFloat)
            {
                for (int i = 0; i < frames; i++)
                {
                    float* s = (float*)(src + (long)i * srcStride);
                    float l = s[0];
                    float r = _srcChannels >= 2 ? s[1] : l;
                    d[i * 2 + 0] = ToInt16(l);
                    d[i * 2 + 1] = ToInt16(r);
                }
            }
            else if (_srcBytesPerSample == 2)
            {
                for (int i = 0; i < frames; i++)
                {
                    short* s = (short*)(src + (long)i * srcStride);
                    d[i * 2 + 0] = s[0];
                    d[i * 2 + 1] = _srcChannels >= 2 ? s[1] : s[0];
                }
            }
            else if (_srcBytesPerSample == 4)
            {
                // 32-bit PCM: shift down to 16-bit.
                for (int i = 0; i < frames; i++)
                {
                    int* s = (int*)(src + (long)i * srcStride);
                    d[i * 2 + 0] = (short)(s[0] >> 16);
                    d[i * 2 + 1] = _srcChannels >= 2 ? (short)(s[1] >> 16) : (short)(s[0] >> 16);
                }
            }
            else
            {
                // Unknown format — emit silence rather than blow up.
                for (int i = 0; i < frames * 2; i++) d[i] = 0;
            }
        }
    }

    private static short ToInt16(float v)
    {
        int s = (int)(v * 32767f);
        if (s >  32767) s =  32767;
        if (s < -32768) s = -32768;
        return (short)s;
    }

    private static unsafe float ComputePeak(byte[] pcm16, int byteCount)
    {
        int samples = byteCount / 2;
        if (samples <= 0) return 0f;
        int max = 0;
        fixed (byte* p = pcm16)
        {
            short* s = (short*)p;
            for (int i = 0; i < samples; i++)
            {
                int v = s[i];
                if (v < 0) v = -v;
                if (v > max) max = v;
            }
        }
        return max / 32768f;
    }

    private unsafe void ParseFormat(IntPtr pwfx)
    {
        var fmt = *(WAVEFORMATEX*)pwfx;
        SampleRate         = (int)fmt.nSamplesPerSec;
        _srcChannels       = fmt.nChannels;
        _srcBytesPerSample = fmt.wBitsPerSample / 8;
        _srcIsFloat        = false;

        if (fmt.wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
        {
            _srcIsFloat = true;
        }
        else if (fmt.wFormatTag == WAVE_FORMAT_EXTENSIBLE && fmt.cbSize >= 22)
        {
            var ext = *(WAVEFORMATEXTENSIBLE*)pwfx;
            if (ext.SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT) _srcIsFloat = true;
        }
    }

    private static void CheckHr(int hr)
    {
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { }
        _ready.Dispose();
    }
}
