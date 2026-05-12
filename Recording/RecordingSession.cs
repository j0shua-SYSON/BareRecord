using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BareRecord.Audio;
using BareRecord.Capture;
using BareRecord.Encoding;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace BareRecord.Recording;

internal sealed partial class RecordingSession : IDisposable
{
    public string OutputPath { get; }

    /// <summary>
    /// Fired (on a non-UI thread) when the captured source has gone away
    /// or the frame pipeline has been failing persistently. Listeners should
    /// trigger <see cref="StopAsync"/> from the UI thread.
    /// </summary>
    public event Action<string>? AbortRequested;

    /// <summary>Threshold after which we give up on a chronically-failing capture.</summary>
    private const int PersistentErrorThreshold = 60;

    private const int VideoFps     = 30;
    private const int VideoBitrate = 8_000_000;
    private static readonly long FrameMinTicks = TimeSpan.TicksPerSecond / VideoFps;
    // Drop frames that arrived less than 75 % of one frame period after the
    // last write. A strict `< FrameMinTicks` would mis-drop on a 60 Hz source
    // because QPC rounding can round 33.33 ms down to FrameMinTicks - 1 ticks.
    private static readonly long FrameSkipBelowTicks = FrameMinTicks - FrameMinTicks / 4;

    /// <summary>AAC encoder accepts these sample rates only (Windows MF AAC encoder).</summary>
    private static readonly int[] AacSupportedRates =
        { 8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000 };

    private readonly GraphicsCaptureItem _item;
    private readonly bool _captureSystem;
    private readonly bool _captureMic;
    private readonly bool _captureCursor;
    private readonly object _gate = new();

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _staging;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _captureSession;
    private WasapiLoopback? _audio;     // system audio (loopback) — timeline driver when present
    private WasapiLoopback? _mic;       // microphone — buffered into _micRing for mixing
    private PcmRingBuffer? _micRing;
    private byte[]? _micMix;            // scratch buffer for mic chunks pulled out of _micRing
    private bool _mixWithMic;
    private bool _micOnly;              // only mic enabled → mic drives the timeline
    // _writer is set on the UI thread but read from the WGC frame thread and
    // the WASAPI audio thread; volatile ensures the publish is observed.
    private volatile MediaSinkWriter? _writer;

    private int _width, _height;
    private TimeSpan? _firstFrameTime;
    private TimeSpan _lastWrittenTimestamp = TimeSpan.MinValue;
    private long _audioBytesAccumulated;
    private long _audioBytesPerSecond;
    private int _consecutiveFrameErrors;
    private bool _abortFired;

    // Pause/resume bookkeeping. Frames and audio packets that arrive while
    // _isPaused == true are dropped, and post-resume video timestamps are
    // shifted back by _totalPausedTicks so the resulting MP4 has continuous
    // timing (no gap, no double-speed segment). Audio stays in sync because
    // _audioBytesAccumulated isn't incremented while paused.
    private volatile bool _isPaused;
    private long _totalPausedTicks;

    private volatile bool _started;
    private volatile bool _stopRequested;
    private volatile bool _disposed;

