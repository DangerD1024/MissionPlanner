using MissionPlanner.ArduPilot;
using Newtonsoft.Json;
using FirmwareInfo = MissionPlanner.ArduPilot.APFirmware.FirmwareInfo;
using MavType = MissionPlanner.ArduPilot.APFirmware.MAV_TYPE;
using ReleaseType = MissionPlanner.ArduPilot.APFirmware.RELEASE_TYPES;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>
/// Queries over ArduPilot's firmware manifest (firmware.ardupilot.org/manifest.json.gz), as
/// <c>APFirmware</c> and <c>ConfigFirmwareManifest</c> make them, on a snapshot of the entries so
/// it can be built from a test fixture without touching the network.
/// </summary>
public sealed class FirmwareCatalog
{
    public FirmwareCatalog(IEnumerable<FirmwareInfo> firmware)
    {
        All = firmware.Where(f => f != null).ToArray();
        Uploadable = All.Where(IsUploadable).ToArray();
    }

    /// <summary>Every manifest entry, all formats.</summary>
    public IReadOnlyList<FirmwareInfo> All { get; }

    /// <summary>Entries this page can flash through the bootloader (.apj).</summary>
    public IReadOnlyList<FirmwareInfo> Uploadable { get; }

    public int Count => All.Count;

    public static FirmwareCatalog Parse(string json)
    {
        var root = JsonConvert.DeserializeObject<APFirmware.ManifestRoot>(json);
        return new FirmwareCatalog(root?.Firmware ?? []);
    }

    public static bool IsUploadable(FirmwareInfo f) =>
        f.BoardId != 0 && f.Url != null &&
        (string.Equals(f.Format, "apj", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(f.Format, "px4", StringComparison.OrdinalIgnoreCase));

    public static string ManifestName(ReleaseType release) => release.ToString();

    public static string ManifestName(MavType type) => type.ToString();

    /// <summary>The newest release of a vehicle type (any board, any format): what the vehicle tile offers.</summary>
    public FirmwareInfo? Newest(MavType type, ReleaseType release)
    {
        var t = ManifestName(type);
        var r = ManifestName(release);
        // among the newest, prefer an entry with a display version ("V4.7.2") and a board file
        return All.Where(f => f.MavType == t && f.MavFirmwareVersionType == r)
            .OrderByDescending(f => f.MavFirmwareVersion ?? new Version(0, 0))
            .ThenByDescending(f => !string.IsNullOrEmpty(f.MavFirmwareVersionStr))
            .ThenByDescending(IsUploadable)
            .ThenBy(f => f.Format, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>"V4.7.2 OFFICIAL", as Mission Planner labels its vehicle pictures.</summary>
    public static string VersionLabel(FirmwareInfo f)
    {
        var version = string.IsNullOrEmpty(f.MavFirmwareVersionStr)
            ? f.MavFirmwareVersion?.ToString() ?? ""
            : f.MavFirmwareVersionStr;
        return (version + " " + f.MavFirmwareVersionType).Trim();
    }

    /// <summary>
    /// Board ids for a USB device, as <c>APFirmware.GetBoardID</c>: the USB product string against
    /// the manifest's platform and bootloader names first, then the USB vendor/product id. The
    /// vendor/product id is read from any spelling (see <see cref="UsbId.Parse"/>), so it works with
    /// the hardware ids Linux produces. Empty when nothing matches.
    /// </summary>
    public IReadOnlyList<long> BoardIdsFor(string? board, string? hardwareId) =>
        BoardMatcher.BoardIds(All, board, hardwareId);

    /// <summary>
    /// What a vehicle tile installs on a board: uploadable entries for one of the board ids, the
    /// vehicle type and release (<c>ConfigFirmwareManifest.LookForPort</c>), one per file.
    /// </summary>
    public IReadOnlyList<FirmwareInfo> Candidates(IEnumerable<long> boardIds, MavType type, ReleaseType release)
    {
        var ids = boardIds.ToHashSet();
        var t = ManifestName(type);
        var r = ManifestName(release);
        return Uploadable
            .Where(f => ids.Contains(f.BoardId) && f.MavType == t && f.MavFirmwareVersionType == r)
            .GroupBy(f => f.Url!.AbsoluteUri, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(f => f.Platform, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(f => f.MavFirmwareVersion ?? new Version(0, 0))
            .ToList();
    }

    /// <summary>Board names ("platforms") that use a board id, for display.</summary>
    public IReadOnlyList<string> PlatformsFor(long boardId) =>
        Uploadable.Where(f => f.BoardId == boardId && !string.IsNullOrEmpty(f.Platform))
            .Select(f => f.Platform)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>A short name for a board id: its plainest platform name, or "board id N".</summary>
    public string BoardName(long boardId)
    {
        var names = PlatformsFor(boardId);
        return names.Count switch
        {
            0 => $"board id {boardId}",
            1 => names[0],
            _ => names[0] + " (or " + string.Join(", ", names.Skip(1).Take(3)) + (names.Count > 4 ? ", …" : "") + ")",
        };
    }

    /// <summary>The newest build of a vehicle for a platform (e.g. the SITL builds).</summary>
    public FirmwareInfo? ForPlatform(string platform, MavType type, ReleaseType release)
    {
        var t = ManifestName(type);
        var r = ManifestName(release);
        return All.Where(f => f.Platform == platform && f.MavType == t && f.MavFirmwareVersionType == r && f.Url != null)
            .OrderByDescending(f => f.MavFirmwareVersion ?? new Version(0, 0))
            .FirstOrDefault();
    }
}

/// <summary>Port of <c>APFirmware.GetBoardID</c> with the vendor/product id regex fixed.</summary>
public static class BoardMatcher
{
    public static IReadOnlyList<long> BoardIds(IEnumerable<FirmwareInfo> firmware, string? board, string? hardwareId,
        bool boardIdCheck = true)
    {
        var list = firmware as IReadOnlyList<FirmwareInfo> ?? firmware.ToList();

        if (!string.IsNullOrWhiteSpace(board))
        {
            var byName = list.Where(f => NameMatches(f, board!));
            if (boardIdCheck)
                byName = byName.Where(f => f.BoardId != 0);
            var ids = byName.Select(f => f.BoardId).Distinct().ToList();
            if (ids.Count > 0)
                return ids;
        }

        if (UsbId.Parse(hardwareId) is { } usb)
        {
            return list.Where(f => f.BoardId != 0 && f.Usbid != null && f.Usbid.Any(u => UsbId.Parse(u) == usb))
                .Select(f => f.BoardId)
                .Distinct()
                .ToList();
        }

        return [];
    }

    /// <summary>The name comparisons <c>GetBoardID</c> makes (case-insensitive), including the bootloader's "-BL" spellings.</summary>
    public static bool NameMatches(FirmwareInfo f, string board)
    {
        var b = board.Trim().ToLowerInvariant();
        var platform = f.Platform?.ToLowerInvariant();
        if (platform != null)
        {
            if (platform == b ||
                platform.Replace("primary", "secondary") == b ||
                platform == b.Replace("-bl", "secondary") ||
                platform == b.Replace("-bl", "primary") ||
                platform == b + "primary" ||
                platform == b + "secondary")
                return true;

            // a board in its bootloader reports "<platform>-BL"; the manifest does not always list it
            if (b.EndsWith("-bl", StringComparison.Ordinal) && platform == b[..^3])
                return true;
        }

        return f.BootloaderStr != null && f.BootloaderStr.Any(s => s != null && s.Trim().ToLowerInvariant() == b);
    }
}
