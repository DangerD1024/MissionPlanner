using System.Globalization;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>A flash image read from an Intel HEX file: one block from the lowest address, gaps filled with 0xFF.</summary>
public sealed class FlashImage
{
    public FlashImage(uint startAddress, byte[] data, uint? entryPoint = null, long gapBytes = 0)
    {
        StartAddress = startAddress;
        Data = data;
        EntryPoint = entryPoint;
        GapBytes = gapBytes;
    }

    /// <summary>Where the first byte goes (0x08000000 for an ArduPilot "_with_bl.hex").</summary>
    public uint StartAddress { get; }

    public byte[] Data { get; }

    /// <summary>One past the last byte.</summary>
    public ulong EndAddress => (ulong)StartAddress + (ulong)Data.Length;

    /// <summary>The start address record (type 05, or 03 as segment:offset), when the file has one.</summary>
    public uint? EntryPoint { get; }

    /// <summary>Bytes between the file's data blocks, filled with 0xFF (erased flash).</summary>
    public long GapBytes { get; }

    /// <summary>STM32 flash starts at 0x08000000; an image from there holds the bootloader too.</summary>
    public const uint Stm32FlashStart = 0x0800_0000;

    public bool StartsAtFlashStart => StartAddress == Stm32FlashStart;

    public string RangeText => $"0x{StartAddress:X8}–0x{EndAddress - 1:X8}";

    /// <summary>A raw .bin, written as it is from <paramref name="address"/>.</summary>
    public static FlashImage FromBinary(byte[] data, uint address = Stm32FlashStart)
    {
        if (data.Length == 0)
            throw new FormatException("The file is empty.");
        if (data.Length > IntelHex.MaxImageSize)
            throw new FormatException($"The file is {data.Length / 1024 / 1024} MB, more than any STM32 flash.");
        return new FlashImage(address, data);
    }
}

/// <summary>
/// Intel HEX reader (record types 00 data, 01 end of file, 02 extended segment address, 03 start
/// segment address, 04 extended linear address, 05 start linear address), with checksums, into one
/// contiguous <see cref="FlashImage"/> as <c>objcopy -O binary</c> would make it, but filling gaps with 0xFF.
/// </summary>
public static class IntelHex
{
    /// <summary>Larger than any STM32's internal flash; a file spanning more mixes memories (e.g. external flash).</summary>
    public const int MaxImageSize = 16 * 1024 * 1024;

    public static FlashImage Load(string path)
    {
        using var reader = new StreamReader(path);
        return Parse(reader);
    }

    public static FlashImage Parse(string text)
    {
        using var reader = new StringReader(text);
        return Parse(reader);
    }

    /// <exception cref="FormatException">Not a valid HEX file, a bad checksum, no end record, or data too far apart.</exception>
    public static FlashImage Parse(TextReader reader)
    {
        var blocks = new List<(uint Address, byte[] Data)>();
        uint upper = 0; // from record 04 (<< 16) or 02 (<< 4)
        uint? entry = null;
        var sawEnd = false;
        var lineNo = 0;

        while (reader.ReadLine() is { } raw)
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line[0] != ':')
                throw new FormatException($"Line {lineNo} does not start with ':' (not an Intel HEX file).");
            if (line.Length < 11 || (line.Length - 1) % 2 != 0)
                throw new FormatException($"Line {lineNo} is too short or has an odd number of digits.");

            var bytes = new byte[(line.Length - 1) / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(line.AsSpan(1 + i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
                    throw new FormatException($"Line {lineNo} has a character that is not a hex digit.");
            }

            var count = bytes[0];
            if (bytes.Length != count + 5)
                throw new FormatException($"Line {lineNo} says {count} data bytes but has {bytes.Length - 5}.");

            byte sum = 0;
            foreach (var b in bytes)
                sum += b;
            if (sum != 0)
                throw new FormatException($"Bad checksum on line {lineNo}: the file is damaged. Download it again.");

            var offset = (uint)(bytes[1] << 8 | bytes[2]);
            var type = bytes[3];
            var data = bytes.AsSpan(4, count);
            switch (type)
            {
                case 0x00:
                    if (count > 0)
                        blocks.Add((upper + offset, data.ToArray()));
                    break;
                case 0x01:
                    sawEnd = true;
                    break;
                case 0x02:
                    RequireLength(count, 2, lineNo, type);
                    upper = (uint)(data[0] << 8 | data[1]) << 4;
                    break;
                case 0x03:
                    RequireLength(count, 4, lineNo, type);
                    entry = ((uint)(data[0] << 8 | data[1]) << 4) + (uint)(data[2] << 8 | data[3]);
                    break;
                case 0x04:
                    RequireLength(count, 2, lineNo, type);
                    upper = (uint)(data[0] << 8 | data[1]) << 16;
                    break;
                case 0x05:
                    RequireLength(count, 4, lineNo, type);
                    entry = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);
                    break;
                default:
                    throw new FormatException($"Line {lineNo} has an unknown record type {type:X2}.");
            }

            if (sawEnd)
                break;
        }

        if (!sawEnd)
            throw new FormatException("The file has no end record: it is incomplete. Download it again.");
        if (blocks.Count == 0)
            throw new FormatException("The file has no data.");

        var start = blocks.Min(b => b.Address);
        var end = blocks.Max(b => (ulong)b.Address + (ulong)b.Data.Length);
        if (end - start > MaxImageSize)
            throw new FormatException(
                $"The file has data from 0x{start:X8} to 0x{end - 1:X8}, more than {MaxImageSize / 1024 / 1024} MB apart: " +
                "it is for more than one memory (e.g. external flash) and cannot be written as one image.");

        var image = new byte[end - start];
        Array.Fill(image, (byte)0xFF);
        var written = new bool[image.Length];
        long used = 0;
        foreach (var (address, data) in blocks)
        {
            var at = (int)(address - start);
            for (var i = 0; i < data.Length; i++)
            {
                if (written[at + i])
                {
                    if (image[at + i] != data[i])
                        throw new FormatException($"The file writes two different values at 0x{address + (uint)i:X8}.");
                    continue;
                }

                written[at + i] = true;
                image[at + i] = data[i];
                used++;
            }
        }

        return new FlashImage(start, image, entry, image.Length - used);
    }

    private static void RequireLength(int count, int expected, int lineNo, byte type)
    {
        if (count != expected)
            throw new FormatException($"Line {lineNo}: a type {type:X2} record must have {expected} data bytes, not {count}.");
    }
}
