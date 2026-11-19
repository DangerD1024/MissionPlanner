using System.Text;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

namespace MissionPlanner.App.Tests.SetupScreens.Rtk;

[TestClass]
public class NtripTests
{
    [TestMethod]
    public void GgaSentenceHasNmeaFormatAndChecksum()
    {
        var utc = new DateTime(2026, 11, 18, 12, 35, 19, 250, DateTimeKind.Utc);
        var s = NmeaGga.Build(new GgaPosition(-35.363261, 149.16523, 584.2, 4, 18, 0.7), utc);

        Assert.AreEqual("$GPGGA,123519.25,3521.79566,S,14909.91380,E,4,18,0.7,584.2,M,0.0,M,,*", s[..^2]);
        var body = s[1..s.IndexOf('*')];
        Assert.AreEqual(NmeaGga.Checksum(body).ToString("X2"), s[^2..]);

        // north/west and minute rounding that would print 60
        var w = NmeaGga.Build(new GgaPosition(47.9999999999, -122.5, 10), utc);
        StringAssert.Contains(w, ",4800.00000,N,12230.00000,W,1,10,1.0,10.0,M,");
        Assert.AreEqual(2, NmeaGga.QualityFromFixType(4));
        Assert.AreEqual(4, NmeaGga.QualityFromFixType(6));
        Assert.AreEqual(5, NmeaGga.QualityFromFixType(5));
        Assert.AreEqual(1, NmeaGga.QualityFromFixType(3));
    }

