using System.Net;
using System.Net.Sockets;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

public enum RtcmSourceKind
{
    Ntrip,
    Serial,
    TcpClient,
    UdpListen,
}

/// <summary>Where correction bytes come from. Used by one worker task at a time.</summary>
public interface IRtcmSource : IAsyncDisposable
{
    string Description { get; }

    /// <summary>Reconnect when nothing arrived for this long (null: a quiet link is fine, e.g. UDP).</summary>
    TimeSpan? SilenceTimeout { get; }

    Task OpenAsync(CancellationToken cancel);

    /// <summary>Reads what is available: 0 means nothing yet (call again); throws <see cref="IOException"/> when closed.</summary>
    Task<int> ReadAsync(byte[] buffer, CancellationToken cancel);

    Task CloseAsync();
}

/// <summary>A serial base station (u-blox, Septentrio, ...); exposes the port for receiver configuration.</summary>
public sealed class SerialRtcmSource(string portName, int baud) : IRtcmSource
{
    public string PortName => portName;

    /// <summary>Baud rate for the next open; u-blox configuration changes it to 460800.</summary>
    public int Baud { get; set; } = baud;

    public string Description => $"{portName} @ {Baud}";

    public TimeSpan? SilenceTimeout => TimeSpan.FromSeconds(10);

    /// <summary>The open port; u-blox configuration writes to it.</summary>
    public ICommsSerial? Port { get; private set; }

    public async Task OpenAsync(CancellationToken cancel)
    {
        var port = LinkFactory.Create(LinkSpec.Serial(portName, Baud));
        port.ReadBufferSize = 64 * 1024;
        await Task.Run(port.Open, cancel).ConfigureAwait(false);
        Port = port;
    }

    public async Task<int> ReadAsync(byte[] buffer, CancellationToken cancel)
    {
        var port = Port ?? throw new IOException("not open");
        if (!port.IsOpen)
            throw new IOException("The serial port closed.");
        var available = port.BytesToRead;
        if (available <= 0)
        {
            await Task.Delay(10, cancel).ConfigureAwait(false);
            return 0;
        }

        return port.Read(buffer, 0, Math.Min(buffer.Length, available));
    }

    public Task CloseAsync()
    {
        var port = Port;
        Port = null;
        if (port != null)
        {
            try
            {
                port.Close();
                port.Dispose();
            }
            catch (Exception)
            {
                // already gone (unplugged)
            }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}

/// <summary>A TCP server that streams RTCM (a base station's TCP output, str2str, ...).</summary>
public sealed class TcpRtcmSource(string host, int port) : IRtcmSource
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public string Description => $"tcp://{host}:{port}";

    public TimeSpan? SilenceTimeout => TimeSpan.FromSeconds(10);

    public async Task OpenAsync(CancellationToken cancel)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public async Task<int> ReadAsync(byte[] buffer, CancellationToken cancel)
    {
        var stream = _stream ?? throw new IOException("not open");
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        silence.CancelAfter(SilenceTimeout!.Value);
        try
        {
            var n = await stream.ReadAsync(buffer, silence.Token).ConfigureAwait(false);
            if (n == 0)
                throw new IOException($"{host}:{port} closed the connection.");
            return n;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"No data from {host}:{port} for {SilenceTimeout.Value.TotalSeconds:0} s.");
        }
    }

    public Task CloseAsync()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}

/// <summary>Listens for RTCM in UDP datagrams on a local port.</summary>
public sealed class UdpRtcmSource(int port) : IRtcmSource
{
    private UdpClient? _udp;
    private byte[]? _partial;
    private int _partialOffset;

    public string Description => $"udp://0.0.0.0:{port}";

    public TimeSpan? SilenceTimeout => null;

    /// <summary>The bound port (useful when 0 was asked for).</summary>
    public int LocalPort => (_udp?.Client.LocalEndPoint as IPEndPoint)?.Port ?? port;

    public Task OpenAsync(CancellationToken cancel)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        return Task.CompletedTask;
    }

    public async Task<int> ReadAsync(byte[] buffer, CancellationToken cancel)
    {
        var udp = _udp ?? throw new IOException("not open");
        if (_partial == null)
        {
            var result = await udp.ReceiveAsync(cancel).ConfigureAwait(false);
            _partial = result.Buffer;
            _partialOffset = 0;
        }

        var n = Math.Min(buffer.Length, _partial.Length - _partialOffset);
        Array.Copy(_partial, _partialOffset, buffer, 0, n);
        _partialOffset += n;
        if (_partialOffset >= _partial.Length)
            _partial = null;
        return n;
    }

    public Task CloseAsync()
    {
        _udp?.Dispose();
        _udp = null;
        _partial = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
