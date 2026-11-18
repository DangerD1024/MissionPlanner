using System.IO.Compression;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;
using Newtonsoft.Json.Linq;

namespace MissionPlanner.App.Tests.SetupScreens.Firmware;

/// <summary>A small manifest in the shape of firmware.ardupilot.org/manifest.json, and .apj files built in memory.</summary>
internal static class FirmwareFixtures
{
    public const string Manifest = """
    {
      "format-version": "1.0.0",
      "firmware": [
        { "board_id": 1013, "mav-type": "Copter", "format": "apj", "mav-firmware-version-type": "OFFICIAL",
          "mav-firmware-version": "4.7.2", "mav-firmware-version-str": "V4.7.2", "vehicletype": "Copter", "platform": "MatekH743",
          "bootloader_str": ["MatekH743-BL"], "USBID": ["0x1209/0x5740"], "git-sha": "aaa", "latest": 0,
          "url": "https://firmware.ardupilot.org/Copter/stable/MatekH743/arducopter.apj" },
        { "mav-type": "Copter", "format": "hex", "mav-firmware-version-type": "OFFICIAL",
          "mav-firmware-version": "4.7.2", "mav-firmware-version-str": "V4.7.2", "vehicletype": "Copter", "platform": "MatekH743",
          "url": "https://firmware.ardupilot.org/Copter/stable/MatekH743/arducopter_with_bl.hex" },
        { "board_id": 1013, "mav-type": "Copter", "format": "apj", "mav-firmware-version-type": "STABLE-4.6.3",
          "mav-firmware-version": "4.6.3", "mav-firmware-version-str": "V4.6.3", "vehicletype": "Copter", "platform": "MatekH743",
          "bootloader_str": ["MatekH743-BL"], "USBID": ["0x1209/0x5740"],
          "url": "https://firmware.ardupilot.org/Copter/stable-4.6.3/MatekH743/arducopter.apj" },
        { "board_id": 1013, "mav-type": "Copter", "format": "apj", "mav-firmware-version-type": "BETA",
          "mav-firmware-version": "4.7.3", "mav-firmware-version-str": "V4.7.3-beta1", "vehicletype": "Copter", "platform": "MatekH743",
          "bootloader_str": ["MatekH743-BL"], "USBID": ["0x1209/0x5740"],
          "url": "https://firmware.ardupilot.org/Copter/beta/MatekH743/arducopter.apj" },
        { "board_id": 1013, "mav-type": "HELICOPTER", "format": "apj", "mav-firmware-version-type": "OFFICIAL",
          "mav-firmware-version": "4.7.2", "vehicletype": "Copter", "platform": "MatekH743",
          "bootloader_str": ["MatekH743-BL"], "USBID": ["0x1209/0x5740"],
          "url": "https://firmware.ardupilot.org/Copter/stable/MatekH743-heli/arducopter-heli.apj" },
        { "board_id": 1013, "mav-type": "FIXED_WING", "format": "apj", "mav-firmware-version-type": "OFFICIAL",
          "mav-firmware-version": "4.7.1", "mav-firmware-version-str": "V4.7.1", "vehicletype": "Plane", "platform": "MatekH743",
          "bootloader_str": ["MatekH743-BL"], "USBID": ["0x1209/0x5740"],
          "url": "https://firmware.ardupilot.org/Plane/stable/MatekH743/arduplane.apj" },
        { "board_id": 140, "mav-type": "Copter", "format": "apj", "mav-firmware-version-type": "OFFICIAL",
          "mav-firmware-version": "4.7.2", "mav-firmware-version-str": "V4.7.2", "vehicletype": "Copter", "platform": "CubeOrange",
          "bootloader_str": ["CubeOrange-BL"], "USBID": ["0x2dae/0x1016"],
          "url": "https://firmware.ardupilot.org/Copter/stable/CubeOrange/arducopter.apj" },
        { "board_id": 140, "mav-type": "Copter", "format": "apj", "mav-firmware-version-type": "OFFICIAL",
          "mav-firmware-version": "4.7.2", "mav-firmware-version-str": "V4.7.2", "vehicletype": "Copter", "platform": "CubeOrange-bdshot",
          "bootloader_str": ["CubeOrange-BL"], "USBID": ["0x2dae/0x1016"],
          "url": "https://firmware.ardupilot.org/Copter/stable/CubeOrange-bdshot/arducopter.apj" },
        { "board_id": 9, "mav-type": "Copter", "format": "apj", "mav-firmware-version-type": "OFFICIAL",
          "mav-firmware-version": "4.7.2", "vehicletype": "Copter", "platform": "fmuv3",
          "bootloader_str": ["fmuv3-BL"], "USBID": ["0x26AC/0x0011", "0x1209/0x5740"],
          "url": "https://firmware.ardupilot.org/Copter/stable/fmuv3/arducopter.apj" },
        { "mav-type": "Copter", "format": "ELF", "mav-firmware-version-type": "OFFICIAL", "mav-firmware-version": "4.7.2",
          "vehicletype": "Copter", "platform": "SITL_x86_64_linux_gnu", "git-sha": "fb77",
          "url": "https://firmware.ardupilot.org/Copter/stable/SITL_x86_64_linux_gnu/arducopter" },
        { "mav-type": "Copter", "format": "ELF", "mav-firmware-version-type": "STABLE-4.6.3", "mav-firmware-version": "4.6.3",
          "vehicletype": "Copter", "platform": "SITL_x86_64_linux_gnu",
          "url": "https://firmware.ardupilot.org/Copter/stable-4.6.3/SITL_x86_64_linux_gnu/arducopter" },
        { "mav-type": "HELICOPTER", "format": "ELF", "mav-firmware-version-type": "OFFICIAL", "mav-firmware-version": "4.7.2",
          "vehicletype": "Copter", "platform": "SITL_x86_64_linux_gnu",
          "url": "https://firmware.ardupilot.org/Copter/stable/SITL_x86_64_linux_gnu-heli/arducopter-heli" },
        { "mav-type": "Copter", "format": "ELF", "mav-firmware-version-type": "BETA", "mav-firmware-version": "4.7.3",
          "vehicletype": "Copter", "platform": "SITL_x86_64_linux_gnu",
          "url": "https://firmware.ardupilot.org/Copter/beta/SITL_x86_64_linux_gnu/arducopter" },
        { "mav-type": "Copter", "format": "ELF", "mav-firmware-version-type": "OFFICIAL", "mav-firmware-version": "4.7.2",
          "vehicletype": "Copter", "platform": "SITL_arm_linux_gnueabihf",
          "url": "https://firmware.ardupilot.org/Copter/stable/SITL_arm_linux_gnueabihf/arducopter" },
        { "board_id": 1013, "mav-type": "ANTENNA_TRACKER", "format": "apj", "mav-firmware-version-type": "OFFICIAL",
          "mav-firmware-version": "4.7.2", "mav-firmware-version-str": "V4.7.2", "vehicletype": "AntennaTracker", "platform": "MatekH743",
          "bootloader_str": ["MatekH743-BL"], "USBID": ["0x1209/0x5740"],
          "url": "https://firmware.ardupilot.org/AntennaTracker/stable/MatekH743/antennatracker.apj" }
      ]
    }
    """;

