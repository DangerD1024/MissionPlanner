using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;
using Renci.SshNet;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>A DataFlash log on the companion computer.</summary>
public sealed partial class RemoteLogFile(string name, string fullName, long length, DateTime modified) : ObservableObject
{
    public string Name { get; } = name;

    public string FullName { get; } = fullName;

    public long Length { get; } = length;

    public DateTime Modified { get; } = modified;

    public string Text => $"{Name}   {Modified:yyyy-MM-dd HH:mm}   ({Length / 1024.0 / 1024.0:0.0} MB)";

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>
/// Mission Planner's "logdownload scp": lists the .bin DataFlash logs a companion computer keeps
/// (APSync dflogger, Solo...) over SSH/SFTP and downloads the ticked ones into the log folder.
/// </summary>
public sealed partial class ScpLogDownloadViewModel(AppServices services)
    : ToolPageViewModel("Log download (SSH)",
        "Lists the DataFlash logs a companion computer keeps (APSync dflogger, Solo) over SSH and downloads the ticked ones.",
        services)
{
    public const string HostKey = "next_scp_host";
    public const string UserKey = "next_scp_user";
    public const string PathKey = "next_scp_path";

    public override bool NeedsVehicle => false;

    [ObservableProperty]
    private string _host = Settings.Instance[HostKey, "10.0.1.128"];

    [ObservableProperty]
    private string _user = Settings.Instance[UserKey, "user"];

    /// <summary>Not saved.</summary>
    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _remotePath = Settings.Instance[PathKey, "/home/user/dflogger/dataflash/"];

    [ObservableProperty]
    private double _percent;

    public ObservableCollection<RemoteLogFile> Files { get; } = new();

    public string Destination => Settings.Instance.LogDir;

    /// <summary>Mission Planner's scp path, user:password@host:/dir/, split into the fields.</summary>
    public static (string User, string Password, string Host, string Path)? ParseScpPath(string text)
    {
        try
        {
            var uri = new Uri("http://" + text.Trim());
            var info = uri.UserInfo.Split(':', 2);
            return (Uri.UnescapeDataString(info[0]), info.Length > 1 ? Uri.UnescapeDataString(info[1]) : "", uri.Host, uri.AbsolutePath);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private void Remember()
    {
        Settings.Instance[HostKey] = Host.Trim();
        Settings.Instance[UserKey] = User.Trim();
        Settings.Instance[PathKey] = RemotePath.Trim();
        Settings.Instance.Save();
    }

    [RelayCommand]
    private async Task ListAsync()
    {
        if (IsBusy)
            return;
        Remember();
        IsBusy = true;
        Status = "Fetching the log list…";
        try
        {
            var files = await Task.Run(() =>
            {
                using var sftp = new SftpClient(Host.Trim(), User.Trim(), Password);
                sftp.Connect();
                return sftp.ListDirectory(RemotePath.Trim())
                    .Where(f => f.IsRegularFile && f.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.LastWriteTime)
                    .Select(f => new RemoteLogFile(f.Name, f.FullName, f.Length, f.LastWriteTime))
                    .ToList();
            });
            Files.Clear();
            foreach (var f in files)
                Files.Add(f);
            Report(files.Count == 0 ? "No logs found." : $"{files.Count} logs found.");
        }
        catch (Exception ex)
        {
            Report("Could not list the logs: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        var all = Files.Any(f => !f.IsChecked);
        foreach (var f in Files)
            f.IsChecked = all;
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        var chosen = Files.Where(f => f.IsChecked).ToList();
        if (IsBusy || chosen.Count == 0)
        {
            if (chosen.Count == 0)
                Report("Tick the logs to download.", true);
            return;
        }

        IsBusy = true;
        var dir = Destination;
        Directory.CreateDirectory(dir);
        var total = Math.Max(1, chosen.Sum(f => f.Length));
        try
        {
            await Task.Run(() =>
            {
                using var sftp = new SftpClient(Host.Trim(), User.Trim(), Password);
                sftp.Connect();
                long before = 0;
                foreach (var f in chosen)
                {
                    Services.Dispatcher.Post(() => Status = "Downloading " + f.Name + "…");
                    var target = Path.Combine(dir, f.Name);
                    using (var file = File.Create(target))
                    {
                        var start = before;
                        sftp.DownloadFile(f.FullName, file, bytes =>
                            Services.Dispatcher.Post(() => Percent = 100.0 * (start + (long)bytes) / total));
                    }

                    before += f.Length;
                }
            });
            Percent = 100;
            Report($"Downloaded {chosen.Count} logs into {dir}.");
        }
        catch (Exception ex)
        {
            Report("Download failed: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
