using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using GMap.NET;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>Place search for Zoom To (OpenStreetMap's Nominatim, as Mission Planner).</summary>
public static class PlaceSearch
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Nominatim refuses requests without an application name
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MissionPlanner/3");
        return c;
    }

    /// <summary>The first match's centre and bounding box (south, north, west, east), or null.</summary>
    public static async Task<(PointLatLng Centre, PointLatLng[] Box)?> FindAsync(string query, CancellationToken cancel = default)
    {
        var url = "https://nominatim.openstreetmap.org/search?format=json&limit=1&q=" + Uri.EscapeDataString(query);
        var json = await Http.GetStringAsync(url, cancel).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>Reads Nominatim's JSON answer.</summary>
    public static (PointLatLng Centre, PointLatLng[] Box)? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;
        var e = doc.RootElement[0];
        static double D(JsonElement v) => double.Parse(v.GetString() ?? "0", CultureInfo.InvariantCulture);
        var centre = new PointLatLng(D(e.GetProperty("lat")), D(e.GetProperty("lon")));
        if (e.TryGetProperty("boundingbox", out var bb) && bb.GetArrayLength() == 4)
            return (centre, [new PointLatLng(D(bb[0]), D(bb[2])), new PointLatLng(D(bb[1]), D(bb[3]))]);
        return (centre, [centre]);
    }
}

/// <summary>PLAN map tools: zoom to a place or coordinates, rotate, prefetch tiles, KML overlay, elevation graph.</summary>
public sealed partial class FlightPlanViewModel
{
    private const string BearingKey = "next_plan_bearing";

    /// <summary>Shapes of the KML overlay (drawn only, not part of the mission).</summary>
    public IReadOnlyList<PlanShape> KmlOverlay { get; private set; } = [];

    /// <summary>Rotate Map: the heading at the top of the map, degrees.</summary>
    [ObservableProperty]
    private double _mapBearing = Math.Clamp(MissionPlanner.Utilities.Settings.Instance.GetDouble(BearingKey, 0), -360, 360);

    partial void OnMapBearingChanged(double value) =>
        MissionPlanner.Utilities.Settings.Instance[BearingKey] = value.ToString(CultureInfo.InvariantCulture);

    public async Task RotateMapAsync()
    {
        if (await PromptNumberAsync("Rotate Map", "Heading at the top of the map (degrees, 0 = north up)",
                MapBearing.ToString("0", CultureInfo.InvariantCulture)) is { } h)
            MapBearing = ((h % 360) + 360) % 360;
    }

    /// <summary>Zoom To > Coordinates ("-35.36 149.16", degrees-minutes-seconds too).</summary>
    public async Task ZoomToCoordsAsync()
    {
        var text = await Services.Dialogs.PromptAsync("Zoom to coordinates", "Latitude and longitude, e.g. -35.363 149.165");
        if (text == null)
            return;
        if (CoordinateInput.Parse(text) is not { } c)
        {
            await Services.Dialogs.ShowMessageAsync("Zoom to coordinates", $"\"{text}\" is not a position.", MessageKind.Error);
            return;
        }

        ZoomAround(new PointLatLng(c.Lat, c.Lng), 300);
    }

    /// <summary>Zoom To > Place: searches OpenStreetMap for a place name.</summary>
    public async Task ZoomToPlaceAsync()
    {
        var text = await Services.Dialogs.PromptAsync("Zoom to place", "Place name or address");
        if (string.IsNullOrWhiteSpace(text))
            return;
        Status = $"Searching for {text}…";
        try
        {
            if (await PlaceSearch.FindAsync(text) is not { } found)
            {
                Status = "";
                await Services.Dialogs.ShowMessageAsync("Zoom to place", $"Nothing found for \"{text}\".", MessageKind.Warning);
                return;
            }

            Status = "";
            if (found.Box.Length == 2)
                RequestZoom(found.Box);
            else
                ZoomAround(found.Centre, 1000);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException or KeyNotFoundException)
        {
            Status = "";
            await Services.Dialogs.ShowMessageAsync("Zoom to place", "The place search failed: " + ex.Message, MessageKind.Error);
        }
    }

    private void ZoomAround(PointLatLng p, double metres)
    {
        var dLat = metres / 111_320.0;
        var dLng = metres / (111_320.0 * Math.Max(0.01, Math.Cos(p.Lat * Math.PI / 180)));
        RequestZoom([new PointLatLng(p.Lat - dLat, p.Lng - dLng), new PointLatLng(p.Lat + dLat, p.Lng + dLng)]);
    }