    public static FirmwareCatalog Catalog() => FirmwareCatalog.Parse(Manifest);

    /// <summary>Deterministic pseudo-random image bytes.</summary>
    public static byte[] Image(int size, int seed = 1)
    {
        var rnd = new Random(seed);
        var b = new byte[size];
        rnd.NextBytes(b);
        return b;
    }

    /// <summary>An .apj as ArduPilot's build writes it: zlib-compressed, base64 image plus metadata.</summary>
    public static string Apj(int boardId, byte[] image, byte[]? extf = null)
    {
        var o = new JObject
        {
            ["board_id"] = boardId,
            ["magic"] = "APJFWv1",
            ["description"] = "Firmware for a test board",
            ["image"] = Convert.ToBase64String(Deflate(image)),
            ["image_size"] = image.Length,
            ["summary"] = "TESTBOARD",
            ["version"] = "0.1",
            ["git_identity"] = "abcdef12",
            ["board_revision"] = 0,
            ["USBID"] = "0x1209/0x5740",
        };
        if (extf != null)
        {
            o["extf_image"] = Convert.ToBase64String(Deflate(extf));
            o["extf_image_size"] = extf.Length;
        }

        return o.ToString();
    }

    public static string WriteApj(int boardId, byte[] image)
    {
        var path = Path.Combine(TestEnvironment.DataRoot, $"fw-{Guid.NewGuid():N}.apj");
        File.WriteAllText(path, Apj(boardId, image));
        return path;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }
}
