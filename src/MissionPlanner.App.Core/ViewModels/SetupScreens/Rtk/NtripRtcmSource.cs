using log4net;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

/// <summary>
/// An NTRIP caster mountpoint. When a position source is given, a GGA sentence goes to the caster
/// right after connecting and then every <see cref="GgaInterval"/> (VRS casters need it).
/// </summary>
public sealed class NtripRtcmSource(NtripEndpoint endpoint, Func<GgaPosition?>? ggaSource) : IRtcmSource
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(NtripRtcmSource));

    public static TimeSpan GgaInterval { get; set; } = TimeSpan.FromSeconds(10);

    private NtripClient? _client;
    private CancellationTokenSource? _ggaStop;
    private Task _gga = Task.CompletedTask;
    private int _ggaSent;

    public string Description => $"{endpoint.Host}:{endpoint.Port}/{endpoint.Mountpoint}";

    /// <summary>A VRS can take a while to start streaming after the first GGA.</summary>
    public TimeSpan? SilenceTimeout => TimeSpan.FromSeconds(30);

    public NtripEndpoint Endpoint => endpoint;

    /// <summary>GGA sentences sent to the caster so far.</summary>
    public int GgaSent => Volatile.Read(ref _ggaSent);

    public string? LastGga { get; private set; }

    public async Task OpenAsync(CancellationToken cancel)
    {
        var client = new NtripClient(endpoint);
        try
        {
            await client.ConnectAsync(cancel).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _client = client;
        if (ggaSource != null)
        {
            var stop = new CancellationTokenSource();
            _ggaStop = stop;
            _gga = Task.Run(() => GgaLoop(client, stop.Token), CancellationToken.None);
        }
    }

    public async Task<int> ReadAsync(byte[] buffer, CancellationToken cancel)
    {
        var client = _client ?? throw new IOException("not open");
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        silence.CancelAfter(SilenceTimeout!.Value);
        try
        {
            var n = await client.ReadAsync(buffer, silence.Token).ConfigureAwait(false);
            if (n == 0)
                throw new IOException("The caster closed the connection.");
            return n;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"No corrections from the caster for {SilenceTimeout.Value.TotalSeconds:0} s.");
        }
    }

    private async Task GgaLoop(NtripClient client, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var pos = ggaSource!();
                if (pos != null && (pos.Lat != 0 || pos.Lng != 0))
                {
                    var sentence = NmeaGga.Build(pos, DateTime.UtcNow);
                    await client.WriteAsync(NmeaGga.ToBytes(sentence), stop).ConfigureAwait(false);
                    LastGga = sentence;
                    Interlocked.Increment(ref _ggaSent);
                    await Task.Delay(GgaInterval, stop).ConfigureAwait(false);
                }
                else
                {
                    // no position yet: try again soon rather than wait a whole interval
                    await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // the read side notices a dead connection and reconnects
                Log.Warn("ntrip gga", ex);
                return;
            }
        }
    }

    public async Task CloseAsync()
    {
        var stop = _ggaStop;
        _ggaStop = null;
        if (stop != null)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await _gga.ConfigureAwait(false);
            stop.Dispose();
        }

        var client = _client;
        _client = null;
        if (client != null)
            await client.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
