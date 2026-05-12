using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using static BareRecord.Encoding.Mf;

namespace BareRecord.Encoding;

/// <summary>
/// Owns an IMFSinkWriter that mux H.264 video (from RGB32 input — the sink
/// writer auto-inserts a color converter MFT) and optional AAC audio (from
/// PCM 16-bit 48k stereo) into an MP4 container.
/// </summary>
internal sealed partial class MediaSinkWriter : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }

    private readonly bool _withAudio;
    private readonly int _audioSampleRate;
    private readonly int _audioChannels;
    private readonly int _audioBitsPerSample;

    private IntPtr _writer;
    private int _videoStreamIndex = -1;
    private int _audioStreamIndex = -1;
    private long _audioBytesWritten;
    private bool _started;
    private bool _disposed;
    private static int _refCount;
    private readonly object _gate = new();

    public MediaSinkWriter(
        string outputPath,
        int width,
        int height,
        int fps,
        int videoBitrate,
        bool withAudio,
        int audioSampleRate,
        int audioChannels,
        int audioBitsPerSample)
    {
        Width = width;
        Height = height;
        Fps = fps;
        _withAudio = withAudio;
        _audioSampleRate = audioSampleRate;
        _audioChannels = audioChannels;
        _audioBitsPerSample = audioBitsPerSample;

        EnsureMfStarted();

        IntPtr attrs = IntPtr.Zero;
        IntPtr videoOut = IntPtr.Zero, videoIn = IntPtr.Zero;
        IntPtr audioOut = IntPtr.Zero, audioIn = IntPtr.Zero;

        try
        {
            CheckHr(MFCreateAttributes(out attrs, 4));
            CheckHr(Attr_SetUINT32(attrs, in MF_LOW_LATENCY, 1));
            CheckHr(Attr_SetUINT32(attrs, in MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1));
            CheckHr(Attr_SetUINT32(attrs, in MF_SINK_WRITER_DISABLE_THROTTLING, 1));

            // Make sure target dir exists; MF will fail with E_INVALIDARG otherwise.
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            CheckHr(MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, attrs, out _writer));

            // ── video output (H.264) ────────────────────────────────────────
            CheckHr(MFCreateMediaType(out videoOut));
            CheckHr(Attr_SetGUID  (videoOut, in MF_MT_MAJOR_TYPE, in MFMediaType_Video));
            CheckHr(Attr_SetGUID  (videoOut, in MF_MT_SUBTYPE,    in MFVideoFormat_H264));
            CheckHr(Attr_SetUINT32(videoOut, in MF_MT_AVG_BITRATE, (uint)videoBitrate));
            CheckHr(Attr_SetUINT32(videoOut, in MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
            CheckHr(Attr_SetUINT32(videoOut, in MF_MT_VIDEO_PROFILE,  eAVEncH264VProfile_High));
            CheckHr(Attr_SetSize  (videoOut, in MF_MT_FRAME_SIZE, width, height));
            CheckHr(Attr_SetRatio (videoOut, in MF_MT_FRAME_RATE, fps, 1));
            CheckHr(Attr_SetRatio (videoOut, in MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
            CheckHr(Writer_AddStream(_writer, videoOut, out _videoStreamIndex));

            // ── video input (RGB32 / BGRA, top-down) ────────────────────────
            CheckHr(MFCreateMediaType(out videoIn));
            CheckHr(Attr_SetGUID  (videoIn, in MF_MT_MAJOR_TYPE, in MFMediaType_Video));
            CheckHr(Attr_SetGUID  (videoIn, in MF_MT_SUBTYPE,    in MFVideoFormat_RGB32));
            CheckHr(Attr_SetUINT32(videoIn, in MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
            CheckHr(Attr_SetUINT32(videoIn, in MF_MT_DEFAULT_STRIDE, (uint)(width * 4)));
            CheckHr(Attr_SetSize  (videoIn, in MF_MT_FRAME_SIZE, width, height));
            CheckHr(Attr_SetRatio (videoIn, in MF_MT_FRAME_RATE, fps, 1));
            CheckHr(Attr_SetRatio (videoIn, in MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
            CheckHr(Writer_SetInputMediaType(_writer, _videoStreamIndex, videoIn, IntPtr.Zero));

            if (_withAudio)
            {
                int blockAlign = audioChannels * (audioBitsPerSample / 8);
                int bytesPerSecond = audioSampleRate * blockAlign;

                // ── audio output (AAC) ──────────────────────────────────────
                CheckHr(MFCreateMediaType(out audioOut));
                CheckHr(Attr_SetGUID  (audioOut, in MF_MT_MAJOR_TYPE, in MFMediaType_Audio));
                CheckHr(Attr_SetGUID  (audioOut, in MF_MT_SUBTYPE,    in MFAudioFormat_AAC));
                CheckHr(Attr_SetUINT32(audioOut, in MF_MT_AUDIO_BITS_PER_SAMPLE,      (uint)audioBitsPerSample));
                CheckHr(Attr_SetUINT32(audioOut, in MF_MT_AUDIO_SAMPLES_PER_SECOND,   (uint)audioSampleRate));
                CheckHr(Attr_SetUINT32(audioOut, in MF_MT_AUDIO_NUM_CHANNELS,         (uint)audioChannels));
                CheckHr(Attr_SetUINT32(audioOut, in MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 16000)); // 128 kbps target
                CheckHr(Writer_AddStream(_writer, audioOut, out _audioStreamIndex));

                // ── audio input (PCM) ───────────────────────────────────────
                CheckHr(MFCreateMediaType(out audioIn));
                CheckHr(Attr_SetGUID  (audioIn, in MF_MT_MAJOR_TYPE, in MFMediaType_Audio));
                CheckHr(Attr_SetGUID  (audioIn, in MF_MT_SUBTYPE,    in MFAudioFormat_PCM));
                CheckHr(Attr_SetUINT32(audioIn, in MF_MT_AUDIO_BITS_PER_SAMPLE,      (uint)audioBitsPerSample));
                CheckHr(Attr_SetUINT32(audioIn, in MF_MT_AUDIO_SAMPLES_PER_SECOND,   (uint)audioSampleRate));
                CheckHr(Attr_SetUINT32(audioIn, in MF_MT_AUDIO_NUM_CHANNELS,         (uint)audioChannels));
                CheckHr(Attr_SetUINT32(audioIn, in MF_MT_AUDIO_BLOCK_ALIGNMENT,      (uint)blockAlign));
                CheckHr(Attr_SetUINT32(audioIn, in MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)bytesPerSecond));
                CheckHr(Writer_SetInputMediaType(_writer, _audioStreamIndex, audioIn, IntPtr.Zero));
            }

            CheckHr(Writer_BeginWriting(_writer));
            _started = true;
        }
        catch
        {
            Release(ref _writer);
            ReleaseMfIfLast();
            throw;
        }
        finally
        {
            Release(ref attrs);
            Release(ref videoOut);
            Release(ref videoIn);
            Release(ref audioOut);
            Release(ref audioIn);
        }
    }

    /// <summary>
    /// Append one BGRA video frame. <paramref name="sourcePtr"/> points to
    /// <c>height</c> rows of <paramref name="sourceRowPitch"/> bytes each.
    /// </summary>
    public void WriteVideoFrame(IntPtr sourcePtr, int sourceRowPitch, TimeSpan timestamp, TimeSpan duration)
    {
        if (!_started) return;
        lock (_gate)
        {
            // FinalizeAndClose can null _writer while we wait for the lock.
            if (_disposed || _writer == IntPtr.Zero || !_started) return;

            int destRowPitch = Width * 4;
            uint totalBytes = (uint)(destRowPitch * Height);

            IntPtr buffer = IntPtr.Zero, sample = IntPtr.Zero;
            try
            {
                CheckHr(MFCreateMemoryBuffer(totalBytes, out buffer));
                CheckHr(Buffer_Lock(buffer, out var dest, out _, out _));
                try
                {
                    unsafe
                    {
                        byte* src = (byte*)sourcePtr;
                        byte* dst = (byte*)dest;
                        if (sourceRowPitch == destRowPitch)
                        {
                            Buffer.MemoryCopy(src, dst, totalBytes, totalBytes);
                        }
                        else
                        {
                            for (int y = 0; y < Height; y++)
                            {
                                Buffer.MemoryCopy(
                                    src + (long)y * sourceRowPitch,
                                    dst + (long)y * destRowPitch,
                                    destRowPitch,
                                    destRowPitch);
                            }
                        }
                    }
                }
                finally
                {
                    CheckHr(Buffer_Unlock(buffer));
                }
                CheckHr(Buffer_SetCurrentLength(buffer, totalBytes));

                CheckHr(MFCreateSample(out sample));
                CheckHr(Sample_AddBuffer(sample, buffer));
                CheckHr(Sample_SetSampleTime(sample, ToHns(timestamp)));
                CheckHr(Sample_SetSampleDuration(sample, ToHns(duration)));

                CheckHr(Writer_WriteSample(_writer, _videoStreamIndex, sample));
            }
            finally
            {
                Release(ref sample);
                Release(ref buffer);
            }
        }
    }

    public void WriteAudio(ReadOnlySpan<byte> pcm, TimeSpan timestamp)
    {
        if (!_started || !_withAudio || pcm.Length == 0) return;
        lock (_gate)
        {
            if (_disposed || _writer == IntPtr.Zero || !_started) return;

            int blockAlign = _audioChannels * (_audioBitsPerSample / 8);
            // Truncate to whole frames (just in case).
            int bytes = pcm.Length - (pcm.Length % blockAlign);
            if (bytes == 0) return;

            IntPtr buffer = IntPtr.Zero, sample = IntPtr.Zero;
            try
            {
                CheckHr(MFCreateMemoryBuffer((uint)bytes, out buffer));
                CheckHr(Buffer_Lock(buffer, out var dest, out _, out _));
                try
                {
                    unsafe
                    {
                        fixed (byte* src = pcm)
                        {
                            Buffer.MemoryCopy(src, (void*)dest, bytes, bytes);
                        }
                    }
                }
                finally
                {
                    CheckHr(Buffer_Unlock(buffer));
                }
                CheckHr(Buffer_SetCurrentLength(buffer, (uint)bytes));

                long durationHns = bytes * 10_000_000L / (_audioSampleRate * blockAlign);

                CheckHr(MFCreateSample(out sample));
                CheckHr(Sample_AddBuffer(sample, buffer));
                CheckHr(Sample_SetSampleTime(sample, ToHns(timestamp)));
                CheckHr(Sample_SetSampleDuration(sample, durationHns));

                CheckHr(Writer_WriteSample(_writer, _audioStreamIndex, sample));
                _audioBytesWritten += bytes;
            }
            finally
            {
                Release(ref sample);
                Release(ref buffer);
            }
        }
    }

    public void FinalizeAndClose()
    {
        lock (_gate)
        {
            if (!_started || _disposed) return;
            int finalizeHr = 0;
            try
            {
                // Flush per stream first; errors here aren't fatal — Finalize
                // will retry the flush on its own.
                if (_videoStreamIndex >= 0) Writer_Flush(_writer, _videoStreamIndex);
                if (_audioStreamIndex >= 0) Writer_Flush(_writer, _audioStreamIndex);
                finalizeHr = Writer_Finalize(_writer);
            }
            finally
            {
                Release(ref _writer);
                _started = false;
            }
            if (finalizeHr < 0) Marshal.ThrowExceptionForHR(finalizeHr);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_writer != IntPtr.Zero)
            {
                // Attempt to finalize; swallow failures so disposal is robust.
                try { Writer_Finalize(_writer); } catch { }
                Release(ref _writer);
            }
        }
        ReleaseMfIfLast();
    }

    private static long ToHns(TimeSpan t) => t.Ticks; // 1 tick = 100 ns

    private static void EnsureMfStarted()
    {
        if (Interlocked.Increment(ref _refCount) == 1)
        {
            CheckHr(MFStartup(MF_VERSION, MFSTARTUP_FULL));
        }
    }

    private static void ReleaseMfIfLast()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            try { MFShutdown(); } catch { }
        }
    }
}
