using System.Collections.Generic;
using System.Reflection;
using GMap.NET;
using GMap.NET.MapProviders;

namespace MissionPlanner.App.Core.Maps;

/// <summary>
/// Esri's current satellite imagery (World_Imagery, web Mercator). GMap.NET's built-in
/// "ArcGIS_Imagery_World_2D" points at ESRI_Imagery_World_2D, a service Esri retired (it now
/// answers 400), which is why that map stayed empty.
/// </summary>
public sealed class EsriWorldImageryProvider : ArcGISMapMercatorProviderBase
{
    private const string UrlFormat = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{0}/{1}/{2}";

    public static readonly EsriWorldImageryProvider Instance;

    private static readonly Guid ProviderId = new("6B3B5D0E-9B0D-4C5E-9E3A-2D8F0C6A1E47");

    private EsriWorldImageryProvider()
    {
        MaxZoom = 19;
    }

    static EsriWorldImageryProvider()
    {
        Instance = new EsriWorldImageryProvider();
        Register(Instance);
    }

    public override Guid Id => ProviderId;

    public override string Name => "ArcGIS World Imagery";

    public override PureImage GetTileImage(GPoint pos, int zoom) =>
        GetTileImageUsingHttp(string.Format(UrlFormat, zoom, pos.Y, pos.X));

    /// <summary>
    /// The tile cache finds a provider by its numeric id in GMapProviders' private table, which only
    /// holds the built-in ones; Mission Planner's own providers add themselves the same way.
    /// </summary>
    internal static void Register(GMapProvider provider)
    {
        var field = typeof(GMapProviders).GetField("DbHash", BindingFlags.Static | BindingFlags.NonPublic);
        if (field?.GetValue(null) is Dictionary<int, GMapProvider> table)
            table.TryAdd(provider.DbId, provider);
    }
}
