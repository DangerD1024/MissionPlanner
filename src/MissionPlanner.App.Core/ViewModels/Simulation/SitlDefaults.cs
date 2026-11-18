using System.Text;
using System.Text.RegularExpressions;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MissionPlanner.App.Core.ViewModels.Simulation;

/// <summary>
/// The default parameter files ArduPilot's autotest uses for a SITL model (SITL.cs GetDefaultConfig):
/// looked up in sim_vehicle.py (older ArduPilot) or vehicleinfo.py, downloaded from the
/// ArduPilot repository and cached in the sitl folder.
/// </summary>
public static partial class SitlDefaults
{
    public const string SimVehicleUrl = "https://raw.githubusercontent.com/ArduPilot/ardupilot/master/Tools/autotest/sim_vehicle.py";
    public const string VehicleInfoUrl = "https://firmware.ardupilot.org/Tools/MissionPlanner/vehicleinfo.py";
    public const string AutotestBaseUrl = "https://raw.githubusercontent.com/ArduPilot/ardupilot/master/Tools/autotest/";

    /// <summary>Cached files younger than this are used without asking the server.</summary>
    public static TimeSpan CacheAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Old sim_vehicle.py kept the frame table inline; SITL.cs matches it with this regex.</summary>
    [GeneratedRegex("\"([^\"]+)\"\\s*:\\s*\\{\\s*[^\\{}]+\"default_params_filename\"\\s*:\\s*\\[*\"([^\"]+)\"\\s*[^\\}]*\\}")]
    private static partial Regex SimVehicleRegex();

    /// <summary>Parameter file for a model from sim_vehicle.py's inline table (first file only, as SITL.cs).</summary>
    public static IReadOnlyList<string> ParseSimVehicle(string text, string model)
    {
        foreach (Match m in SimVehicleRegex().Matches(text))
        {
            if (m.Groups[1].Value.Equals(model, StringComparison.OrdinalIgnoreCase))
                return [m.Groups[2].Value];
        }

        return [];
    }

    /// <summary>
    /// Parameter files for a model from vehicleinfo.py: the first vehicle whose "frames" has the
    /// model; "default_params_filename" is a string or a list. The Python dict is made JSON the way
    /// SITL.cs cleanupJson does (outer braces, comments dropped, True/False quoted).
    /// </summary>
    public static IReadOnlyList<string> ParseVehicleInfo(string text, string model)
    {
        var json = BraceMatch(text);
        if (json.Length == 0)
            return [];
        json = CommentRegex().Replace(json, "");
        json = TrueRegex().Replace(json, "\"True\"");
        json = FalseRegex().Replace(json, "\"False\"");

        JObject root;
        try
        {
            root = JObject.Parse(json, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
        }
        catch (JsonException)
        {
            return [];
        }

        foreach (var vehicle in root.Properties())
        {
            if (vehicle.Value["frames"] is not JObject frames || frames[model] is not JObject frame)
                continue;
            return frame["default_params_filename"] switch
            {
                JValue v when v.Type == JTokenType.String => [(string)v!],
                JArray a => a.Select(t => (string?)t).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList(),
                _ => [],
            };
        }

        return [];
    }

    [GeneratedRegex("#[^\\n]*")]
    private static partial Regex CommentRegex();

    [GeneratedRegex("\\bTrue\\b")]
    private static partial Regex TrueRegex();

    [GeneratedRegex("\\bFalse\\b")]
    private static partial Regex FalseRegex();

    /// <summary>The first balanced {...} block.</summary>
    internal static string BraceMatch(string text)
    {
        var level = 0;
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                if (level == 0)
                    start = i;
                level++;
            }
            else if (c == '}' && level > 0)
            {
                level--;
                if (level == 0)
                    return text.Substring(start, i - start + 1);
            }
        }

        return "";
    }

    /// <summary>
    /// Resolves (and downloads into <paramref name="sitlDir"/>) the defaults file for a model.
    /// Several files are joined into one, as SITL.cs does. Null when none is known or none could be fetched.
    /// </summary>
    public static async Task<string?> ResolveAsync(HttpClient http, string sitlDir, string model, Action<string>? log,
        CancellationToken cancel)
    {
        var files = new List<string>();

        var simVehicle = await CachedAsync(http, new Uri(SimVehicleUrl), Path.Combine(sitlDir, "sim_vehicle.py"), log, cancel);
        if (simVehicle != null)
            files.AddRange(ParseSimVehicle(await File.ReadAllTextAsync(simVehicle, cancel), model));

        if (files.Count == 0)
        {
            var info = await CachedAsync(http, new Uri(VehicleInfoUrl), Path.Combine(sitlDir, "vehicleinfo.py"), log, cancel);
            if (info != null)
                files.AddRange(ParseVehicleInfo(await File.ReadAllTextAsync(info, cancel), model));
        }

        if (files.Count == 0)
        {
            log?.Invoke($"No default parameter file is listed for model '{model}'.");
            return null;
        }

        var local = new List<string>();
        foreach (var rel in files)
        {
            if (rel.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                continue;
            var path = Path.Combine(sitlDir, rel.Replace('/', Path.DirectorySeparatorChar));
            var got = await CachedAsync(http, new Uri(AutotestBaseUrl + rel), path, log, cancel);
            if (got != null)
                local.Add(got);
        }

        if (local.Count == 0)
            return null;
        if (local.Count == 1)
            return local[0];

        var joined = Path.Combine(sitlDir, "defaults-" + SitlArguments.SafeName(model).Replace("+", "plus") + ".parm");
        var sb = new StringBuilder();
        foreach (var f in local)
            sb.Append("\r\n").Append(await File.ReadAllTextAsync(f, cancel));
        await File.WriteAllTextAsync(joined, sb.ToString(), cancel);
        return joined;
    }

    /// <summary>A file from the cache when fresh, else downloaded; a stale copy beats nothing when offline.</summary>
    private static async Task<string?> CachedAsync(HttpClient http, Uri url, string path, Action<string>? log, CancellationToken cancel)
    {
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < CacheAge)
            return path;
        try
        {
            await FileDownloader.DownloadAsync(http, url, path, null, cancel, TimeSpan.FromSeconds(15));
            return path;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (File.Exists(path))
            {
                log?.Invoke($"Could not refresh {Path.GetFileName(path)} ({ex.Message}); using the cached copy.");
                return path;
            }

            log?.Invoke($"Could not download {url}: {ex.Message}");
            return null;
        }
    }
}
