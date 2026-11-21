using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>The map tile cache on disk: one folder per map provider (Mission Planner's GMAPCache, Ctrl+X).</summary>
public static class MapCacheFolders
{
    private static readonly string[] Patterns = ["*.jpg", "*.png"];

    public sealed record Folder(string Name, string Path, long Bytes, int Tiles)
    {
        public string SizeText => Bytes < 1024 * 1024
            ? (Bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " kB"
            : (Bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>The cache's provider folders with their size and tile count, largest first.</summary>
    public static IReadOnlyList<Folder> List(string root)
    {
        if (!Directory.Exists(root))
            return [];
        return Directory.GetDirectories(root)
            .Select(dir =>
            {
                long bytes = 0;
                var count = 0;
                foreach (var f in Tiles(dir))
                {
                    try
                    {
                        bytes += new FileInfo(f).Length;
                        count++;
                    }
                    catch (IOException)
                    {
                    }
                }

                return new Folder(System.IO.Path.GetFileName(dir), dir, bytes, count);
            })
            .OrderByDescending(f => f.Bytes)
            .ToList();
    }

    /// <summary>Deletes the tiles fetched before <paramref name="before"/> (DateTime.MaxValue: all). Returns how many.</summary>
    public static int DeleteOlderThan(string folder, DateTime before)
    {
        var removed = 0;
        foreach (var f in Tiles(folder))
        {
            try
            {
                if (File.GetLastWriteTime(f) < before)
                {
                    File.Delete(f);
                    removed++;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static IEnumerable<string> Tiles(string dir) =>
        Directory.Exists(dir) ? Patterns.SelectMany(p => Directory.EnumerateFiles(dir, p, SearchOption.AllDirectories)) : [];
}

/// <summary>TOOLS > Map cache: what is cached per map type, and removing old or all tiles.</summary>
public sealed partial class MapCacheViewModel : ToolPageViewModel
{
    private readonly Func<string> _root;

    public MapCacheViewModel(AppServices services)
        : this(services, () => MissionPlanner.Maps.MyImageCache.Instance?.CacheLocation ?? "")
    {
    }

    internal MapCacheViewModel(AppServices services, Func<string> root)
        : base("Map cache",
            "Map tiles are kept on disk so maps work offline. Remove tiles older than 30 days to get fresh imagery, or a whole map type to free space.",
            services)
    {
        _root = root;
    }

    public override bool NeedsVehicle => false;

    public ObservableCollection<MapCacheFolders.Folder> Folders { get; } = new();

    [ObservableProperty]
    private string _total = "";

    public string Location => _root();

    public override void OnShown()
    {
        base.OnShown();
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var root = _root();
            var list = await Task.Run(() => MapCacheFolders.List(root));
            Folders.Clear();
            foreach (var f in list)
                Folders.Add(f);
            var bytes = list.Sum(f => f.Bytes);
            Total = $"{list.Sum(f => f.Tiles)} tiles, {new MapCacheFolders.Folder("", "", bytes, 0).SizeText} in {root}";
            OnPropertyChanged(nameof(Location));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task RemoveOldAsync(MapCacheFolders.Folder folder) => RemoveAsync(folder, DateTime.Now.AddDays(-30), "older than 30 days");

    [RelayCommand]
    private async Task RemoveAllAsync(MapCacheFolders.Folder folder)
    {
        if (!await Services.Dialogs.ConfirmAsync("Map cache", $"Remove all {folder.Tiles} cached tiles of {folder.Name}? They download again when the map shows them.", "Remove"))
            return;
        await RemoveAsync(folder, DateTime.MaxValue, "");
    }

    private async Task RemoveAsync(MapCacheFolders.Folder folder, DateTime before, string what)
    {
        IsBusy = true;
        try
        {
            var removed = await Task.Run(() => MapCacheFolders.DeleteOlderThan(folder.Path, before));
            Report($"Removed {removed} tiles {what} of {folder.Name}.".Replace("  ", " "));
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Location, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Report("Could not open the folder: " + ex.Message, true);
        }
    }
}
