using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Paths on the vehicle's MAVFTP file system ("/", "@ROMFS/", "@SYS/"...).</summary>
public static class FtpPath
{
    /// <summary>Folders the browser starts from, as in Mission Planner's MAVFtp page.</summary>
    public static readonly IReadOnlyList<string> Roots = ["/", "@ROMFS/", "@SYS/"];

    /// <summary>A path travels in one FTP payload (239 bytes); leave room for the vehicle's terminator.</summary>
    public const int MaxPathBytes = 230;

    public static string Join(string directory, string name)
    {
        if (string.IsNullOrEmpty(directory))
            return name;
        return directory.EndsWith('/') ? directory + name : directory + "/" + name;
    }

    /// <summary>"/" for the root, "@SYS" for "@SYS/", else the last path segment.</summary>
    public static string DisplayName(string path)
    {
        if (path == "/")
            return "/";
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>Directory listings on SITL include ".", ".." and nameless placeholders.</summary>
    public static bool IsListable(string? name) => !string.IsNullOrEmpty(name) && name != "." && name != "..";

    /// <summary>Returns why <paramref name="name"/> cannot be created in <paramref name="directory"/>, or null when it can.</summary>
    public static string? ValidateNewName(string directory, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a name.";
        if (name != name.Trim())
            return "Names cannot start or end with a space.";
        if (name is "." or "..")
            return "\"" + name + "\" is not a valid name.";
        if (name.IndexOfAny(['/', '\\', '\0', '\t']) >= 0)
            return "Names cannot contain /, \\ or tabs.";
        if (Encoding.UTF8.GetByteCount(Join(directory, name)) > MaxPathBytes)
            return "The path is too long for MAVFTP.";
        return null;
    }

    /// <summary>
    /// Reduces a vehicle-chosen name to a safe local file name (no directories, no "..") or null
    /// when nothing safe is left.
    /// </summary>
    public static string? SafeLocalName(string remoteName)
    {
        var name = Path.GetFileName(remoteName.Replace('\\', '/').Split('/').Last());
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return IsListable(name) ? name : null;
    }

    /// <summary>
    /// A file name in <paramref name="folder"/> that does not exist yet: "name", then "name (1)",
    /// "name (2)"... before the extension.
    /// </summary>
    public static string UniqueLocalPath(string folder, string name, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var path = Path.Combine(folder, name);
        if (!exists(path))
            return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!exists(path))
                return path;
        }
    }
}

/// <summary>A file or folder in a vehicle directory listing.</summary>
public sealed record FtpEntry(string Directory, string Name, bool IsDirectory, ulong Size = 0, DateTime? ModifiedUtc = null)
{
    public string Path => FtpPath.Join(Directory, Name);

    public string Kind => IsDirectory ? "Folder" : "File";

    public string SizeText => IsDirectory ? "" : FtpFormat.Size(Size);

    public string ModifiedText => FtpFormat.Modified(ModifiedUtc);
}

public static class FtpFormat
{
    public static string Size(ulong bytes)
    {
        var c = CultureInfo.InvariantCulture;
        return bytes switch
        {
            < 1024 => bytes.ToString(c) + " B",
            < 1024 * 1024 => (bytes / 1024.0).ToString(bytes < 10 * 1024 ? "0.0" : "0", c) + " KB",
            < 1024UL * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", c) + " MB",
            _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", c) + " GB",
        };
    }

    /// <summary>Local time, blank when the vehicle did not report one.</summary>
    public static string Modified(DateTime? utc) =>
        utc == null ? "" : utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

public enum FtpSortColumn
{
    Name,
    Kind,
    Size,
    Modified,
}

/// <summary>
/// The file list of one vehicle folder: drops ".", ".." and nameless entries, filters by name
/// (substring, or a * / ? pattern) and sorts with folders first.
/// </summary>
public sealed class FtpListing
{
    private List<FtpEntry> _all = [];

    public string Directory { get; private set; } = "";

    /// <summary>Every usable entry of the last listing, in the vehicle's order.</summary>
    public IReadOnlyList<FtpEntry> All => _all;

    public IReadOnlyList<FtpEntry> Folders =>
        _all.Where(e => e.IsDirectory).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();

    public string Filter { get; set; } = "";

    public FtpSortColumn SortColumn { get; private set; } = FtpSortColumn.Name;

    public bool Descending { get; private set; }

    public void Load(string directory, IEnumerable<FtpEntry> entries)
    {
        Directory = directory;
        var seen = new HashSet<(string, bool)>();
        _all = entries.Where(e => FtpPath.IsListable(e.Name) && seen.Add((e.Name, e.IsDirectory)))
            .Select(e => e.Directory == directory ? e : e with { Directory = directory })
            .ToList();
    }

    public void Clear()
    {
        Directory = "";
        _all = [];
    }

    /// <summary>Clicking the column that is already sorted reverses it; another column sorts ascending.</summary>
    public void ToggleSort(FtpSortColumn column)
    {
        if (column == SortColumn)
            Descending = !Descending;
        else
        {
            SortColumn = column;
            Descending = false;
        }
    }

    public void SetSort(FtpSortColumn column, bool descending)
    {
        SortColumn = column;
        Descending = descending;
    }

    /// <summary>The entries to show: filtered, folders first, then sorted by the chosen column.</summary>
    public IReadOnlyList<FtpEntry> View()
    {
        var match = BuildMatcher(Filter);
        var shown = _all.Where(e => match(e.Name)).ToList();
        shown.Sort(Compare);
        return shown;
    }

    private int Compare(FtpEntry a, FtpEntry b)
    {
        // folders stay on top whichever way the list is sorted
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;

        var byName = CompareNames(a.Name, b.Name);
        var result = SortColumn switch
        {
            FtpSortColumn.Size => a.Size.CompareTo(b.Size),
            FtpSortColumn.Modified => Nullable.Compare(a.ModifiedUtc, b.ModifiedUtc),
            _ => byName,
        };
        if (result == 0)
            result = byName;
        return Descending ? -result : result;
    }

    private static int CompareNames(string a, string b)
    {
        var c = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return c != 0 ? c : StringComparer.Ordinal.Compare(a, b);
    }

    private static Func<string, bool> BuildMatcher(string? filter)
    {
        filter = filter?.Trim();
        if (string.IsNullOrEmpty(filter))
            return _ => true;
        if (filter.IndexOfAny(['*', '?']) < 0)
            return name => name.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var pattern = "^" + Regex.Escape(filter).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return regex.IsMatch;
    }
}
