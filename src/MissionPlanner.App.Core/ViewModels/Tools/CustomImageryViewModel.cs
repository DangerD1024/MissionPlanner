using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMap.NET;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.Maps;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// Mission Planner's "Inject GE" and "Clear Custom Maps": map tiles from a folder laid out as
/// Z{zoom}/{y}/{x}.jpg|png (Google Earth / tile exporter layout) put into the tile cache as the
/// "Custom" map, and removed again.
/// </summary>
public static partial class CustomImagery
{
    [GeneratedRegex(@"Z([0-9]+)[\\/]([0-9]+)[\\/]([0-9]+)")]
    private static partial Regex TilePath();

    public sealed record InjectResult(int Injected, int Skipped);

    /// <summary>The zoom and tile of a file path (Z15\12345\23456.jpg: zoom 15, y 12345, x 23456), or null.</summary>
    public static (int Zoom, long X, long Y)? ParseTilePath(string file)
    {
        var m = TilePath().Match(file);
        if (!m.Success)
            return null;
        return (int.Parse(m.Groups[1].Value), long.Parse(m.Groups[3].Value), long.Parse(m.Groups[2].Value));
    }

    /// <summary>Puts every .jpg and .png under <paramref name="folder"/> into the cache as the Custom map.</summary>
    public static InjectResult Inject(string folder, Action<string>? progress = null)
    {
        MapTiles.Initialize();
        var files = Directory.GetFiles(folder, "*.jpg", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(folder, "*.png", SearchOption.AllDirectories));
        var injected = 0;
        var skipped = 0;
        foreach (var file in files)
        {
            var tile = ParseTilePath(file);
            if (tile == null)
                continue;
            var bytes = File.ReadAllBytes(file);
            // the cache keeps the encoded image; both apps decode jpg and png alike
            if (!MapTiles.TileBytesProxy.LooksLikeImage(bytes))
            {
                skipped++;
                continue;
            }

            progress?.Invoke(file);
            if (GMaps.Instance.PrimaryCache.PutImageToCache(bytes, Custom.Instance.DbId, new GPoint(tile.Value.X, tile.Value.Y), tile.Value.Zoom))
                injected++;
            else
                skipped++;
        }

        GMaps.Instance.MemoryCache.Clear();
        return new InjectResult(injected, skipped);
    }

    /// <summary>Removes every Custom map tile from the cache; returns how many.</summary>
    public static int Clear()
    {
        MapTiles.Initialize();
        var removed = GMaps.Instance.PrimaryCache.DeleteOlderThan(DateTime.Now, Custom.Instance.DbId);
        GMaps.Instance.MemoryCache.Clear();
        return removed;
    }
}

/// <summary>TOOLS > Custom imagery (Ctrl+F "Inject GE" and "Clear Custom Maps").</summary>
public sealed partial class CustomImageryViewModel(AppServices services)
    : ToolPageViewModel("Custom imagery",
        "Adds your own map tiles to Mission Planner: pick a folder of tiles laid out as Z<zoom>/<y>/<x>.jpg or .png (as Google Earth and tile exporters save them). Show them by choosing the \"Custom\" map type.",
        services)
{
    public override bool NeedsVehicle => false;

    [ObservableProperty]
    private string _currentFile = "";

    [RelayCommand]
    private async Task InjectAsync()
    {
        var folder = await Services.Dialogs.PickFolderAsync("Folder of map tiles (Z<zoom>/<y>/<x>)");
        if (folder == null)
            return;
        await InjectFolderAsync(folder);
    }

    internal async Task InjectFolderAsync(string folder)
    {
        if (!Directory.Exists(folder) || IsBusy)
            return;
        IsBusy = true;
        Status = "Adding tiles…";
        StatusIsError = false;
        try
        {
            var result = await Task.Run(() => CustomImagery.Inject(folder,
                f => Services.Dispatcher.Post(() => CurrentFile = f)));
            CurrentFile = "";
            if (result.Injected == 0 && result.Skipped == 0)
                Report("No tiles found: the files must be in folders named Z<zoom>/<y>/<x>.jpg or .png.", true);
            else
                Report($"Added {result.Injected} tiles to the Custom map" + (result.Skipped > 0 ? $" ({result.Skipped} skipped: not an image)." : "."));
        }
        catch (Exception ex)
        {
            Report("Could not add the tiles: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (!await Services.Dialogs.ConfirmAsync("Custom imagery", "Remove all tiles of the Custom map from the cache?", "Remove"))
            return;
        await ClearNowAsync();
    }

    internal async Task ClearNowAsync()
    {
        IsBusy = true;
        try
        {
            var removed = await Task.Run(CustomImagery.Clear);
            Report($"Removed {removed} images");
        }
        catch (Exception ex)
        {
            Report("Could not remove the tiles: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
