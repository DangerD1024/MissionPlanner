using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Params;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>The outcome of a documentation download: the files written and the ones that failed.</summary>
public sealed record ParamDocsResult(IReadOnlyList<string> Updated, IReadOnlyList<string> Failed);

/// <summary>
/// Downloads ArduPilot's parameter documentation (apm.pdef.xml) from autotest.ardupilot.org into the
/// data folder, now, whatever its age: the files and names <see cref="ParameterMetaDataRepositoryAPMpdef"/>
/// uses (which only fetches them when they are a week old).
/// </summary>
public static class ParamDocsDownload
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ParamDocsDownload));

    /// <summary>The library's list of vehicle files.</summary>
    public static IReadOnlyList<string> Vehicles { get; } =
        ["SITL", "AP_Periph", "ArduSub", "Rover", "ArduCopter", "ArduPlane", "AntennaTracker", "Blimp", "Heli"];

    public const string UrlFormat = "https://autotest.ardupilot.org/Parameters/{0}/apm.pdef.xml.gz";

    public const string VersionedUrlFormat = "https://autotest.ardupilot.org/Parameters/versioned/{0}/stable-{1}/apm.pdef.xml";

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var agent = Settings.Instance.UserAgent;
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", string.IsNullOrEmpty(agent) ? "MissionPlanner" : agent);
        return client;
    });

    /// <summary>Downloads and unpacks &lt;vehicle&gt;.apm.pdef.xml(.gz) for each vehicle into a folder.</summary>
    public static async Task<ParamDocsResult> DownloadAsync(string directory, IEnumerable<string> vehicles,
        IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(directory);
        var updated = new List<string>();
        var failed = new List<string>();
        foreach (var vehicle in vehicles)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(vehicle);
            var gz = Path.Combine(directory, vehicle + ".apm.pdef.xml.gz");
            var xml = Path.Combine(directory, vehicle + ".apm.pdef.xml");
            try
            {
                var bytes = await Http.Value.GetByteArrayAsync(string.Format(CultureInfo.InvariantCulture, UrlFormat, vehicle), cancel).ConfigureAwait(false);
                await using (var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
                    await ReplaceAsync(xml, input, cancel).ConfigureAwait(false);
                await File.WriteAllBytesAsync(gz, bytes, cancel).ConfigureAwait(false);
                updated.Add(vehicle);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                Log.Warn("parameter documentation " + vehicle, ex);
                failed.Add(vehicle);
            }
        }

        return new ParamDocsResult(updated, failed);
    }

    /// <summary>The documentation of one firmware release (Copter 4.5.7...): &lt;Prefix&gt;&lt;version&gt;.apm.pdef.xml.</summary>
    public static async Task<bool> DownloadVersionedAsync(string directory, string prefix, Version version, CancellationToken cancel = default)
    {
        var text = version.ToString(3);
        var file = Path.Combine(directory, prefix + text + ".apm.pdef.xml");
        try
        {
            using var response = await Http.Value.GetAsync(string.Format(CultureInfo.InvariantCulture, VersionedUrlFormat, prefix, text), cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;
            await using var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await ReplaceAsync(file, input, cancel).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            Log.Warn("parameter documentation " + prefix + text, ex);
            return false;
        }
    }

    /// <summary>Writes to a temporary file, checks it parses with parameters in it, then replaces the old file.</summary>
    private static async Task ReplaceAsync(string path, Stream content, CancellationToken cancel)
    {
        var temp = path + ".download";
        await using (var output = File.Create(temp))
            await content.CopyToAsync(output, cancel).ConfigureAwait(false);
        try
        {
            if (ParamMetadata.ParsePdef(temp).Count == 0)
                throw new InvalidDataException("no parameters in " + Path.GetFileName(path));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>The versioned file name prefix for a firmware ("Copter", "Plane"...), or null.</summary>
    public static string? VersionedPrefix(Firmwares firmware) => firmware switch
    {
        Firmwares.ArduPlane => "Plane",
        Firmwares.ArduCopter2 => "Copter",
        Firmwares.ArduRover => "Rover",
        Firmwares.ArduSub => "Sub",
        Firmwares.ArduTracker => "Tracker",
        _ => null,
    };
}

/// <summary>A documentation file in the data folder.</summary>
public sealed record ParamDocsFile(string Name, string Updated, string Size);

/// <summary>
/// Mission Planner's "Param gen" (Ctrl+F): fetch the parameter documentation the app uses again, now.
/// Download takes ArduPilot's current apm.pdef.xml files; Generate from source is the old button
/// itself, which reads Parameters.cpp of every vehicle (master, then stable) and writes
/// ParameterMetaData.xml.
/// </summary>
public sealed partial class ParamGenViewModel(AppServices services)
    : ToolPageViewModel("Parameter documentation",
        "Refresh the parameter descriptions, ranges and options the app shows: download them from ardupilot.org now instead of waiting for the weekly check.",
        services)
{
    /// <summary>The ParameterLocationsBleeding and ParameterLocations lists of Mission Planner's app.config, in that order.</summary>
    public const string SourceUrls =
        "https://raw.oborne.me/ardupilot/ardupilot/master/ArduCopter/Parameters.cpp;https://raw.oborne.me/ardupilot/ardupilot/master/ArduSub/Parameters.cpp;" +
        "https://raw.oborne.me/ardupilot/ardupilot/master/ArduPlane/Parameters.cpp;https://raw.oborne.me/ardupilot/ardupilot/master/APMrover2/Parameters.cpp;" +
        "https://raw.oborne.me/ardupilot/ardupilot/master/Rover/Parameters.cpp;https://raw.oborne.me/ardupilot/ardupilot/master/AntennaTracker/Parameters.cpp;" +
        "https://raw.oborne.me/ardupilot/ardupilot/ArduCopter-stable/ArduCopter/Parameters.cpp;https://raw.oborne.me/ardupilot/ardupilot/ArduSub-stable/ArduSub/Parameters.cpp;" +
        "https://raw.oborne.me/ardupilot/ardupilot/ArduPlane-stable/ArduPlane/Parameters.cpp;https://raw.oborne.me/ardupilot/ardupilot/APMrover2-stable/APMrover2/Parameters.cpp;" +
        "https://raw.oborne.me/ardupilot/ardupilot/Rover-stable/Rover/Parameters.cpp;https://raw.oborne.me/ardupilot/ardupilot/master/AntennaTracker/Parameters.cpp;";

    public override bool NeedsVehicle => false;

    public ObservableCollection<ParamDocsFile> Files { get; } = new();

    public string Folder => Settings.GetDataDirectory();

    [ObservableProperty]
    private string _progressText = "";

    public override void OnShown()
    {
        base.OnShown();
        ListFiles();
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Status = "Downloading the parameter documentation…";
        StatusIsError = false;
        try
        {
            var dir = Settings.GetDataDirectory();
            var progress = new Progress<string>(v => ProgressText = v);
            var result = await ParamDocsDownload.DownloadAsync(dir, ParamDocsDownload.Vehicles, progress);

            // the release the vehicle runs, as the parameter screens look for it first
            var cs = Services.Session.Cs;
            if (IsConnected && cs.version is { Build: >= 0 } version && ParamDocsDownload.VersionedPrefix(cs.firmware) is { } prefix)
            {
                ProgressText = prefix + " " + version.ToString(3);
                await ParamDocsDownload.DownloadVersionedAsync(dir, prefix, version);
            }

            await ApplyAsync();
            if (result.Updated.Count == 0)
                Report("Could not download the parameter documentation (no internet connection?).", true);
            else if (result.Failed.Count > 0)
                Report(L.Format("Updated {0}; could not download {1}.", string.Join(", ", result.Updated), string.Join(", ", result.Failed)), true);
            else
                Report(L.Format("Parameter documentation updated for {0}.", string.Join(", ", result.Updated)));
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    /// <summary>The old button: parse Parameters.cpp and the libraries it names, write ParameterMetaData.xml.</summary>
    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (IsBusy)
            return;
        if (!await Services.Dialogs.ConfirmAsync("Generate from source",
                "Read the parameter comments from ArduPilot's source code (master, then the stable releases) and write ParameterMetaData.xml? This makes hundreds of requests and takes several minutes. The app uses it only for vehicles without downloaded documentation.",
                "Generate"))
            return;

        IsBusy = true;
        Status = "Reading the ArduPilot sources (several minutes)…";
        StatusIsError = false;
        try
        {
            await Task.Run(() => ParameterMetaDataParser.GetParameterInformation(SourceUrls));
            ParameterMetaDataRepositoryAPM.Reload();
            await ApplyAsync();
            var file = Settings.GetUserDataDirectory() + ParamMetadata.GeneratedFileName;
            Report(L.Format("Wrote {0}", file));
        }
        catch (Exception ex)
        {
            Report("Could not generate the parameter documentation: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Drops the indexes built from the old files and reloads the connected vehicle's.</summary>
    private async Task ApplyAsync()
    {
        ParameterMetaDataRepositoryAPMpdef.Reset();
        ParamMetadata.Invalidate();
        if (IsConnected)
            await Services.Params.LoadMetadataAsync();
        ListFiles();
    }

    private void ListFiles()
    {
        Files.Clear();
        var dir = Settings.GetDataDirectory();
        var files = new List<string>();
        try
        {
            if (Directory.Exists(dir))
                files.AddRange(Directory.GetFiles(dir, "*.apm.pdef.xml"));
        }
        catch (Exception)
        {
            // unreadable folder: nothing to list
        }

        var generated = Settings.GetUserDataDirectory() + ParamMetadata.GeneratedFileName;
        if (File.Exists(generated))
            files.Add(generated);

        foreach (var f in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(f);
            Files.Add(new ParamDocsFile(info.Name, info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture),
                (info.Length / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB"));
        }
    }
}
