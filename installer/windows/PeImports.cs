// Reads the DLL names a Windows executable or DLL imports (normal and delay-loaded), so the
// installer build can copy exactly the GStreamer libraries the chosen plugins need.
// Loaded by build-msi.ps1 with Add-Type.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class PeImports
{
    public static List<string> Read(string path)
    {
        var names = new List<string>();
        var data = File.ReadAllBytes(path);
        int peOffset = BitConverter.ToInt32(data, 0x3C);
        if (peOffset <= 0 || peOffset + 24 > data.Length || BitConverter.ToUInt32(data, peOffset) != 0x00004550)
            return names;

        int coff = peOffset + 4;
        int sectionCount = BitConverter.ToUInt16(data, coff + 2);
        int optionalSize = BitConverter.ToUInt16(data, coff + 16);
        int optional = coff + 20;
        bool pe32Plus = BitConverter.ToUInt16(data, optional) == 0x20B;
        int dataDirs = optional + (pe32Plus ? 112 : 96);
        int sections = optional + optionalSize;

        Func<uint, int> toOffset = rva =>
        {
            for (int i = 0; i < sectionCount; i++)
            {
                int s = sections + i * 40;
                uint va = BitConverter.ToUInt32(data, s + 12);
                uint size = Math.Max(BitConverter.ToUInt32(data, s + 8), BitConverter.ToUInt32(data, s + 16));
                uint raw = BitConverter.ToUInt32(data, s + 20);
                if (rva >= va && rva < va + size)
                    return (int)(rva - va + raw);
            }

            return -1;
        };

        Func<uint, string> readName = rva =>
        {
            int o = toOffset(rva);
            if (o < 0)
                return null;
            int end = o;
            while (end < data.Length && data[end] != 0)
                end++;
            return Encoding.ASCII.GetString(data, o, end - o);
        };

        // directory 1: imports (20-byte descriptors, name RVA at +12); 13: delay imports (32 bytes, name RVA at +4)
        foreach (var dir in new[] { new[] { 1, 20, 12 }, new[] { 13, 32, 4 } })
        {
            uint rva = BitConverter.ToUInt32(data, dataDirs + dir[0] * 8);
            if (rva == 0)
                continue;
            int o = toOffset(rva);
            if (o < 0)
                continue;
            for (; o + dir[1] <= data.Length; o += dir[1])
            {
                uint nameRva = BitConverter.ToUInt32(data, o + dir[2]);
                if (nameRva == 0)
                    break;
                var name = readName(nameRva);
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
        }

        return names;
    }
}
