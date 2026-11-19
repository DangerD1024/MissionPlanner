using System.Globalization;
using System.Text;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

/// <summary>Where to get corrections from. An empty mountpoint asks the caster for its source table.</summary>
public sealed record NtripEndpoint(
    string Host,
    int Port = 2101,
    string Mountpoint = "",
    string Username = "",
    string Password = "",
    bool UseTls = false,
    bool NtripV1 = false)
{
    /// <summary>
    /// Reads Mission Planner's NTRIP URL form, <c>http(s)://user:pass@host:port/mount</c> (also
    /// <c>ntrip://</c>). Returns null when it is not a URL.
    /// </summary>
    public static NtripEndpoint? TryParseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        url = url.Trim();
        if (url.StartsWith("ntrip://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url["ntrip://".Length..];
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return null;

        var tls = uri.Scheme == Uri.UriSchemeHttps;
        var port = uri.IsDefaultPort ? (tls ? 443 : 2101) : uri.Port;
        string user = "", pass = "";
        if (uri.UserInfo.Length > 0)
        {
            var colon = uri.UserInfo.IndexOf(':');
            user = Uri.UnescapeDataString(colon < 0 ? uri.UserInfo : uri.UserInfo[..colon]);
            pass = colon < 0 ? "" : Uri.UnescapeDataString(uri.UserInfo[(colon + 1)..]);
        }

        return new NtripEndpoint(uri.Host, port, Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')), user, pass, tls);
    }
}

public enum NtripResponseKind
{
    /// <summary>The caster accepted the mountpoint; correction data follows.</summary>
    Stream,

    /// <summary>The caster sent its source table (no or unknown mountpoint).</summary>
    SourceTable,

    Unauthorized,
    NotFound,
    Error,
}

public sealed record NtripResponseHeader(NtripResponseKind Kind, int StatusCode, string StatusLine, bool Chunked, string? ContentType)
{
    public string Explain(string mountpoint) => Kind switch
    {
        NtripResponseKind.Stream => "Connected",
        NtripResponseKind.SourceTable => string.IsNullOrEmpty(mountpoint)
            ? "No mountpoint given; the caster sent its source table. Pick a mountpoint from the list."
            : $"The caster has no mountpoint \"{mountpoint}\" (it sent its source table instead). Check the name, or load the list.",
        NtripResponseKind.Unauthorized => "The caster refused the username or password (" + StatusLine + ").",
        NtripResponseKind.NotFound => $"The caster has no mountpoint \"{mountpoint}\" ({StatusLine}).",
        _ => "The caster answered: " + StatusLine,
    };
}

/// <summary>An NTRIP failure; <see cref="Fatal"/> ones (bad mountpoint, password) are not retried.</summary>
public sealed class NtripException(string message, bool fatal, Exception? inner = null) : Exception(message, inner)
{
    public bool Fatal { get; } = fatal;
}

/// <summary>A mountpoint ("STR") record of a caster's source table.</summary>
public sealed record NtripMountpoint(
    string Mountpoint,
    string Identifier,
    string Format,
    string FormatDetails,
    string NavSystem,
    string Network,
    string Country,
    double Lat,
    double Lng,
    bool NeedsGga,
    string Generator,
    string Authentication,
    int Bitrate)
{
    public bool HasPosition => Lat != 0 || Lng != 0;
}

/// <summary>NTRIP 1/2 request and response handling, independent of the socket.</summary>
public static class NtripProtocol
{
    public const string UserAgent = "NTRIP MissionPlanner/2.0";

    public static string BuildRequest(NtripEndpoint ep, string? mountpoint = null)
    {
        var mount = mountpoint ?? ep.Mountpoint;
        var path = "/" + Uri.EscapeDataString(mount.TrimStart('/')).Replace("%2F", "/", StringComparison.Ordinal);
        var sb = new StringBuilder();
        if (ep.NtripV1)
        {
            sb.Append("GET ").Append(path).Append(" HTTP/1.0\r\n");
            sb.Append("User-Agent: ").Append(UserAgent).Append("\r\n");
        }
        else
        {
            sb.Append("GET ").Append(path).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(ep.Host).Append(':').Append(ep.Port.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Ntrip-Version: Ntrip/2.0\r\n");
            sb.Append("User-Agent: ").Append(UserAgent).Append("\r\n");
        }

        if (ep.Username.Length > 0 || ep.Password.Length > 0)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(ep.Username + ":" + ep.Password));
            sb.Append("Authorization: Basic ").Append(token).Append("\r\n");
        }

        sb.Append("Connection: close\r\n\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Finds the end of the caster's response header in the first bytes received. NTRIP 1 casters
    /// answer a single line ("ICY 200 OK") and start streaming; NTRIP 2 and errors are HTTP with
    /// header fields and a blank line. Returns false while more bytes are needed.
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out NtripResponseHeader header, out int headerLength)
    {
        header = null!;
        headerLength = 0;

        var firstEnd = data.IndexOf((byte)'\n');
        if (firstEnd < 0)
            return false;

        var first = Encoding.ASCII.GetString(data[..firstEnd]).Trim();
        var isHttp = first.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (isHttp)
        {
            var end = IndexOfBlankLine(data, out var sepLen);
            if (end < 0)
                return false;
            headerLength = end + sepLen;
            var lines = Encoding.ASCII.GetString(data[..end]).Split('\n');
            foreach (var raw in lines.Skip(1))
            {
                var line = raw.TrimEnd('\r');
                var colon = line.IndexOf(':');
                if (colon > 0)
                    fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }
        else
        {
            headerLength = firstEnd + 1;
        }

        header = Classify(first, fields);
        return true;
    }

    private static NtripResponseHeader Classify(string first, Dictionary<string, string> fields)
    {
        fields.TryGetValue("Content-Type", out var contentType);
        var chunked = fields.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase);

        if (first.StartsWith("ICY 200", StringComparison.OrdinalIgnoreCase))
            return new(NtripResponseKind.Stream, 200, first, false, contentType);
        if (first.StartsWith("SOURCETABLE 200", StringComparison.OrdinalIgnoreCase))
            return new(NtripResponseKind.SourceTable, 200, first, false, contentType);
        if (first.Contains("Bad Password", StringComparison.OrdinalIgnoreCase) ||
            first.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return new(NtripResponseKind.Unauthorized, 401, first, false, contentType);

        if (first.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = first.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var code = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 0;
            if (code == 200)
            {
                var table = contentType != null && contentType.Contains("sourcetable", StringComparison.OrdinalIgnoreCase);
                return new(table ? NtripResponseKind.SourceTable : NtripResponseKind.Stream, code, first, chunked, contentType);
            }

            var kind = code switch
            {
                401 or 403 => NtripResponseKind.Unauthorized,
                404 => NtripResponseKind.NotFound,
                _ => NtripResponseKind.Error,
            };
            return new(kind, code, first, chunked, contentType);
        }

        return new(NtripResponseKind.Error, 0, first.Length > 0 ? first : "(empty response)", false, contentType);
    }

    private static int IndexOfBlankLine(ReadOnlySpan<byte> data, out int separatorLength)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != '\n')
                continue;
            // "\n\r\n" ends a CRLF header, "\n\n" a bare-LF one
            if (i + 2 < data.Length && data[i + 1] == '\r' && data[i + 2] == '\n')
            {
                separatorLength = 3;
                return i;
            }

            if (i + 1 < data.Length && data[i + 1] == '\n')
            {
                separatorLength = 2;
                return i;
            }
        }

        separatorLength = 0;
        return -1;
    }

    /// <summary>Mountpoints ("STR;" lines) of a source table; other records are ignored.</summary>
    public static List<NtripMountpoint> ParseSourceTable(string text)
    {
        var list = new List<NtripMountpoint>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("STR;", StringComparison.OrdinalIgnoreCase))
                continue;
            var f = line.Split(';');
            string F(int i) => i < f.Length ? f[i].Trim() : "";
            double D(int i) => double.TryParse(F(i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            int I(int i) => int.TryParse(F(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

            if (F(1).Length == 0)
                continue;
            list.Add(new NtripMountpoint(
                Mountpoint: F(1),
                Identifier: F(2),
                Format: F(3),
                FormatDetails: F(4),
                NavSystem: F(6),
                Network: F(7),
                Country: F(8),
                Lat: D(9),
                Lng: D(10),
                NeedsGga: F(11) == "1",
                Generator: F(13),
                Authentication: F(15),
                Bitrate: I(17)));
        }

        return list;
    }
}

/// <summary>Removes HTTP/1.1 chunked transfer encoding (NTRIP 2 casters may use it for the stream).</summary>
public sealed class HttpChunkedDecoder
{
    private enum State
    {
        Size,
        SizeExtension,
        SizeLf,
        Data,
        DataCr,
        DataLf,
        Trailer,
        Done,
    }

    private State _state = State.Size;
    private long _remaining;
    private bool _sawDigit;
    private bool _trailerLineEmpty = true;

    /// <summary>True once the terminating zero-size chunk and trailer were read.</summary>
    public bool IsComplete => _state == State.Done;

    /// <summary>Decodes <paramref name="input"/>; payload bytes are written to <paramref name="output"/>.</summary>
    public void Decode(ReadOnlySpan<byte> input, List<byte> output)
    {
        foreach (var b in input)
        {
            switch (_state)
            {
                case State.Size:
                    var hex = HexValue(b);
                    if (hex >= 0)
                    {
                        _remaining = _remaining * 16 + hex;
                        _sawDigit = true;
                        if (_remaining > int.MaxValue)
                            throw new InvalidDataException("chunk size too large");
                    }
                    else if (b == ';' || b == ' ' || b == '\t')
                    {
                        _state = State.SizeExtension;
                    }
                    else if (b == '\r')
                    {
                        _state = State.SizeLf;
                    }
                    else if (b == '\n')
                    {
                        EndSize();
                    }
                    else
                    {
                        throw new InvalidDataException("bad chunk size");
                    }

                    break;
                case State.SizeExtension:
                    if (b == '\r')
                        _state = State.SizeLf;
                    else if (b == '\n')
                        EndSize();
                    break;
                case State.SizeLf:
                    if (b != '\n')
                        throw new InvalidDataException("bad chunk header");
                    EndSize();
                    break;
                case State.Data:
                    output.Add(b);
                    if (--_remaining == 0)
                        _state = State.DataCr;
                    break;
                case State.DataCr:
                    if (b == '\r')
                        _state = State.DataLf;
                    else if (b == '\n')
                        _state = State.Size;
                    else
                        throw new InvalidDataException("chunk not terminated");
                    break;
                case State.DataLf:
                    if (b != '\n')
                        throw new InvalidDataException("chunk not terminated");
                    _state = State.Size;
                    break;
                case State.Trailer:
                    if (b == '\n')
                    {
                        if (_trailerLineEmpty)
                            _state = State.Done;
                        _trailerLineEmpty = true;
                    }
                    else if (b != '\r')
                    {
                        _trailerLineEmpty = false;
                    }

                    break;
                case State.Done:
                    return;
            }
        }
    }

    private void EndSize()
    {
        if (!_sawDigit)
            throw new InvalidDataException("missing chunk size");
        _sawDigit = false;
        if (_remaining == 0)
        {
            _state = State.Trailer;
            _trailerLineEmpty = true;
        }
        else
        {
            _state = State.Data;
        }
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
