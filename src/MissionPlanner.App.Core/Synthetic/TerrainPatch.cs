using GMap.NET.MapProviders;

namespace MissionPlanner.App.Core.Synthetic;

/// <summary>Detail levels of a terrain patch.</summary>
public sealed record PatchSpec(double Half, int GridSize, int DemZoom, int ImageryZoom, int MaxImageryTiles)
{
    /// <summary>±8 km, 100 m grid, coarse imagery: the horizon and the landscape around.</summary>
    public static readonly PatchSpec Far = new(8000, 161, 11, 14, 10);

    /// <summary>±1.2 km, 20 m grid, sharp imagery: what is under and near the vehicle.</summary>
    public static readonly PatchSpec Near = new(1200, 121, 13, 17, 12);
}

/// <summary>
/// A square of terrain ready for the GPU, in its own local frame: interleaved vertices
/// (x east, y height MSL, z = −north, normal, u, v), triangle indices and the drape picture.
/// </summary>
public sealed class TerrainPatch
{
    public const int FloatsPerVertex = 8;

    public required LocalFrame Frame { get; init; }
    public required PatchSpec Spec { get; init; }
    public required float[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    public DrapeImage? Drape { get; init; }

    /// <summary>Whether real heights were found (false: a flat stand-in at the vehicle's ground level).</summary>
    public bool HasHeights { get; init; }

    /// <summary>The height at the frame's origin (centre of the patch).</summary>
    public float CentreHeight { get; init; }

    /// <summary>Builds a patch around a point; missing heights become <paramref name="fallbackHeight"/>.</summary>
    public static async Task<TerrainPatch> BuildAsync(TerrainSource terrain, GMapProvider? provider, double lat, double lng, PatchSpec spec,
        double fallbackHeight, CancellationToken cancel)
    {
        var frame = new LocalFrame(lat, lng);
        var gridTask = terrain.GridAsync(frame, spec.Half, spec.GridSize, spec.DemZoom, cancel);
        var drapeTask = provider == null
            ? Task.FromResult<DrapeImage?>(null)
            : Synthetic.Drape.BuildAsync(provider, frame, spec.Half, spec.ImageryZoom, spec.MaxImageryTiles, cancel);
        var grid = await gridTask.ConfigureAwait(false);
        var drape = await drapeTask.ConfigureAwait(false);
        return Build(frame, spec, grid, drape, fallbackHeight);
    }

    /// <summary>The mesh from a height grid (NaN = unknown) and a drape.</summary>
    public static TerrainPatch Build(LocalFrame frame, PatchSpec spec, float[] grid, DrapeImage? drape, double fallbackHeight)
    {
        var n = spec.GridSize;
        var known = grid.Count(h => !float.IsNaN(h));
        var fill = known > 0 ? (float)grid.Where(h => !float.IsNaN(h)).Average() : (float)fallbackHeight;
        if (known == 0)
            fill = (float)fallbackHeight;
        var heights = grid.Select(h => float.IsNaN(h) ? fill : h).ToArray();
        var step = 2 * spec.Half / (n - 1);

        var v = new float[n * n * FloatsPerVertex];
        for (var row = 0; row < n; row++)
        {
            for (var col = 0; col < n; col++)
            {
                var east = -spec.Half + col * step;
                var north = spec.Half - row * step;
                var hgt = heights[row * n + col];

                // normal from the neighbours' heights (central differences)
                var hl = heights[row * n + Math.Max(col - 1, 0)];
                var hr = heights[row * n + Math.Min(col + 1, n - 1)];
                var hu = heights[Math.Max(row - 1, 0) * n + col];
                var hd = heights[Math.Min(row + 1, n - 1) * n + col];
                var dx = (hr - hl) / (2 * step);
                var dzNorth = (hu - hd) / (2 * step);
                // surface y = h(x, z) with z = -north: normal ∝ (-dh/dx, 1, -dh/dz) = (-dx, 1, dzNorth)
                var len = (float)Math.Sqrt(dx * dx + 1 + dzNorth * dzNorth);

                float u = 0, tv = 0;
                if (drape != null)
                {
                    var (lat, lng) = frame.ToGeo(east, north);
                    (u, tv) = Synthetic.Drape.Uv(drape, lat, lng);
                }

                var i = (row * n + col) * FloatsPerVertex;
                v[i] = (float)east;
                v[i + 1] = hgt;
                v[i + 2] = (float)-north;
                v[i + 3] = (float)(-dx / len);
                v[i + 4] = 1 / len;
                v[i + 5] = (float)(dzNorth / len);
                v[i + 6] = u;
                v[i + 7] = tv;
            }
        }

        var idx = new uint[(n - 1) * (n - 1) * 6];
        var k = 0;
        for (var row = 0; row < n - 1; row++)
        {
            for (var col = 0; col < n - 1; col++)
            {
                var a = (uint)(row * n + col);
                var b = a + 1;
                var c = a + (uint)n;
                var d = c + 1;
                idx[k++] = a;
                idx[k++] = c;
                idx[k++] = b;
                idx[k++] = b;
                idx[k++] = c;
                idx[k++] = d;
            }
        }

        return new TerrainPatch
        {
            Frame = frame,
            Spec = spec,
            Vertices = v,
            Indices = idx,
            Drape = drape,
            HasHeights = known > 0,
            CentreHeight = heights[(n / 2) * n + n / 2],
        };
    }

    /// <summary>The height at a local point by bilinear interpolation of the grid; null outside the patch.</summary>
    public float? HeightAt(double east, double north)
    {
        var n = Spec.GridSize;
        var step = 2 * Spec.Half / (n - 1);
        var col = (east + Spec.Half) / step;
        var row = (Spec.Half - north) / step;
        if (col < 0 || row < 0 || col > n - 1 || row > n - 1)
            return null;
        var c0 = Math.Min((int)col, n - 2);
        var r0 = Math.Min((int)row, n - 2);
        var fc = (float)(col - c0);
        var fr = (float)(row - r0);
        float H(int r, int c) => Vertices[(r * n + c) * FloatsPerVertex + 1];
        var top = H(r0, c0) * (1 - fc) + H(r0, c0 + 1) * fc;
        var bottom = H(r0 + 1, c0) * (1 - fc) + H(r0 + 1, c0 + 1) * fc;
        return top * (1 - fr) + bottom * fr;
    }
}
