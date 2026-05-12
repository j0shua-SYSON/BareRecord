using System;

namespace BareRecord.Audio;

/// <summary>
/// Lock-protected byte ring used to align a secondary audio stream (mic)
/// with the timeline-driving stream (system loopback). Overflowing writes
/// drop the oldest bytes; reads zero-pad on underflow.
/// </summary>
internal sealed class PcmRingBuffer
{
    private readonly byte[] _buf;
    private int _read;
    private int _write;
    private int _count;
    private readonly object _lock = new();

    public PcmRingBuffer(int capacityBytes)
    {
        if (capacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        _buf = new byte[capacityBytes];
    }

    public void Write(byte[] src, int count)
    {
        if (count <= 0 || count > _buf.Length) return;
        lock (_lock)
        {
            if (_count + count > _buf.Length)
            {
                int drop = _count + count - _buf.Length;
                _read = (_read + drop) % _buf.Length;
                _count -= drop;
            }
            int first = Math.Min(count, _buf.Length - _write);
            Buffer.BlockCopy(src, 0, _buf, _write, first);
            int rest = count - first;
            if (rest > 0) Buffer.BlockCopy(src, first, _buf, 0, rest);
            _write = (_write + count) % _buf.Length;
            _count += count;
        }
    }

    /// <summary>Read exactly <paramref name="count"/> bytes; zero-pad on underflow.</summary>
    public void ReadPadded(byte[] dst, int count)
    {
        lock (_lock)
        {
            int have = Math.Min(_count, count);
            int first = Math.Min(have, _buf.Length - _read);
            Buffer.BlockCopy(_buf, _read, dst, 0, first);
            int rest = have - first;
            if (rest > 0) Buffer.BlockCopy(_buf, 0, dst, first, rest);
            _read = (_read + have) % _buf.Length;
            _count -= have;
            if (have < count) Array.Clear(dst, have, count - have);
        }
    }
}
