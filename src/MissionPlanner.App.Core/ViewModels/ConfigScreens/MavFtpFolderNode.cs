using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// A folder in the MAVFtp tree. Its sub-folders are listed the first time it is expanded or
/// opened; until then it holds a placeholder child so the tree shows an expander.
/// </summary>
public sealed partial class FtpFolderNode : ObservableObject
{
    private readonly Action<FtpFolderNode>? _expandRequested;

    public FtpFolderNode(string path, FtpFolderNode? parent, Action<FtpFolderNode>? expandRequested)
        : this(path, FtpPath.DisplayName(path), parent, expandRequested, placeholder: false)
    {
    }

    private FtpFolderNode(string path, string name, FtpFolderNode? parent, Action<FtpFolderNode>? expandRequested, bool placeholder)
    {
        Path = path;
        Name = name;
        Parent = parent;
        IsPlaceholder = placeholder;
        _expandRequested = expandRequested;
        if (!placeholder)
            Children.Add(new FtpFolderNode(path, "Loading…", this, null, placeholder: true));
    }

    public string Path { get; }

    public string Name { get; }

    public FtpFolderNode? Parent { get; }

    /// <summary>The "Loading…" stand-in shown before the folder is listed.</summary>
    public bool IsPlaceholder { get; }

    /// <summary>Whether the sub-folders have been listed at least once.</summary>
    public bool IsLoaded { get; private set; }

    public ObservableCollection<FtpFolderNode> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !IsLoaded && !IsPlaceholder)
            _expandRequested?.Invoke(this);
    }

    public FtpFolderNode? FindChild(string name) => Children.FirstOrDefault(c => !c.IsPlaceholder && c.Name == name);

    /// <summary>
    /// Replaces the sub-folders with a new listing, keeping the nodes (and so the expansion and
    /// loaded state) of folders that are still there.
    /// </summary>
    public void SetChildren(IEnumerable<FtpEntry> folders)
    {
        IsLoaded = true;
        var names = folders.Where(f => f.IsDirectory && FtpPath.IsListable(f.Name))
            .Select(f => f.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();

        for (var i = Children.Count - 1; i >= 0; i--)
        {
            if (Children[i].IsPlaceholder || !names.Contains(Children[i].Name))
                Children.RemoveAt(i);
        }

        for (var i = 0; i < names.Count; i++)
        {
            var existing = FindChild(names[i]);
            if (existing == null)
                Children.Insert(i, new FtpFolderNode(FtpPath.Join(Path, names[i]), this, _expandRequested));
            else if (Children.IndexOf(existing) != i)
                Children.Move(Children.IndexOf(existing), i);
        }
    }

    public override string ToString() => Path;
}
