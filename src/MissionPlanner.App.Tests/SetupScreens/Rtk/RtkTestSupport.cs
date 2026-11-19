using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.SetupScreens.Rtk;

/// <summary>Builds valid RTCM 3 frames bit by bit (preamble, length, payload, CRC-24Q).</summary>
internal sealed class RtcmFrameBuilder
{
    private readonly byte[] _buf = new byte[1100];
    private int _bit = 24;

    public RtcmFrameBuilder Put(long value, int bits)
    {
        for (var i = bits - 1; i >= 0; i--)
        {
            var bit = (value >> i) & 1;
            if (bit != 0)
                _buf[_bit >> 3] |= (byte)(0x80 >> (_bit & 7));
            _bit++;
        }

        return this;
    }

    public byte[] Build()
    {
        var len = (_bit - 24 + 7) / 8;
        _buf[0] = 0xD3;
        _buf[1] = (byte)((len >> 8) & 0x03);
        _buf[2] = (byte)(len & 0xff);
        var crc = rtcm3.crc24.crc24q(_buf, (uint)(len + 3), 0);
        var frame = new byte[len + 6];
        Array.Copy(_buf, frame, len + 3);
        frame[len + 3] = (byte)(crc >> 16);
        frame[len + 4] = (byte)(crc >> 8);
        frame[len + 5] = (byte)crc;
        return frame;
    }

    /// <summary>RTCM 1005 for a WGS84 position, as a base station sends it.</summary>
    public static byte[] Type1005(double lat, double lng, double alt, int station = 42)
    {
        var ecef = new double[3];
        rtcm3.pos2ecef(new[] { lat * rtcm3.D2R, lng * rtcm3.D2R, alt }, ref ecef);
        return new RtcmFrameBuilder()
            .Put(1005, 12).Put(station, 12).Put(0, 6)
            .Put(1, 1).Put(1, 1).Put(1, 1).Put(0, 1)
            .Put((long)Math.Round(ecef[0] / 0.0001), 38)
            .Put(1, 1).Put(0, 1)
            .Put((long)Math.Round(ecef[1] / 0.0001), 38)
            .Put(0, 2)
            .Put((long)Math.Round(ecef[2] / 0.0001), 38)
            .Build();
    }

    /// <summary>An MSM4 frame (1074 GPS, 1084 GLONASS, 1094 Galileo, 1124 BeiDou...) with one signal per satellite.</summary>
    public static byte[] Msm4(int type, IReadOnlyList<int> prns, IReadOnlyList<int> cnr, int signal = 2)
    {
        var b = new RtcmFrameBuilder()
            .Put(type, 12).Put(42, 12).Put(123456, 30).Put(0, 1).Put(0, 3).Put(0, 7).Put(0, 2).Put(0, 2).Put(0, 1).Put(0, 3);
        long satMask = 0;
        foreach (var p in prns)
            satMask |= 1L << (64 - p);
        b.Put(satMask >> 32, 32).Put(satMask & 0xffffffff, 32);
        b.Put(1L << (32 - signal), 32);
        foreach (var _ in prns)
            b.Put(1, 1); // cell mask: every satellite has the signal
        foreach (var _ in prns)
            b.Put(75, 8); // rough range, ms
        foreach (var _ in prns)
            b.Put(100, 10);
        foreach (var _ in prns)
            b.Put(10, 15); // fine pseudorange
        foreach (var _ in prns)
            b.Put(20, 22); // fine phase range
        foreach (var _ in prns)
            b.Put(5, 4); // lock time
        foreach (var _ in prns)
            b.Put(0, 1);
        foreach (var c in cnr)
            b.Put(c, 6);
        return b.Build();
    }

