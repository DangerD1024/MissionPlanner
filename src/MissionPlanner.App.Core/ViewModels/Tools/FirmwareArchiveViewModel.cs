using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// Mission Planner's "rip all fw": downloads every firmware in the firmware list (firmware2.xml) into
/// the data folder's History directory and writes a firmware2.xml there that points at the local copies,
/// for installing firmware without internet.
/// </summary>
public sealed partial class FirmwareArchiveViewModel(AppServices services)
    : ToolPageViewModel("Download all firmware",
        "Downloads every firmware in Mission Planner's firmware list into a local folder, with a firmware2.xml that points at the copies.",
        services)
{
    public static readonly string[] ListUrls =
    [
        "https://github.com/ArduPilot/binary/raw/master/Firmware/firmware2.xml",
        "https://firmware.ardupilot.org/Tools/MissionPlanner/Firmware/firmware2.xml",
    ];

    private CancellationTokenSource? _cancel;

    public override bool NeedsVehicle => false;

    public string Folder => Path.Combine(Settings.GetDataDirectory(), "History");

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _current = "";

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        _cancel = new CancellationTokenSource();
        var progress = new Progress<(double Percent, string Text)>(p =>
        {
            Percent = p.Percent;
            Current = p.Text;
        });
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Settings.Instance.UserAgent);
            var (files, failed) = await RipAsync(http, Folder, progress, _cancel.Token);
            Report(failed == 0 ? $"Downloaded {files} files into {Folder}." : $"Downloaded {files} files into {Folder}; {failed} failed.", failed > 0);
        }
        catch (OperationCanceledException)
        {
            Report("Stopped.");
        }
        catch (Exception ex)
        {
            Report("Download failed: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
            _cancel.Dispose();
            _cancel = null;
        }
    }

    [RelayCommand]
    private void Stop() => _cancel?.Cancel();

    /// <summary>Fetches the list, downloads every *url* field of every software entry and writes the local list.</summary>
    public static async Task<(int Files, int Failed)> RipAsync(HttpClient http, string folder, IProgress<(double, string)>? progress, CancellationToken cancel)
    {
        XDocument? list = null;
        Exception? last = null;
        foreach (var url in ListUrls)
        {
            try
            {
                list = XDocument.Parse(await http.GetStringAsync(url, cancel));
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        if (list == null)
            throw new IOException("Could not download the firmware list: " + last?.Message, last);
        return await RipListAsync(list, (u, path, c) => DownloadAsync(http, u, path, c), folder, progress, cancel);
    }

    /// <summary>The download part, with the fetcher replaceable (tests).</summary>
    internal static async Task<(int Files, int Failed)> RipListAsync(XDocument list, Func<string, string, CancellationToken, Task> download,
        string folder, IProgress<(double, string)>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        var softwares = list.Descendants().Where(e => e.Name.LocalName == "software").ToList();
        var files = 0;
        var failed = 0;
        for (var i = 0; i < softwares.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var sw = softwares[i];
            var name = sw.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value ?? "";
            progress?.Report((100.0 * i / softwares.Count, name));
            foreach (var field in sw.Elements().Where(e => e.Name.LocalName.Contains("url", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var url = field.Value.Trim();
                if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    continue;
                var local = uri.LocalPath.TrimStart('/', '\\');
                var path = Path.Combine(folder, local.Replace('/', Path.DirectorySeparatorChar));
                field.Value = local;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await download(url, path, cancel);
                    files++;
                }
                catch (Exception) when (!cancel.IsCancellationRequested)
                {
                    failed++;
                }
            }
        }

        progress?.Report((100, ""));
        list.Save(Path.Combine(folder, "firmware2.xml"));
        return (files, failed);
    }

    private static async Task DownloadAsync(HttpClient http, string url, string path, CancellationToken cancel)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file, cancel);
    }
}
