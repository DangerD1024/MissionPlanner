using System.Text.RegularExpressions;
using GMap.NET.MapProviders;

namespace MissionPlanner.App.Core.Maps;

/// <summary>
/// The current Google tile versions. GMap.NET looks them up itself (over plain http, with
/// WebRequest), but that fails on iOS, and its built-in satellite version is retired (the server
/// answers 404). This asks the same page over https and sets the versions it lists.
/// </summary>
public static partial class GoogleVersions
{
    private const string Page = "https://maps.google.com/maps/api/js?v=3.2&sensor=false";
    private static int _started;

    /// <summary>Runs the lookup once per process; failures leave the versions as they are.</summary>
    public static Task RefreshOnceAsync() =>
        Interlocked.Exchange(ref _started, 1) == 0 ? RefreshAsync() : Task.CompletedTask;

    private static async Task RefreshAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(GMapProvider.UserAgent ?? "MissionPlanner");
            Apply(Parse(await http.GetStringAsync(Page).ConfigureAwait(false)));
        }
        catch (Exception)
        {
            // offline or blocked: GMap.NET's own lookup or the cached tiles still apply
        }
    }

    /// <summary>The versions on the Maps API page: satellite (kh?v=N), road (m@N), labels (h@N).</summary>
    public static (string? Satellite, string? Road, string? Labels) Parse(string html) =>
        (Match(SatellitePattern(), html), Match(RoadPattern(), html) is { } m ? "m@" + m : null,
            Match(LabelsPattern(), html) is { } h ? "h@" + h : null);

    public static void Apply((string? Satellite, string? Road, string? Labels) v)
    {
        if (v.Satellite != null)
        {
            GMapProviders.GoogleSatelliteMap.Version = v.Satellite;
            GMapProviders.GoogleKoreaSatelliteMap.Version = v.Satellite;
            GMapProviders.GoogleChinaSatelliteMap.Version = "s@" + v.Satellite;
        }

        if (v.Road != null)
        {
            GMapProviders.GoogleMap.Version = v.Road;
            GMapProviders.GoogleChinaMap.Version = v.Road;
        }

        if (v.Labels != null)
        {
            GMapProviders.GoogleHybridMap.Version = v.Labels;
            GMapProviders.GoogleChinaHybridMap.Version = v.Labels;
        }
    }

    private static string? Match(Regex regex, string html) =>
        regex.Match(html) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"https?://khms?\D?\d\.googleapis\.com/kh\?v=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SatellitePattern();

    [GeneratedRegex(@"https?://mts?\D?\d\.[^/]*/vt\?lyrs=m@(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RoadPattern();

    [GeneratedRegex(@"https?://mts?\D?\d\.[^/]*/vt\?lyrs=h@(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LabelsPattern();
}
