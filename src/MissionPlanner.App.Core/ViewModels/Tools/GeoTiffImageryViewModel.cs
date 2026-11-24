using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// TOOLS > GeoTIFF imagery (Ctrl+F "Custom GDAL"): loads the georeferenced images of a folder as
/// the "GeoTIFF Custom" map and the folder's DTED files as terrain. Mission Planner did this with
/// native GDAL, which has no .NET 10 build; the images are read with the managed LibTiff instead.
/// </summary>
public sealed partial class GeoTiffImageryViewModel : ToolPageViewModel
{
    public GeoTiffImageryViewModel(AppServices services)
        : base("GeoTIFF imagery",
            "Shows your own aerial images (GeoTIFF, or TIFF with a .tfw world file) over the satellite map: choose a folder, then the \"GeoTIFF Custom\" map type. DTED terrain files in the folder are used for elevation.",
            services)
    {
        _folder = Settings.Instance[GeoTiffImagery.FolderKey] ?? "";
        _opacity = GeoTiffImagery.Opacity * 100;
        Refresh();
    }

    public override bool NeedsVehicle => false;

    public ObservableCollection<GeoTiffImage> Images { get; } = new();

    [ObservableProperty]
    private string _folder;

    [ObservableProperty]
    private string _output = "";

    /// <summary>Overlay opacity in percent.</summary>
    [ObservableProperty]
    private double _opacity;

    /// <summary>The image picked in the list; the view zooms its map to it.</summary>
    [ObservableProperty]
    private GeoTiffImage? _selectedImage;

    public bool HasImages => Images.Count > 0;

    /// <summary>The map type that shows the overlay.</summary>
    public GMap.NET.MapProviders.GMapProvider Provider => GeoTiffImageryProvider.Instance;

    partial void OnOpacityChanged(double value)
    {
        GeoTiffImagery.Opacity = value / 100.0;
        Settings.Instance.Save();
        GMap.NET.GMaps.Instance.MemoryCache.Clear();
    }

    public override void OnShown()
    {
        base.OnShown();
        _ = LoadSavedAsync();
    }

    private async Task LoadSavedAsync()
    {
        try
        {
            await Task.Run(GeoTiffImagery.EnsureLoaded);
        }
        catch (Exception ex)
        {
            Report("Could not read the folder: " + ex.Message, true);
        }

        Refresh();
    }

    private void Refresh()
    {
        Images.Clear();
        foreach (var i in GeoTiffImagery.Images)
            Images.Add(i);
        OnPropertyChanged(nameof(HasImages));
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var folder = await Services.Dialogs.PickFolderAsync("Folder of GeoTIFF images");
        if (folder == null)
            return;
        await LoadFolderAsync(folder);
    }

    internal async Task LoadFolderAsync(string folder)
    {
        if (!Directory.Exists(folder) || IsBusy)
            return;
        Folder = folder;
        Settings.Instance[GeoTiffImagery.FolderKey] = folder;
        Settings.Instance.Save();

        IsBusy = true;
        Status = "Reading " + folder + "…";
        StatusIsError = false;
        try
        {
            void Progress(double fraction, string file) =>
                Services.Dispatcher.Post(() => Status = $"{fraction:0.0%} {Path.GetFileName(file)}");

            var result = await Task.Run(() =>
            {
                var r = GeoTiffImagery.ScanDirectory(folder, Progress);
                // terrain from the same folder, as Mission Planner's button
                DTED.OnProgress += Progress;
                try
                {
                    DTED.AddCustomDirectory(folder);
                }
                finally
                {
                    DTED.OnProgress -= Progress;
                }

                return r;
            });
            Refresh();
            Output = string.Join(Environment.NewLine,
                result.Added.Select(i => "Loaded " + i.Describe()).Concat(result.Skipped.Select(s => "Skipped " + s)));
            Report(GeoTiffImagery.Images.Count == 0
                ? "No georeferenced images found in " + folder + "."
                : $"{result.Added.Count} images added ({GeoTiffImagery.Images.Count} loaded). Choose the \"GeoTIFF Custom\" map type to see them.",
                GeoTiffImagery.Images.Count == 0);
            if (Images.Count > 0)
                SelectedImage = result.Added.FirstOrDefault() ?? Images[0];
        }
        catch (Exception ex)
        {
            Report("Could not read the folder: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearImages()
    {
        GeoTiffImagery.Clear();
        Refresh();
        Output = "";
        Report("The GeoTIFF overlay is empty.");
    }
}