    [TestMethod]
    public void RequestsCarryVersionAndBasicAuth()
    {
        var ep = new NtripEndpoint("caster.test", 2101, "MOUNT 1", "user", "p@ss:word");
        var v2 = NtripProtocol.BuildRequest(ep);
        StringAssert.StartsWith(v2, "GET /MOUNT%201 HTTP/1.1\r\n");
        StringAssert.Contains(v2, "Host: caster.test:2101\r\n");
        StringAssert.Contains(v2, "Ntrip-Version: Ntrip/2.0\r\n");
        StringAssert.Contains(v2, "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:p@ss:word")) + "\r\n");
        Assert.EndsWith("\r\n\r\n", v2);

        var v1 = NtripProtocol.BuildRequest(ep with { NtripV1 = true, Username = "", Password = "" }, "");
        StringAssert.StartsWith(v1, "GET / HTTP/1.0\r\n");
        Assert.DoesNotContain("Ntrip-Version", v1);
        Assert.DoesNotContain("Authorization", v1);
    }

    [TestMethod]
    public void UrlsInMissionPlannerFormParse()
    {
        var ep = NtripEndpoint.TryParseUrl("http://me:se%40cret@rtk.example.org:2102/BASE1");
        Assert.IsNotNull(ep);
        Assert.AreEqual("rtk.example.org", ep.Host);
        Assert.AreEqual(2102, ep.Port);
        Assert.AreEqual("BASE1", ep.Mountpoint);
        Assert.AreEqual("me", ep.Username);
        Assert.AreEqual("se@cret", ep.Password);
        Assert.IsFalse(ep.UseTls);

        var tls = NtripEndpoint.TryParseUrl("https://caster.example.org/M");
        Assert.AreEqual(443, tls!.Port);
        Assert.IsTrue(tls.UseTls);
        Assert.AreEqual(2101, NtripEndpoint.TryParseUrl("ntrip://caster.example.org/M")!.Port);
        Assert.IsNull(NtripEndpoint.TryParseUrl(""));
    }

    [TestMethod]
    public void ResponseHeadersAreClassified()
    {
        var icy = Encoding.ASCII.GetBytes("ICY 200 OK\r\n").Concat(new byte[] { 0xD3, 0, 1 }).ToArray();
        Assert.IsTrue(NtripProtocol.TryParseHeader(icy, out var h, out var len));
        Assert.AreEqual(NtripResponseKind.Stream, h.Kind);
        Assert.AreEqual(12, len, "the stream starts right after the status line");

        var http = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nNtrip-Version: Ntrip/2.0\r\nTransfer-Encoding: chunked\r\n\r\n10\r\n");
        Assert.IsTrue(NtripProtocol.TryParseHeader(http, out h, out len));
        Assert.AreEqual(NtripResponseKind.Stream, h.Kind);
        Assert.IsTrue(h.Chunked);
        Assert.AreEqual(http.Length - 4, len);

        Assert.IsFalse(NtripProtocol.TryParseHeader(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nNtrip-Ver"), out _, out _),
            "an incomplete HTTP header needs more bytes");
        Assert.IsFalse(NtripProtocol.TryParseHeader(Encoding.ASCII.GetBytes("ICY 20"), out _, out _));

        Assert.IsTrue(NtripProtocol.TryParseHeader("SOURCETABLE 200 OK\r\nServer: x\r\n"u8, out h, out _));
        Assert.AreEqual(NtripResponseKind.SourceTable, h.Kind);
        StringAssert.Contains(h.Explain("NOPE"), "NOPE");

        Assert.IsTrue(NtripProtocol.TryParseHeader("HTTP/1.1 200 OK\r\nContent-Type: gnss/sourcetable\r\n\r\n"u8, out h, out _));
        Assert.AreEqual(NtripResponseKind.SourceTable, h.Kind);

        Assert.IsTrue(NtripProtocol.TryParseHeader("HTTP/1.0 401 Unauthorized\r\n\r\n"u8, out h, out _));
        Assert.AreEqual(NtripResponseKind.Unauthorized, h.Kind);
        Assert.IsTrue(NtripProtocol.TryParseHeader("ERROR - Bad Password\r\n"u8, out h, out _));
        Assert.AreEqual(NtripResponseKind.Unauthorized, h.Kind);
        Assert.IsTrue(NtripProtocol.TryParseHeader("HTTP/1.1 404 Not Found\n\n"u8, out h, out _));
        Assert.AreEqual(NtripResponseKind.NotFound, h.Kind);
    }

    [TestMethod]
    public void SourceTableListsMountpoints()
    {
        var list = NtripProtocol.ParseSourceTable(FakeCaster.SourceTable);
        Assert.HasCount(2, list);
        var m = list[0];
        Assert.AreEqual("MOUNT1", m.Mountpoint);
        Assert.AreEqual("Canberra", m.Identifier);
        Assert.AreEqual("RTCM 3.2", m.Format);
        Assert.AreEqual("GPS+GLO", m.NavSystem);
        Assert.AreEqual("AUS", m.Country);
        Assert.AreEqual(-35.36, m.Lat, 1e-9);
        Assert.AreEqual(149.16, m.Lng, 1e-9);
        Assert.IsTrue(m.NeedsGga);
        Assert.AreEqual(5000, m.Bitrate);
        Assert.IsFalse(list[1].NeedsGga);
    }

    [TestMethod]
    public void ChunkedDecoderHandlesSplitsAndExtensions()
    {
        var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var wire = new List<byte>();
        wire.AddRange("12C;name=val\r\n"u8.ToArray());
        wire.AddRange(payload);
        wire.AddRange("\r\n0\r\nX-Trailer: 1\r\n\r\n"u8.ToArray());

        foreach (var step in new[] { 1, 5, wire.Count })
        {
            var dec = new HttpChunkedDecoder();
            var output = new List<byte>();
            for (var i = 0; i < wire.Count; i += step)
                dec.Decode(wire.Skip(i).Take(step).ToArray(), output);
            CollectionAssert.AreEqual(payload, output, "step " + step);
            Assert.IsTrue(dec.IsComplete);
        }

        Assert.ThrowsExactly<InvalidDataException>(() => new HttpChunkedDecoder().Decode("zz\r\n"u8, new List<byte>()));
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ClientStreamsChunkedNtrip2AndSendsGga()
    {
        var frames = new[] { RtcmFrameBuilder.Type1005(-35.36, 149.16, 580), RtcmFrameBuilder.Msm4(1074, [1, 2, 3], [40, 41, 42]) };
        var i = 0;
        await using var caster = new FakeCaster("MOUNT1", () => frames[i++ % 2], chunked: true,
            requiredAuth: Convert.ToBase64String("u:p"u8.ToArray()));

        NtripRtcmSource.GgaInterval = TimeSpan.FromMilliseconds(200);
        var source = new NtripRtcmSource(new NtripEndpoint("127.0.0.1", caster.Port, "MOUNT1", "u", "p"),
            () => new GgaPosition(-35.36, 149.16, 580));
        await using (source)
        {
            await source.OpenAsync(CancellationToken.None);

            var framer = new RtcmFramer();
            var got = new List<RtcmFrame>();
            var buf = new byte[1024];
            while (got.Count < 4)
            {
                var n = await source.ReadAsync(buf, CancellationToken.None);
                framer.Push(buf.AsSpan(0, n), got);
            }

            Assert.AreEqual(0, framer.CrcErrors, "chunk headers never reach the RTCM parser");
            Assert.AreEqual(0, framer.OtherBytes);
            CollectionAssert.AreEqual(new[] { 1005, 1074, 1005, 1074 }, got.Select(f => f.MessageType).ToArray());

            Assert.IsTrue(await Wait.Until(() => caster.Upstream.Count >= 2, TimeSpan.FromSeconds(5)), "GGA repeated");
            Assert.IsTrue(caster.Upstream.All(l => l.StartsWith("$GPGGA,", StringComparison.Ordinal)));
            Assert.IsGreaterThanOrEqualTo(2, source.GgaSent);
        }

        StringAssert.Contains(caster.Requests.First(), "Ntrip-Version: Ntrip/2.0");
        NtripRtcmSource.GgaInterval = TimeSpan.FromSeconds(10);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ClientReadsNtrip1IcyStream()
    {
        var frame = RtcmFrameBuilder.Msm4(1084, [5, 6], [40, 40]);
        await using var caster = new FakeCaster("M", () => frame, chunked: false, ntripV1Response: true);
        await using var client = new NtripClient(new NtripEndpoint("127.0.0.1", caster.Port, "M", NtripV1: true));
        await client.ConnectAsync(CancellationToken.None);

        var framer = new RtcmFramer();
        var got = new List<RtcmFrame>();
        var buf = new byte[256];
        while (got.Count < 2)
        {
            var n = await client.ReadAsync(buf, CancellationToken.None);
            framer.Push(buf.AsSpan(0, n), got);
        }
        Assert.IsTrue(got.All(f => f.MessageType == 1084));
        StringAssert.StartsWith(caster.Requests.First(), "GET /M HTTP/1.0");
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task UnknownMountpointAndBadPasswordAreFatal()
    {
        await using var caster = new FakeCaster("GOOD", () => [], chunked: false);
        var bad = await Assert.ThrowsExactlyAsync<NtripException>(() =>
            new NtripClient(new NtripEndpoint("127.0.0.1", caster.Port, "BAD")).ConnectAsync(CancellationToken.None));
        Assert.IsTrue(bad.Fatal);
        StringAssert.Contains(bad.Message, "BAD");

        await using var locked = new FakeCaster("GOOD", () => [], chunked: false, requiredAuth: "nope");
        var denied = await Assert.ThrowsExactlyAsync<NtripException>(() =>
            new NtripClient(new NtripEndpoint("127.0.0.1", locked.Port, "GOOD", "a", "b")).ConnectAsync(CancellationToken.None));
        Assert.IsTrue(denied.Fatal);
        StringAssert.Contains(denied.Message, "password");
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SourceTableDownloads()
    {
        await using var caster = new FakeCaster("MOUNT1", () => [], chunked: false);
        var list = await NtripClient.GetSourceTableAsync(new NtripEndpoint("127.0.0.1", caster.Port), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "MOUNT1", "FAR" }, list.Select(m => m.Mountpoint).ToArray());
    }
}