    /// <summary>
    /// Map Tool > Prefetch: the drawn polygon's area (or the map on screen, between the two corners
    /// given) into the tile cache, between two zoom levels.
    /// </summary>
    public async Task PrefetchAreaAsync(PointLatLng viewCornerA, PointLatLng viewCornerB)
    {
        var provider = SelectedMapProvider.Provider;
        PointLatLng a = viewCornerA, b = viewCornerB;
        if (Polygon.Count >= 3)
        {
            a = new PointLatLng(Polygon.Max(p => p.Lat), Polygon.Min(p => p.Lng));
            b = new PointLatLng(Polygon.Min(p => p.Lat), Polygon.Max(p => p.Lng));
        }
        else if (!await Services.Dialogs.ConfirmAsync("Prefetch", "No polygon drawn. Prefetch the area shown on the map?", "Prefetch"))
        {
            return;
        }

        var maxZoom = provider.MaxZoom ?? 20;
        if (await PromptNumberAsync("Prefetch", "Lowest zoom level", "1") is not { } min ||
            await PromptNumberAsync("Prefetch", $"Highest zoom level (up to {maxZoom})", Math.Min(maxZoom, 18).ToString(CultureInfo.InvariantCulture)) is not { } max)
            return;
        var tiles = TilePrefetch.ForArea(provider.Projection, a, b, Math.Clamp((int)min, 0, maxZoom), Math.Clamp((int)max, 0, maxZoom));
        await StartPrefetchAsync(provider, tiles);
    }

    /// <summary>Map Tool > Prefetch WP Path: the tiles along the mission route up to a zoom level.</summary>
    public async Task PrefetchPathAsync()
    {
        var provider = SelectedMapProvider.Provider;
        var path = RoutePoints();
        if (path.Count < 1)
        {
            Status = "No waypoints";
            return;
        }

        var maxZoom = provider.MaxZoom ?? 20;
        if (await PromptNumberAsync("Prefetch WP Path", $"Highest zoom level (up to {maxZoom})", Math.Min(maxZoom, 18).ToString(CultureInfo.InvariantCulture)) is not { } max)
            return;
        var tiles = TilePrefetch.ForPath(provider.Projection, path, 250, 1, Math.Clamp((int)max, 1, maxZoom));
        await StartPrefetchAsync(provider, tiles);
    }

    private async Task StartPrefetchAsync(GMap.NET.MapProviders.GMapProvider provider, List<TileId> tiles)
    {
        if (tiles.Count == 0)
        {
            Status = "No tiles to fetch";
            return;
        }

        if (tiles.Count > TilePrefetch.MaxTiles)
        {
            await Services.Dialogs.ShowMessageAsync("Prefetch",
                $"That is more than {TilePrefetch.MaxTiles:N0} tiles. Choose a smaller area or a lower zoom level.", MessageKind.Warning);
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("Prefetch", $"Download {tiles.Count:N0} {provider.Name} tiles into the map cache?", "Download"))
            return;
        await Services.Windows.ShowDialogAsync(new TilePrefetchViewModel(provider, tiles, Services.Dispatcher), 460, 220);
    }

    /// <summary>Home then the mission's positions, as the map draws the route.</summary>
    private List<PointLatLng> RoutePoints()
    {
        var path = new List<PointLatLng>();
        if (Home is { } h)
            path.Add(h);
        path.AddRange(Store.Mission.Where(i => i.HasLocation && (i.Lat != 0 || i.Lng != 0)).Select(i => new PointLatLng(i.Lat, i.Lng)));
        return path;
    }

    /// <summary>Map Tool > KML Overlay: draws the shapes of a KML/KMZ over the map.</summary>
    public async Task LoadKmlOverlayAsync()
    {
        var path = await Services.Dialogs.OpenFileAsync("KML overlay", new FileFilter("Google Earth", "kml", "kmz"), new FileFilter("All files", "*"));
        if (path == null)
            return;
        try
        {
            SetKmlOverlay(PlanFiles.ReadKml(ReadKmlText(path)));
            Status = $"KML overlay: {KmlOverlay.Count} shapes from {Path.GetFileName(path)}";
            var pts = KmlOverlay.SelectMany(s => s.Points).Select(p => new PointLatLng(p.Lat, p.Lng)).ToList();
            if (pts.Count > 0)
                RequestZoom(pts);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException or FormatException)
        {
            await Services.Dialogs.ShowMessageAsync("KML overlay", "Could not read the file: " + ex.Message, MessageKind.Error);
        }
    }

    public void SetKmlOverlay(IReadOnlyList<PlanShape> shapes)
    {
        KmlOverlay = shapes;
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearKmlOverlay() => SetKmlOverlay([]);

    /// <summary>Map Tool > Elevation Graph.</summary>
    public async Task ShowElevationGraphAsync()
    {
        var home = Home;
        var homeAlt = Store.HomeAlt;
        var mission = Store.Mission.ToList();
        var vm = new ElevationGraphViewModel(() => Task.Run(() => ElevationProfile.Build(home, homeAlt, mission, PlanTerrain.Altitude)));
        await Services.Windows.ShowDialogAsync(vm, 900, 480);
    }
}
