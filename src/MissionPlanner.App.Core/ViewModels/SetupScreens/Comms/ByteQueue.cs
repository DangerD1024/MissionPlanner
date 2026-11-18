namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

/// <summary>
/// A thread-safe FIFO of bytes with a blocking, time-limited read: one thread (a MAVLink handler)
/// appends, another (a reader loop) takes. Oldest bytes are dropped past <see cref="Capacity"/>.
/// </summary>
public sealed class ByteQueue
{
    private readonly object _gate = new();
    private byte[] _buffer = new byte[4096];
    private int _head;
    private int _count;

    public ByteQueue(int capacity = 1 << 20) => Capacity = capacity;

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        lock (_gate)
        {
            if (data.Length > Capacity)
                data = data[^Capacity..];
            var overflow = _count + data.Length - Capacity;
            if (overflow > 0)
                Drop(overflow);
            EnsureRoom(_count + data.Length);

            var tail = (_head + _count) % _buffer.Length;
            var first = Math.Min(data.Length, _buffer.Length - tail);
            data[..first].CopyTo(_buffer.AsSpan(tail));
            data[first..].CopyTo(_buffer);
            _count += data.Length;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Takes up to <paramref name="count"/> bytes, waiting up to <paramref name="timeoutMs"/> for the first.</summary>
    public int Take(byte[] destination, int offset, int count, int timeoutMs)
    {
        lock (_gate)
        {
            if (_count == 0 && timeoutMs > 0)
            {
                var deadline = Environment.TickCount64 + timeoutMs;
                while (_count == 0)
                {
                    var left = deadline - Environment.TickCount64;
                    if (left <= 0)
                        break;
                    Monitor.Wait(_gate, (int)left);
                }
            }

            var n = Math.Min(count, _count);
            var first = Math.Min(n, _buffer.Length - _head);
            Array.Copy(_buffer, _head, destination, offset, first);
            Array.Copy(_buffer, 0, destination, offset + first, n - first);
            Drop(n);
            return n;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>Wakes any waiting reader (used when the link closes).</summary>
    public void Release()
    {
        lock (_gate)
            Monitor.PulseAll(_gate);
    }

    private void Drop(int n)
    {
        _head = (_head + n) % _buffer.Length;
        _count -= n;
        if (_count == 0)
            _head = 0;
    }

    private void EnsureRoom(int needed)
    {
        if (needed <= _buffer.Length)
            return;
        var size = _buffer.Length;
        while (size < needed)
            size *= 2;
        var bigger = new byte[size];
        var first = Math.Min(_count, _buffer.Length - _head);
        Array.Copy(_buffer, _head, bigger, 0, first);
        Array.Copy(_buffer, 0, bigger, first, _count - first);
        _buffer = bigger;
        _head = 0;
    }
}
