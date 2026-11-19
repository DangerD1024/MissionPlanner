using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

/// <summary>
/// NTRIP 1 and 2 client: connects to a caster, authenticates, handles chunked NTRIP 2 streams and
/// sends GGA positions back. Replaces CommsNTRIP for the page (which cannot list mountpoints, does
/// not skip the HTTP header and does not decode chunked streams).
/// </summary>
public sealed class NtripClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private Stream? _stream;
    private HttpChunkedDecoder? _chunked;
    private readonly List<byte> _pending = new();
    private readonly byte[] _raw = new byte[8192];

    public NtripClient(NtripEndpoint endpoint) => Endpoint = endpoint;

    public NtripEndpoint Endpoint { get; }

    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public NtripResponseHeader? Header { get; private set; }

    public bool IsOpen => _tcp?.Connected == true && _stream != null;

    /// <summary>Connects to the mountpoint; throws <see cref="NtripException"/> when the caster refuses.</summary>
    public async Task ConnectAsync(CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(Endpoint.Mountpoint))
            throw new NtripException("Enter a mountpoint, or load the caster's list and pick one.", fatal: true);

        var (stream, tcp, header, leftover) = await OpenAsync(Endpoint, Endpoint.Mountpoint, cancel).ConfigureAwait(false);
        if (header.Kind != NtripResponseKind.Stream)
        {
            tcp.Dispose();
            var fatal = header.Kind is NtripResponseKind.SourceTable or NtripResponseKind.Unauthorized or NtripResponseKind.NotFound;
            throw new NtripException(header.Explain(Endpoint.Mountpoint), fatal);
        }

        _tcp = tcp;
        _stream = stream;
        Header = header;
        _chunked = header.Chunked ? new HttpChunkedDecoder() : null;
        _pending.Clear();
        Accept(leftover);
    }

    /// <summary>Reads correction bytes; returns 0 when the caster closed the connection.</summary>
    public async Task<int> ReadAsync(byte[] buffer, CancellationToken cancel)
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        while (_pending.Count == 0)
        {
            var n = await stream.ReadAsync(_raw.AsMemory(), cancel).ConfigureAwait(false);
            if (n == 0)
                return 0;
            Accept(_raw.AsSpan(0, n));
            if (_chunked?.IsComplete == true && _pending.Count == 0)
                return 0;
        }

        var count = Math.Min(buffer.Length, _pending.Count);
        _pending.CopyTo(0, buffer, 0, count);
        _pending.RemoveRange(0, count);
        return count;
    }

    public async Task WriteAsync(byte[] data, CancellationToken cancel)
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        await _writeLock.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(data, cancel).ConfigureAwait(false);
            await stream.FlushAsync(cancel).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task SendGgaAsync(GgaPosition position, CancellationToken cancel) =>
        WriteAsync(NmeaGga.ToBytes(NmeaGga.Build(position, DateTime.UtcNow)), cancel);

    private void Accept(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;
        if (_chunked != null)
            _chunked.Decode(data, _pending);
        else
            _pending.AddRange(data.ToArray());
    }

    /// <summary>Downloads and parses the caster's source table (the mountpoint list).</summary>
    public static async Task<IReadOnlyList<NtripMountpoint>> GetSourceTableAsync(NtripEndpoint endpoint, CancellationToken cancel)
    {
        var (stream, tcp, header, leftover) = await OpenAsync(endpoint, "", cancel).ConfigureAwait(false);
        using (tcp)
        {
            if (header.Kind is not (NtripResponseKind.SourceTable or NtripResponseKind.Stream))
                throw new NtripException(header.Kind == NtripResponseKind.Unauthorized
                    ? "The caster refused the username or password (" + header.StatusLine + ")."
                    : "The caster did not send a source table: " + header.StatusLine, fatal: true);

            var chunked = header.Chunked ? new HttpChunkedDecoder() : null;
            var body = new List<byte>();
            void Add(ReadOnlySpan<byte> d)
            {
                if (chunked != null)
                    chunked.Decode(d, body);
                else
                    body.AddRange(d.ToArray());
            }

            Add(leftover);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var buf = new byte[16384];
            try
            {
                while (!EndsTable(body) && chunked?.IsComplete != true)
                {
                    var n = await stream.ReadAsync(buf, timeout.Token).ConfigureAwait(false);
                    if (n == 0)
                        break;
                    Add(buf.AsSpan(0, n));
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                // a caster that neither ends the table nor closes: use what arrived
            }

            return NtripProtocol.ParseSourceTable(Encoding.UTF8.GetString(body.ToArray()));
        }
    }

    private static bool EndsTable(List<byte> body)
    {
        const string end = "ENDSOURCETABLE";
        if (body.Count < end.Length)
            return false;
        var tail = Encoding.ASCII.GetString(body.GetRange(Math.Max(0, body.Count - 64), Math.Min(64, body.Count)).ToArray());
        return tail.Contains(end, StringComparison.Ordinal);
    }

    private static async Task<(Stream Stream, TcpClient Tcp, NtripResponseHeader Header, byte[] Leftover)> OpenAsync(
        NtripEndpoint ep, string mountpoint, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(ep.Host))
            throw new NtripException("Enter the caster's address.", fatal: true);

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await tcp.ConnectAsync(ep.Host, ep.Port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new NtripException($"No answer from {ep.Host}:{ep.Port}.", fatal: false);
            }
            catch (SocketException ex)
            {
                throw new NtripException($"Cannot reach {ep.Host}:{ep.Port}: {ex.Message}", fatal: false, ex);
            }

            Stream stream = tcp.GetStream();
            if (ep.UseTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = ep.Host }, timeout.Token)
                    .ConfigureAwait(false);
                stream = ssl;
            }

            var request = Encoding.ASCII.GetBytes(NtripProtocol.BuildRequest(ep, mountpoint));
            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

            var buf = new byte[8192];
            var count = 0;
            try
            {
                while (true)
                {
                    if (count > 0 && NtripProtocol.TryParseHeader(buf.AsSpan(0, count), out var header, out var len))
                        return (stream, tcp, header, buf.AsSpan(len, count - len).ToArray());
                    if (count == buf.Length)
                        throw new NtripException("The caster's answer is not NTRIP.", fatal: true);

                    var n = await stream.ReadAsync(buf.AsMemory(count), timeout.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        if (count == 0)
                            throw new NtripException($"{ep.Host}:{ep.Port} closed the connection without answering.", fatal: false);
                        // a one-line answer without a newline, then close
                        var line = Encoding.ASCII.GetString(buf, 0, count) + "\n";
                        NtripProtocol.TryParseHeader(Encoding.ASCII.GetBytes(line), out var last, out _);
                        return (stream, tcp, last, Array.Empty<byte>());
                    }

                    count += n;
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new NtripException($"{ep.Host}:{ep.Port} did not answer the NTRIP request.", fatal: false);
            }
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
        }

        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
        return ValueTask.CompletedTask;
    }
}
