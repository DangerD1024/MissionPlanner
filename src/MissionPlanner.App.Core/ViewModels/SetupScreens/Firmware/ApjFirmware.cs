using System.IO.Compression;
using Newtonsoft.Json.Linq;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>
/// An ArduPilot/PX4 firmware file (.apj / .px4): JSON with the zlib-compressed, base64-encoded
/// image and the board id it is built for. Port of <c>px4uploader.Firmware.ProcessFirmware</c>
/// and its CRCs.
/// </summary>
public sealed class ApjFirmware
{
    private static readonly uint[] CrcTable = BuildCrcTable();
    private static readonly byte[] CrcPad = [0xff, 0xff, 0xff, 0xff];

    public int BoardId { get; private init; }

    public int BoardRevision { get; private init; }

    public string? Description { get; private init; }

    public string? Summary { get; private init; }

    public string? Version { get; private init; }

    public string? GitIdentity { get; private init; }

    public string? UsbId { get; private init; }

    /// <summary>Uncompressed image size as stated in the file.</summary>
    public int ImageSize { get; private init; }

    /// <summary>Internal flash image, padded with 0xff to a multiple of 4 bytes.</summary>
    public byte[] Image { get; private init; } = [];

    public int ExtfImageSize { get; private init; }

    /// <summary>External flash image (some H7 boards), padded with 0xff to a multiple of 4 bytes.</summary>
    public byte[] ExtfImage { get; private init; } = [];

    public static ApjFirmware Load(string path) => Parse(File.ReadAllText(path));

    /// <exception cref="FormatException">Not a firmware file.</exception>
    public static ApjFirmware Parse(string json)
    {
        JObject o;
        try
        {
            o = JObject.Parse(json);
        }
        catch (Exception ex)
        {
            throw new FormatException("The file is not an ArduPilot firmware (.apj) file.", ex);
        }

        if (o["board_id"] == null || (o["image"] == null && o["extf_image"] == null))
            throw new FormatException("The firmware file has no board id or image.");

        var imageSize = (int?)o["image_size"] ?? 0;
        var extfSize = (int?)o["extf_image_size"] ?? 0;

        return new ApjFirmware
        {
            BoardId = (int)o["board_id"]!,
            BoardRevision = (int?)o["board_revision"] ?? 0,
            Description = (string?)o["description"],
            Summary = (string?)o["summary"],
            Version = (string?)o["version"],
            GitIdentity = (string?)o["git_identity"],
            UsbId = o["USBID"]?.Type == JTokenType.String ? (string?)o["USBID"] : o["USBID"]?.ToString(Newtonsoft.Json.Formatting.None),
            ImageSize = imageSize,
            Image = imageSize > 0 ? Inflate((string?)o["image"], imageSize) : [],
            ExtfImageSize = extfSize,
            ExtfImage = extfSize > 0 ? Inflate((string?)o["extf_image"], extfSize) : [],
        };
    }

    private static byte[] Inflate(string? base64, int size)
    {
        if (string.IsNullOrEmpty(base64))
            throw new FormatException("The firmware file's image is empty.");

        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new FormatException("The firmware file's image is not valid base64.", ex);
        }

        var image = new byte[(size + 3) / 4 * 4];
        Array.Fill(image, (byte)0xff);
        using var z = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
        var read = 0;
        try
        {
            while (read < size)
            {
                var n = z.Read(image, read, size - read);
                if (n == 0)
                    break;
                read += n;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new FormatException("The firmware file's image is corrupt.", ex);
        }

        if (read < size)
            throw new FormatException($"The firmware file's image is short ({read} of {size} bytes).");
        return image;
    }

    /// <summary>CRC the bootloader reports for flash holding this image, padded with 0xff to <paramref name="padLength"/> bytes.</summary>
    public uint Crc(int padLength)
    {
        var state = Crc32(Image, 0);
        for (var i = Image.Length; i < padLength - 1; i += 4)
            state = Crc32(CrcPad, state);
        return state;
    }

    /// <summary>CRC of the first <paramref name="length"/> bytes of the external flash image.</summary>
    public uint ExtfCrc(int length) => Crc32(ExtfImage.AsSpan(0, Math.Min(length, ExtfImage.Length)), 0);

    /// <summary>The bootloader's CRC32 (reflected 0xEDB88320, no initial or final inversion).</summary>
    public static uint Crc32(ReadOnlySpan<byte> bytes, uint state)
    {
        foreach (var b in bytes)
            state = CrcTable[(state ^ b) & 0xff] ^ (state >> 8);
        return state;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        return table;
    }
}