    /// <summary>A legacy GPS 1004 frame: header plus zeroed satellite blocks.</summary>
    public static byte[] Type1004(int satellites)
    {
        var b = new RtcmFrameBuilder().Put(1004, 12).Put(42, 12).Put(200000, 30).Put(0, 1).Put(satellites, 5).Put(0, 1).Put(0, 3);
        for (var i = 0; i < satellites; i++)
            b.Put(i + 1, 6).Put(0, 64).Put(0, 55);
        return b.Build();
    }

    /// <summary>A frame of the given message number padded to <paramref name="payloadBytes"/>.</summary>
    public static byte[] Padded(int type, int payloadBytes)
    {
        var b = new RtcmFrameBuilder().Put(type, 12);
        for (var i = 12; i < payloadBytes * 8; i += 8)
            b.Put((i / 8) & 0xff, Math.Min(8, payloadBytes * 8 - i));
        return b.Build();
    }
}

/// <summary>ArduPilot's AP_GPS::handle_gps_rtcm_fragment, to check what the vehicle reassembles.</summary>
internal sealed class ArduPilotRtcmReassembler
{
    private const int FragLen = 180;
    private readonly byte[] _buffer = new byte[FragLen * 4];
    private int _received;
    private int _count;
    private int _sequence;
    private int _total;

    public List<byte> Injected { get; } = new();

    public int Discarded { get; private set; }

    public void Handle(MAVLink.mavlink_gps_rtcm_data_t p) => Handle(p.flags, p.data, p.len);

    public void Handle(byte flags, byte[] data, int len)
    {
        if ((flags & 1) == 0)
        {
            Injected.AddRange(data.Take(len));
            return;
        }

        var fragment = (flags >> 1) & 0x03;
        var sequence = (flags >> 3) & 0x1f;
        if (_received != 0 && (_sequence != sequence || (_received & (1 << fragment)) != 0))
        {
            _count = 0;
            Discarded += System.Numerics.BitOperations.PopCount((uint)_received);
            _received = 0;
        }

        _sequence = sequence;
        _received |= 1 << fragment;
        Array.Copy(data, 0, _buffer, FragLen * fragment, len);

        if (len < FragLen)
        {
            _count = fragment + 1;
            _total = FragLen * fragment + len;
        }
        else if (_received == 0x0F)
        {
            _count = 4;
            _total = FragLen * 4;
        }

        if (_count != 0 && _received == (1 << _count) - 1)
        {
            Injected.AddRange(_buffer.Take(_total));
            _count = 0;
            _received = 0;
        }
    }
}