    public RecordingSession(GraphicsCaptureItem item, string outputPath,
                            bool captureSystemAudio, bool captureMic, bool captureCursor)
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
        OutputPath = outputPath;
        _captureSystem = captureSystemAudio;
        _captureMic    = captureMic;
        _captureCursor = captureCursor;
    }

    public void Start()
    {
        if (_started) throw new InvalidOperationException("Already started.");

        try
        {
            StartCore();
            _started = true;
        }
        catch
        {
            // Anything we managed to allocate before the throw must be torn
            // down — the constructor caller has no other way to release it.
            DisposeCore();
            throw;
        }
    }

    private void StartCore()
    {
        // H.264 needs even dimensions.
        _width  = _item.Size.Width  & ~1;
        _height = _item.Size.Height & ~1;
        if (_width < 2 || _height < 2)
            throw new InvalidOperationException("Capture target is too small.");

        var (device, context, winrtDevice) = D3D11.CreateDevice();
        _device = device;
        _context = context;

        _staging = D3D11.Device_CreateTexture2D(_device, new D3D11.D3D11_TEXTURE2D_DESC
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = D3D11.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new D3D11.DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        });

        // Bring up audio first so we know its native sample rate before
        // the sink writer is configured.
        int audioSampleRate = 0;
        bool willWriteAudio = false;

        if (_captureSystem)
        {
            try
            {
                _audio = new WasapiLoopback(WasapiCaptureMode.SystemAudio);
                _audio.Start();
                if (Array.IndexOf(AacSupportedRates, _audio.SampleRate) >= 0)
                {
                    audioSampleRate = _audio.SampleRate;
                    willWriteAudio = true;
                }
                else
                {
                    _audio.Dispose();
                    _audio = null;
                }
            }
            catch
            {
                try { _audio?.Dispose(); } catch { }
                _audio = null;
            }
        }

        if (_captureMic)
        {
            try
            {
                _mic = new WasapiLoopback(WasapiCaptureMode.Microphone);
                _mic.Start();

                bool canMix  = _audio != null && _mic.SampleRate == _audio.SampleRate;
                bool canSolo = _audio == null && Array.IndexOf(AacSupportedRates, _mic.SampleRate) >= 0;

                if (canMix)
                {
                    // System audio drives the timeline; mic is buffered ~1 s
                    // and dequeued by the system-audio handler at write time.
                    int oneSec = _mic.SampleRate * _mic.Channels * (_mic.BitsPerSample / 8);
                    _micRing = new PcmRingBuffer(oneSec);
                    _mic.DataAvailable += OnMicDataAvailable;
                    _mixWithMic = true;
                }
                else if (canSolo)
                {
                    audioSampleRate = _mic.SampleRate;
                    willWriteAudio  = true;
                    _micOnly = true;
                }
                else
                {
                    // System+mic at different rates, or no system and mic at
                    // an AAC-incompatible rate. Drop mic; keep system if any.
                    _mic.Dispose();
                    _mic = null;
                }
            }
            catch
            {
                try { _mic?.Dispose(); } catch { }
                _mic = null;
            }
        }

        _audioBytesPerSecond = willWriteAudio ? (long)audioSampleRate * 2 * 2 : 0;

        _writer = new MediaSinkWriter(
            OutputPath, _width, _height, VideoFps, VideoBitrate,
            withAudio: willWriteAudio,
            audioSampleRate: willWriteAudio ? audioSampleRate : 48000,
            audioChannels: 2,
            audioBitsPerSample: 16);

        // Subscribe to the timeline-driving source AFTER the writer is alive so
        // the handler never observes a null _writer.
        if (_audio != null) _audio.DataAvailable += OnAudioDataAvailable;
        else if (_mic != null && _micOnly) _mic.DataAvailable += OnAudioDataAvailable;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            size: new SizeInt32 { Width = _width, Height = _height });
        _framePool.FrameArrived += OnFrameArrived;

        _captureSession = _framePool.CreateCaptureSession(_item);
        try { _captureSession.IsCursorCaptureEnabled = _captureCursor; } catch { }

        // If the captured window/monitor is destroyed mid-recording (user
        // closes the window, monitor unplugged, etc.) we want to stop and
        // finalize the MP4 instead of producing a corrupt file.
        _item.Closed += OnItemClosed;

        _captureSession.StartCapture();
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        FireAbort("Capture source closed.");
    }

    public void Pause()
    {
        if (!_started) return;
        _isPaused = true;
    }

    public void Resume()
    {
        // _totalPausedTicks was extended by every paused frame, so the next
        // arriving frame's timestamp lines up at _lastWrittenTimestamp + 1
        // frame period without any explicit anchor.
        _isPaused = false;
    }

    private void FireAbort(string reason)
    {
        if (_abortFired) return;
        _abortFired = true;
        try { AbortRequested?.Invoke(reason); } catch { }
    }

    public async Task StopAsync()
    {
        if (!_started || _stopRequested) return;
        _stopRequested = true;

        try { _captureSession?.Dispose(); } catch { }
        _captureSession = null;

        if (_framePool != null)
        {
            try { _framePool.FrameArrived -= OnFrameArrived; } catch { }
            try { _framePool.Dispose(); } catch { }
            _framePool = null;
        }

        try { _audio?.Stop(); } catch { }
        try { _mic?.Stop(); }   catch { }

        // Finalize on a worker thread — Writer_Finalize can block while it
        // flushes the H.264 trailer and moov atom. Errors propagate so the
        // caller knows the MP4 is broken (instead of a misleading "Saved").
        await Task.Run(() => _writer?.FinalizeAndClose());
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_stopRequested || _disposed) return;

        Direct3D11CaptureFrame? frame = null;
        IntPtr texture = IntPtr.Zero;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame == null) return;

            if (_firstFrameTime == null)
                _firstFrameTime = frame.SystemRelativeTime;
            var rawTs = frame.SystemRelativeTime - _firstFrameTime.Value;

            // Pause: drop the frame, but bump _totalPausedTicks so that
            // when we resume the next written frame's timestamp lines up at
            // _lastWrittenTimestamp + one frame period.
            if (_isPaused)
            {
                if (_lastWrittenTimestamp != TimeSpan.MinValue)
                {
                    long gap = rawTs.Ticks - _lastWrittenTimestamp.Ticks - FrameMinTicks;
                    if (gap > _totalPausedTicks) _totalPausedTicks = gap;
                }
                return;
            }

            var ts = TimeSpan.FromTicks(rawTs.Ticks - _totalPausedTicks);
            if (ts.Ticks < 0) ts = TimeSpan.Zero;

            // Rate-limit at the source. WGC fires at the monitor refresh
            // rate; the sink writer's stream is configured at VideoFps so
            // we drop frames that arrive sooner than ~one frame period after
            // the last written frame.
            if (_lastWrittenTimestamp != TimeSpan.MinValue
                && ts.Ticks - _lastWrittenTimestamp.Ticks < FrameSkipBelowTicks)
            {
                return;
            }

            texture = D3D11.GetTexture2DPtr(frame.Surface);

            lock (_gate)
            {
                if (_stopRequested || _disposed || _writer == null
                    || _context == IntPtr.Zero || _staging == IntPtr.Zero)
                    return;

                D3D11.Context_CopyResource(_context, _staging, texture);

                int hr = D3D11.Context_Map(_context, _staging, 0, D3D11.D3D11_MAP_READ, 0, out var map);
                if (hr < 0)
                {
                    BumpFrameError("Map failed (HRESULT 0x" + hr.ToString("X8") + ")");
                    return;
                }
                try
                {
                    _writer.WriteVideoFrame(
                        map.pData,
                        (int)map.RowPitch,
                        ts,
                        TimeSpan.FromTicks(FrameMinTicks));
                    _lastWrittenTimestamp = ts;
                    _consecutiveFrameErrors = 0;
                }
                finally
                {
                    D3D11.Context_Unmap(_context, _staging, 0);
                }
            }
        }
        catch (Exception ex)
        {
            BumpFrameError(ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (texture != IntPtr.Zero) Marshal.Release(texture);
            frame?.Dispose();
        }
    }

    private void BumpFrameError(string detail)
    {
        if (Interlocked.Increment(ref _consecutiveFrameErrors) >= PersistentErrorThreshold)
        {
            FireAbort("Frame pipeline failing persistently — " + detail);
        }
    }

    private void OnAudioDataAvailable(byte[] buffer, int count)
    {
        var writer = _writer;
        if (_stopRequested || _disposed || writer == null || _audioBytesPerSecond == 0) return;
        // Pause drops audio just like video, keeping the stream gap-free.
        if (_isPaused) return;

        // Single-threaded: only one of _audio / _mic ever feeds this handler,
        // so a plain read-then-add is enough.
        long beforeBytes = _audioBytesAccumulated;
        _audioBytesAccumulated = beforeBytes + count;

        var ts = TimeSpan.FromTicks(beforeBytes * 10_000_000L / _audioBytesPerSecond);
        try
        {
            if (_mixWithMic && _micRing != null)
            {
                if (_micMix == null || _micMix.Length < count) _micMix = new byte[count];
                _micRing.ReadPadded(_micMix, count);
                MixInPlace(buffer, _micMix, count);
            }
            writer.WriteAudio(new ReadOnlySpan<byte>(buffer, 0, count), ts);
        }
        catch { /* drop on transient failure */ }
    }

    private void OnMicDataAvailable(byte[] buffer, int count)
    {
        if (_stopRequested || _disposed || _micRing == null) return;
        if (_isPaused) return;
        // Mic packets pile into the ring; the system-audio handler dequeues
        // them at write time, so the mix happens on the loopback's cadence.
        if (count > 0) _micRing.Write(buffer, count);
    }

    private static unsafe void MixInPlace(byte[] dst, byte[] src, int byteCount)
    {
        int samples = byteCount / 2;
        fixed (byte* dp = dst, sp = src)
        {
            short* d = (short*)dp;
            short* s = (short*)sp;
            for (int i = 0; i < samples; i++)
            {
                int sum = d[i] + s[i];
                if (sum >  32767) sum =  32767;
                else if (sum < -32768) sum = -32768;
                d[i] = (short)sum;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCore();
    }

    private void DisposeCore()
    {
        // Stop event sources first so no new handler runs after we release
        // their backing resources.
        try { if (_item != null) _item.Closed -= OnItemClosed; } catch { }
        try { _captureSession?.Dispose(); } catch { }
        try
        {
            if (_framePool != null)
            {
                try { _framePool.FrameArrived -= OnFrameArrived; } catch { }
                _framePool.Dispose();
            }
        }
        catch { }
        try { _audio?.Dispose(); } catch { }
        try { _mic?.Dispose();   } catch { }
        try { _writer?.Dispose(); } catch { }

        // Then wait for any in-flight frame handler to leave _gate before
        // releasing the D3D11 resources it touched.
        lock (_gate)
        {
            D3D11.Release(ref _staging);
            D3D11.Release(ref _context);
            D3D11.Release(ref _device);
        }
    }
}
