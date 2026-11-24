using System.Buffers.Binary;
using System.Text;

namespace MissionPlanner.App.Tests.Tools;

/// <summary>Small map-tool inputs written byte by byte: ESRI shapefiles and RGB GeoTIFFs.</summary>
internal static class MapToolFixtures
{
    /// <summary>A shapefile of polygons (type 5), one part each, coordinates as X (lng/easting), Y (lat/northing).</summary>
    public static byte[] Shapefile(params (double X, double Y)[][] polygons)
    {
        var records = new MemoryStream();
        var n = 1;
        foreach (var poly in polygons)
        {
            var content = new MemoryStream();
            var w = new BinaryWriter(content);
            w.Write(5);
            w.Write(poly.Min(p => p.X));
            w.Write(poly.Min(p => p.Y));
            w.Write(poly.Max(p => p.X));
            w.Write(poly.Max(p => p.Y));
            w.Write(1); // parts
            w.Write(poly.Length);
            w.Write(0); // first part starts at point 0
            foreach (var p in poly)
            {
                w.Write(p.X);
                w.Write(p.Y);
            }

            w.Flush();
            var bytes = content.ToArray();
            var head = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(head, n++);
            BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(4), bytes.Length / 2);
            records.Write(head);
            records.Write(bytes);
        }

        var header = new byte[100];
        BinaryPrimitives.WriteInt32BigEndian(header, 9994);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24), (int)((100 + records.Length) / 2));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 1000);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), 5);
        return header.Concat(records.ToArray()).ToArray();
    }

    /// <summary>
    /// An uncompressed 8-bit RGB GeoTIFF. <paramref name="colour"/> gives each pixel's RGB;
    /// <paramref name="tile"/> &gt; 0 writes tiles of that size instead of strips of 8 rows.
    /// Tie point at the top-left corner (pixel is area) with the given pixel size, and the GeoKeys:
    /// geographic WGS84, or the projected EPSG code.
    /// </summary>
    public static byte[] GeoTiff(int width, int height, Func<int, int, (byte R, byte G, byte B)> colour,
        double originX, double originY, double pixelX, double pixelY, int projectedEpsg = 0, int tile = 0)
    {
        // pixel blocks
        var blocks = new List<byte[]>();
        int bw, bh;
        if (tile > 0)
        {
            bw = bh = tile;
            for (var ty = 0; ty < height; ty += tile)
            for (var tx = 0; tx < width; tx += tile)
            {
                var b = new byte[tile * tile * 3];
                for (var y = 0; y < tile; y++)
                for (var x = 0; x < tile; x++)
                {
                    if (tx + x >= width || ty + y >= height)
                        continue;
                    var c = colour(tx + x, ty + y);
                    var o = (y * tile + x) * 3;
                    (b[o], b[o + 1], b[o + 2]) = (c.R, c.G, c.B);
                }

                blocks.Add(b);
            }
        }
        else
        {
            bw = width;
            bh = 8;
            for (var y0 = 0; y0 < height; y0 += bh)
            {
                var rows = Math.Min(bh, height - y0);
                var b = new byte[rows * width * 3];
                for (var y = 0; y < rows; y++)
                for (var x = 0; x < width; x++)
                {
                    var c = colour(x, y0 + y);
                    var o = (y * width + x) * 3;
                    (b[o], b[o + 1], b[o + 2]) = (c.R, c.G, c.B);
                }

                blocks.Add(b);
            }
        }

        ushort[] geoKeys = projectedEpsg == 0
            ? [1, 1, 0, 3, 1024, 0, 1, 2, 1025, 0, 1, 1, 2048, 0, 1, 4326]
            : [1, 1, 0, 3, 1024, 0, 1, 1, 1025, 0, 1, 1, 3072, 0, 1, (ushort)projectedEpsg];

        var entries = new List<(ushort Tag, ushort Type, int Count, byte[] Data)>
        {
            (256, 4, 1, Long(width)),
            (257, 4, 1, Long(height)),
            (258, 3, 3, Shorts(8, 8, 8)),
            (259, 3, 1, Shorts(1)),
            (262, 3, 1, Shorts(2)),
            (277, 3, 1, Shorts(3)),
            (284, 3, 1, Shorts(1)),
            (33550, 12, 3, Doubles(pixelX, pixelY, 0)),
            (33922, 12, 6, Doubles(0, 0, 0, originX, originY, 0)),
            (34735, 3, geoKeys.Length, Shorts(geoKeys)),
        };
        if (tile > 0)
        {
            entries.Add((322, 4, 1, Long(bw)));
            entries.Add((323, 4, 1, Long(bh)));
            entries.Add((324, 4, blocks.Count, new byte[blocks.Count * 4])); // offsets, filled below
            entries.Add((325, 4, blocks.Count, Longs(blocks.Select(b => b.Length))));
        }
        else
        {
            entries.Add((273, 4, blocks.Count, new byte[blocks.Count * 4]));
            entries.Add((278, 4, 1, Long(bh)));
            entries.Add((279, 4, blocks.Count, Longs(blocks.Select(b => b.Length))));
        }

        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        var ifdSize = 2 + entries.Count * 12 + 4;
        var extrasStart = 8 + ifdSize;
        var extras = entries.Where(e => e.Data.Length > 4).Sum(e => (e.Data.Length + 1) & ~1);
        var pixelStart = extrasStart + extras;
        var offsets = new List<int>();
        var at = pixelStart;
        foreach (var b in blocks)
        {
            offsets.Add(at);
            at += b.Length;
        }

        var offsetTag = tile > 0 ? 324 : 273;
        var idx = entries.FindIndex(e => e.Tag == offsetTag);
        entries[idx] = entries[idx] with { Data = Longs(offsets) };

        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        w.Write(Encoding.ASCII.GetBytes("II"));
        w.Write((ushort)42);
        w.Write(8);
        w.Write((ushort)entries.Count);
        var extraAt = extrasStart;
        var extraData = new MemoryStream();
        foreach (var e in entries)
        {
            w.Write(e.Tag);
            w.Write(e.Type);
            w.Write(e.Count);
            if (e.Data.Length <= 4)
            {
                var v = new byte[4];
                e.Data.CopyTo(v, 0);
                w.Write(v);
            }
            else
            {
                w.Write(extraAt);
                extraData.Write(e.Data);
                if ((e.Data.Length & 1) != 0)
                    extraData.WriteByte(0);
                extraAt += (e.Data.Length + 1) & ~1;
            }
        }

        w.Write(0); // no next IFD
        w.Write(extraData.ToArray());
        foreach (var b in blocks)
            w.Write(b);
        w.Flush();
        return file.ToArray();

        static byte[] Long(int v) => BitConverter.GetBytes(v);
        static byte[] Longs(IEnumerable<int> v) => v.SelectMany(BitConverter.GetBytes).ToArray();
        static byte[] Shorts(params ushort[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
        static byte[] Doubles(params double[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
    }
}