/// <summary>A local NTRIP caster on loopback: one mountpoint that streams frames, a source table for the rest.</summary>
internal sealed class FakeCaster : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;

    public FakeCaster(string mountpoint, Func<byte[]> nextData, bool chunked, bool ntripV1Response = false, string? requiredAuth = null)
    {
        Mountpoint = mountpoint;
        NextData = nextData;
        Chunked = chunked;
        V1Response = ntripV1Response;
        RequiredAuth = requiredAuth;
        _listener.Start();
        _accept = Task.Run(AcceptLoop);
    }

    public string Mountpoint { get; }
    public Func<byte[]> NextData { get; }
    public bool Chunked { get; }
    public bool V1Response { get; }
    public string? RequiredAuth { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Text the client sent after its request (GGA sentences).</summary>
    public ConcurrentQueue<string> Upstream { get; } = new();

    public const string SourceTable =
        "STR;MOUNT1;Canberra;RTCM 3.2;1005(10),1074(1),1084(1);2;GPS+GLO;TEST;AUS;-35.36;149.16;1;0;sNTRIP;none;B;N;5000;\r\n" +
        "STR;FAR;Perth;RTCM 3.3;1077(1);2;GPS;TEST;AUS;-31.95;115.86;0;0;sNTRIP;none;N;N;3000;\r\n" +
        "CAS;caster.test;2101;TEST;Test;0;AUS;-35;149;;\r\n" +
        "NET;TEST;Test;B;N;;;;\r\n" +
        "ENDSOURCETABLE\r\n";

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var header = await ReadHeader(stream);
                Requests.Enqueue(header);
                var path = header.Split(' ')[1].TrimStart('/');

                if (RequiredAuth != null && !header.Contains("Authorization: Basic " + RequiredAuth, StringComparison.Ordinal))
                {
                    await Write(stream, "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"/\"\r\n\r\n");
                    return;
                }

                if (path != Mountpoint)
                {
                    await Write(stream, "SOURCETABLE 200 OK\r\nServer: FakeCaster\r\nContent-Type: text/plain\r\n\r\n" + SourceTable);
                    return;
                }

                await Write(stream, V1Response
                    ? "ICY 200 OK\r\n"
                    : "HTTP/1.1 200 OK\r\nNtrip-Version: Ntrip/2.0\r\nContent-Type: gnss/data\r\n" +
                      (Chunked ? "Transfer-Encoding: chunked\r\n" : "") + "\r\n");

                _ = Task.Run(() => ReadUpstream(stream));
                while (!_stop.IsCancellationRequested && client.Connected)
                {
                    var data = NextData();
                    if (data.Length > 0)
                    {
                        if (Chunked)
                        {
                            await Write(stream, data.Length.ToString("x") + ";ext=1\r\n");
                            await stream.WriteAsync(data);
                            await Write(stream, "\r\n");
                        }
                        else
                        {
                            await stream.WriteAsync(data);
                        }
                    }

                    await Task.Delay(50, _stop.Token);
                }
            }
            catch (Exception)
            {
                // client went away or caster stopped
            }
        }
    }

    private async Task ReadUpstream(NetworkStream stream)
    {
        var buf = new byte[1024];
        var text = new StringBuilder();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buf, _stop.Token);
                if (n == 0)
                    return;
                text.Append(Encoding.ASCII.GetString(buf, 0, n));
                var s = text.ToString();
                int nl;
                while ((nl = s.IndexOf('\n')) >= 0)
                {
                    Upstream.Enqueue(s[..nl].TrimEnd('\r'));
                    s = s[(nl + 1)..];
                }

                text.Clear().Append(s);
            }
        }
        catch (Exception)
        {
        }
    }

    private static async Task<string> ReadHeader(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one) == 0)
                break;
            sb.Append((char)one[0]);
        }

        return sb.ToString();
    }

    private static Task Write(NetworkStream s, string text) => s.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        _stop.Dispose();
    }
}

/// <summary>Inline dispatcher and scripted dialogs for page tests.</summary>
internal sealed class SetupTestUi : IUiDispatcher, IDialogService, IWindowService
{
    public bool ConfirmAnswer { get; set; } = true;
    public string? PromptAnswer { get; set; }
    public ConcurrentQueue<string> Messages { get; } = new();
    public ConcurrentQueue<string> Confirms { get; } = new();

    public AppServices CreateServices() => new(this, this, this);

    public bool CheckAccess() => true;

    public void Post(Action action)
    {
        lock (this)
            action();
    }

    public Task InvokeAsync(Action action)
    {
        Post(action);
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();

    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info)
    {
        Messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK")
    {
        Confirms.Enqueue(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string?> PromptAsync(string title, string message, string initialValue = "") => Task.FromResult(PromptAnswer);
    public Task<string?> OpenFileAsync(string title, params FileFilter[] filters) => Task.FromResult<string?>(null);
    public Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters) => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public void Show(PageViewModel page, double width = 1100, double height = 750) { }
    public Task<bool> ShowDialogAsync(PageViewModel page, double width = 1000, double height = 700) => Task.FromResult(false);
}

internal static class Wait
{
    public static async Task<bool> Until(Func<bool> condition, TimeSpan timeout, Action? poll = null)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            poll?.Invoke();
            if (condition())
                return true;
            await Task.Delay(20);
        }

        poll?.Invoke();
        return condition();
    }
}
