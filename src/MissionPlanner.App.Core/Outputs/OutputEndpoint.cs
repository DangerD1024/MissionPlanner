using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Core.Outputs;

/// <summary>How an output reaches other programs, as Mission Planner's serial-output windows offer.</summary>
public enum OutputTransportKind
{
    /// <summary>A serial port (Address = port name, Value = baud).</summary>
    Serial,

    /// <summary>Listens on a TCP port; every client that connects gets the data (Value = port).</summary>
    TcpServer,

    /// <summary>Connects to a TCP server (Address = host, Value = port).</summary>
    TcpClient,

    /// <summary>Sends UDP to an address, unicast, broadcast or multicast (Address = host, Value = port).</summary>
    UdpSend,

    /// <summary>Listens on a UDP port and answers whoever sent last (Value = port).</summary>
    UdpListen,
}

/// <summary>Where an output goes. Creates an unopened <see cref="ICommsSerial"/> that never prompts.</summary>
public sealed record OutputEndpoint(OutputTransportKind Kind, string Address, int Value)
{
    public static OutputEndpoint TcpServer(int port) => new(OutputTransportKind.TcpServer, "", port);

    public static OutputEndpoint UdpSend(string host, int port) => new(OutputTransportKind.UdpSend, host, port);

    public bool NeedsAddress => Kind is OutputTransportKind.Serial or OutputTransportKind.TcpClient or OutputTransportKind.UdpSend;

    /// <summary>Why it cannot be opened, or null.</summary>
    public string? Validate()
    {
        if (NeedsAddress && string.IsNullOrWhiteSpace(Address))
            return Kind == OutputTransportKind.Serial ? "Pick a serial port." : "Enter the host or IP address.";
        if (Kind == OutputTransportKind.Serial)
            return Value > 0 ? null : "Pick a baud rate.";
        return Value is > 0 and <= 65535 ? null : "The port must be 1–65535.";
    }

    public ICommsSerial Create() => Kind switch
    {
        OutputTransportKind.Serial => LinkFactory.Create(LinkSpec.Serial(Address, Value)),
        OutputTransportKind.TcpClient => LinkFactory.Create(LinkSpec.Tcp(Address, Value)),
        OutputTransportKind.UdpSend => LinkFactory.Create(LinkSpec.UdpConnect(Address, Value)),
        OutputTransportKind.UdpListen => LinkFactory.Create(LinkSpec.UdpListen(Value)),
        OutputTransportKind.TcpServer => new TcpServerComms(Value),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
    };

    public override string ToString() => Kind switch
    {
        OutputTransportKind.Serial => $"{Address} at {Value.ToString(CultureInfo.InvariantCulture)} baud",
        OutputTransportKind.TcpServer => $"TCP server on port {Value.ToString(CultureInfo.InvariantCulture)}",
        OutputTransportKind.TcpClient => $"TCP to {Address}:{Value.ToString(CultureInfo.InvariantCulture)}",
        OutputTransportKind.UdpSend => $"UDP to {Address}:{Value.ToString(CultureInfo.InvariantCulture)}",
        OutputTransportKind.UdpListen => $"UDP on port {Value.ToString(CultureInfo.InvariantCulture)}",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// A TCP server as an <see cref="ICommsSerial"/>: writes go to every connected client, and what the
/// clients send is read back merged. Clients come and go at any time; with none, writes are dropped.
/// </summary>
public sealed class TcpServerComms : ICommsSerial
{
    private readonly int _port;
    private readonly ConcurrentQueue<byte[]> _incoming = new();
    private readonly List<TcpClient> _clients = new();
    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private byte[]? _partial;
    private int _partialOffset;
    private int _available;

    public TcpServerComms(int port) => _port = port;

    /// <summary>The port actually listened on (useful with port 0).</summary>
    public int LocalPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _port;

    public int ClientCount
    {
        get
        {
            lock (_gate)
                return _clients.Count;
        }
    }

    public bool IsOpen => _listener != null;

    public void Open()
    {
        if (_listener != null)
            return;
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        _listener = listener;
        _stop = new CancellationTokenSource();
        _ = AcceptLoop(listener, _stop.Token);
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            client.NoDelay = true;
            lock (_gate)
                _clients.Add(client);
            _ = ReadLoop(client, stop);
        }
    }

    private async Task ReadLoop(TcpClient client, CancellationToken stop)
    {
        var buffer = new byte[4096];
        try
        {
            var stream = client.GetStream();
            while (!stop.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, stop).ConfigureAwait(false);
                if (n <= 0)
                    break;
                _incoming.Enqueue(buffer[..n]);
                Interlocked.Add(ref _available, n);
            }
        }
        catch (Exception)
        {
            // the client went away
        }

        Drop(client);
    }

    private void Drop(TcpClient client)
    {
        lock (_gate)
            _clients.Remove(client);
        client.Dispose();
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        TcpClient[] clients;
        lock (_gate)
            clients = _clients.ToArray();
        foreach (var client in clients)
        {
            try
            {
                client.GetStream().Write(buffer, offset, count);
            }
            catch (Exception)
            {
                Drop(client);
            }
        }
    }

    public void Write(string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Write(bytes, 0, bytes.Length);
    }

    public void WriteLine(string text) => Write(text + "\n");

    public int BytesToRead => Volatile.Read(ref _available);

    public int Read(byte[] buffer, int offset, int count)
    {
        var read = 0;
        while (read < count)
        {
            if (_partial == null)
            {
                if (!_incoming.TryDequeue(out _partial))
                    break;
                _partialOffset = 0;
            }

            var n = Math.Min(count - read, _partial.Length - _partialOffset);
            Array.Copy(_partial, _partialOffset, buffer, offset + read, n);
            _partialOffset += n;
            read += n;
            if (_partialOffset >= _partial.Length)
                _partial = null;
        }

        Interlocked.Add(ref _available, -read);
        return read;
    }

    public int ReadByte()
    {
        var one = new byte[1];
        return Read(one, 0, 1) == 1 ? one[0] : -1;
    }

    public int ReadChar() => ReadByte();

    public string ReadExisting()
    {
        var buffer = new byte[BytesToRead];
        var n = Read(buffer, 0, buffer.Length);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, n);
    }

    public string ReadLine() => ReadExisting();

    public void DiscardInBuffer()
    {
        while (_incoming.TryDequeue(out _))
        {
        }

        _partial = null;
        Volatile.Write(ref _available, 0);
    }

    public void Close()
    {
        _stop?.Cancel();
        _listener?.Stop();
        _listener = null;
        lock (_gate)
        {
            foreach (var c in _clients)
                c.Dispose();
            _clients.Clear();
        }
    }

    public void Dispose() => Close();

    public void toggleDTR()
    {
    }

    public Stream BaseStream => Stream.Null;
    public int BaudRate { get; set; }
    public int BytesToWrite => 0;
    public int DataBits { get; set; } = 8;
    public bool DtrEnable { get; set; }
    public string PortName { get; set; } = "";
    public int ReadBufferSize { get; set; }
    public int ReadTimeout { get; set; }
    public bool RtsEnable { get; set; }
    public int WriteBufferSize { get; set; }
    public int WriteTimeout { get; set; }
}
