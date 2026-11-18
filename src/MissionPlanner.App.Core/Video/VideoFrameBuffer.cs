using System.Runtime.InteropServices;

namespace MissionPlanner.App.Core.Video;

/// <summary>Receives the latest frame's pixels (32-bit BGRA rows, <paramref name="stride"/> bytes apart).</summary>
public delegate void FrameReader(byte[] pixels, int width, int height, int stride);

/// <summary>
/// Hands the newest decoded frame from the decoder thread to the UI. Two reused buffers: the
/// decoder fills the back one without a lock, then swaps; the reader copies the front one under
/// the lock. Frames the UI had no time for are simply overwritten.
/// </summary>
public sealed class VideoFrameBuffer
{
    private readonly object _lock = new();
    private byte[] _front = Array.Empty<byte>();
    private byte[] _back = Array.Empty<byte>();
    private int _width;
    private int _height;
    private int _stride;
    private long _sequence;

    /// <summary>Moves by one for every frame written; 0 until the first.</summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    public int Width
    {
        get
        {
            lock (_lock)
                return _width;
        }
    }

    public int Height
    {
        get
        {
            lock (_lock)
                return _height;
        }
    }

    /// <summary>Decoder side: copies a frame from native memory.</summary>
    public void Write(IntPtr data, int width, int height, int stride)
    {
        var length = checked(stride * height);
        if (_back.Length != length)
            _back = new byte[length];
        Marshal.Copy(data, _back, 0, length);
        Publish(width, height, stride);
    }

    /// <summary>Decoder side, for managed sources (tests): copies a frame.</summary>
    public void Write(ReadOnlySpan<byte> data, int width, int height, int stride)
    {
        var length = checked(stride * height);
        if (_back.Length != length)
            _back = new byte[length];
        data[..length].CopyTo(_back);
        Publish(width, height, stride);
    }

    private void Publish(int width, int height, int stride)
    {
        lock (_lock)
        {
            (_front, _back) = (_back, _front);
            _width = width;
            _height = height;
            _stride = stride;
            Interlocked.Increment(ref _sequence);
        }
    }

    /// <summary>
    /// UI side: if a frame newer than <paramref name="lastSequence"/> is there, passes it to
    /// <paramref name="reader"/> (which must copy what it needs before returning) and updates
    /// <paramref name="lastSequence"/>.
    /// </summary>
    public bool TryRead(ref long lastSequence, FrameReader reader)
    {
        lock (_lock)
        {
            var seq = Interlocked.Read(ref _sequence);
            if (seq == lastSequence || _width == 0)
                return false;
            lastSequence = seq;
            reader(_front, _width, _height, _stride);
            return true;
        }
    }

    /// <summary>Forgets the last frame (after a stop), so nothing stale is shown again.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _width = 0;
            _height = 0;
            _stride = 0;
            Interlocked.Increment(ref _sequence);
        }
    }
}
