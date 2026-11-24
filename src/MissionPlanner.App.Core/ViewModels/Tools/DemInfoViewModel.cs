using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMap.NET;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>One loaded elevation file, as Mission Planner's DEM list shows it.</summary>
public sealed record DemEntry(string FileName, RectLatLng Area, int Width, int Height, int Bits, string Projection)
{
    public string Name => Path.GetFileName(FileName);

    /// <summary>The line of Mission Planner's list: file = area = width*height bits projection.</summary>
    public string Line => string.Format(CultureInfo.InvariantCulture, "{0} = {1} = {2}*{3} {4} {5}",
        FileName, AreaText, Width, Height, Bits, Projection);

    public string AreaText => string.Format(CultureInfo.InvariantCulture, "{0:0.0000},{1:0.0000} to {2:0.0000},{3:0.0000}",
        Area.Top, Area.Left, Area.Bottom, Area.Right);

    public string SizeText => string.Format(CultureInfo.InvariantCulture, "{0}×{1}, {2} bit", Width, Height, Bits);

    public static DemEntry From(GeoTiff.geotiffdata d) =>
        new(d.FileName ?? "", d.Area, d.width, d.height, d.bits,
            d.srcProjection?.Name ?? d.srcProjection?.Transform?.Name ?? "");
}

/// <summary>TOOLS > DEM (Ctrl+F "DEM"): the GeoTIFF elevation files the terrain lookups use.</summary>
public sealed partial class DemInfoViewModel(AppServices services)
    : ToolPageViewModel("DEM",
        "The elevation models (GeoTIFF) loaded from the terrain folder, used before the downloaded SRTM tiles. Click a line to show its area on the map.",
        services)
{
    public override bool NeedsVehicle => false;

    public ObservableCollection<DemEntry> Entries { get; } = new();

    [ObservableProperty]
    private DemEntry? _selected;

    public string Folder => srtm.datadirectory;

    public bool IsEmpty => Entries.Count == 0;

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
            // the first use reads the folder's GeoTIFFs (GeoTiff's static constructor)
            var list = await Task.Run(() =>
            {
                lock (GeoTiff.index)
                    return GeoTiff.index.Select(DemEntry.From).ToList();
            });
            Entries.Clear();
            foreach (var e in list)
                Entries.Add(e);
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Folder));
            Status = list.Count == 0 ? $"No GeoTIFF elevation files in {Folder}." : $"{list.Count} elevation files in use.";
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            Report("Could not list the elevation files: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Report("Could not open the folder: " + ex.Message, true);
        }
    }
}
