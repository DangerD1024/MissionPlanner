using Newtonsoft.Json;
using FirmwareInfo = MissionPlanner.ArduPilot.APFirmware.FirmwareInfo;

namespace MissionPlanner.App.Core.ViewModels.Simulation;

/// <summary>Where a downloaded simulator came from, kept next to it as &lt;binary&gt;.json.</summary>
public sealed record SitlBinaryInfo(string Url, string? Version, string? Release, string? GitSha, DateTime DownloadedUtc)
{
    public string Describe()
    {
        var what = string.Join(" ", new[] { Version, Release }.Where(s => !string.IsNullOrEmpty(s)));
        return (what.Length == 0 ? "a build" : what) + $", downloaded {DownloadedUtc.ToLocalTime():d MMM yyyy}";
    }
}

/// <summary>What to do about the simulator binary before a start.</summary>
public enum SitlBinaryAction
{
    /// <summary>No local copy: download it.</summary>
    Download,

    /// <summary>The local copy is the build on offer (or nothing newer is known): use it.</summary>
    Reuse,

    /// <summary>A local copy exists but the server offers a different build: ask whether to update.</summary>
    AskUpdate,

    /// <summary>Nothing local and nothing to download.</summary>
    Unavailable,
}

/// <summary>A build the server offers.</summary>
public sealed record SitlBuild(Uri Url, string? Version, string? Release, string? GitSha)
{
    public static SitlBuild From(FirmwareInfo f) =>
        new(f.Url!, f.MavFirmwareVersion?.ToString(), f.MavFirmwareVersionType, f.GitSha);

    public SitlBinaryInfo ToInfo(DateTime downloadedUtc) => new(Url.AbsoluteUri, Version, Release, GitSha, downloadedUtc);
}

/// <summary>The simulators kept in &lt;data dir&gt;/sitl/ and the decision whether to download again.</summary>
public static class SitlBinaryStore
{
    public static SitlBinaryAction Decide(bool exists, SitlBinaryInfo? local, SitlBuild? offered)
    {
        if (!exists)
            return offered == null ? SitlBinaryAction.Unavailable : SitlBinaryAction.Download;
        if (offered == null)
            return SitlBinaryAction.Reuse;
        if (local == null)
            return SitlBinaryAction.AskUpdate;

        var sameUrl = string.Equals(local.Url, offered.Url.AbsoluteUri, StringComparison.Ordinal);
        var sameBuild = offered.GitSha != null
            ? string.Equals(local.GitSha, offered.GitSha, StringComparison.OrdinalIgnoreCase)
            : string.Equals(local.Version, offered.Version, StringComparison.Ordinal);
        return sameUrl && sameBuild ? SitlBinaryAction.Reuse : SitlBinaryAction.AskUpdate;
    }

    public static string InfoPath(string binaryPath) => binaryPath + ".json";

    public static SitlBinaryInfo? ReadInfo(string binaryPath)
    {
        try
        {
            var p = InfoPath(binaryPath);
            return File.Exists(p) ? JsonConvert.DeserializeObject<SitlBinaryInfo>(File.ReadAllText(p)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void WriteInfo(string binaryPath, SitlBinaryInfo info)
    {
        try
        {
            File.WriteAllText(InfoPath(binaryPath), JsonConvert.SerializeObject(info, Formatting.Indented));
        }
        catch (Exception)
        {
            // the binary works without its note
        }
    }

    /// <summary>rwxr-xr-x, so the downloaded simulator can run (the WinForms page P/Invokes chmod).</summary>
    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
