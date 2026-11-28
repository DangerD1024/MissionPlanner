using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.Services;
using FirmwareInfo = MissionPlanner.ArduPilot.APFirmware.FirmwareInfo;
using MavType = MissionPlanner.ArduPilot.APFirmware.MAV_TYPE;
using ReleaseType = MissionPlanner.ArduPilot.APFirmware.RELEASE_TYPES;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>A vehicle picture of the Install Firmware page, labelled with the version it would install.</summary>
public sealed partial class VehicleTile : ObservableObject
{
    public VehicleTile(MavType type, string title, string icon)
    {
        Type = type;
        Title = title;
        Icon = icon;
    }

    public MavType Type { get; }

    public string Title { get; }

    /// <summary>Key of the tile's drawing in the view ("copter", "plane"...).</summary>
    public string Icon { get; }

    [ObservableProperty] private string _versionText = L.Tr("loading…");

    [ObservableProperty] private bool _isAvailable;

    public FirmwareInfo? Newest { get; private set; }

    public void Update(FirmwareCatalog? catalog, ReleaseType release)
    {
        Newest = catalog?.Newest(Type, release);
        IsAvailable = Newest != null;
        VersionText = catalog == null ? L.Tr("loading…") : Newest == null ? L.Tr("not available") : FirmwareCatalog.VersionLabel(Newest);
    }

    /// <summary>The six vehicle types Mission Planner offers.</summary>
    public static IReadOnlyList<VehicleTile> CreateAll() =>
    [
        new(MavType.Copter, "Copter", "copter"),
        new(MavType.FIXED_WING, "Plane", "plane"),
        new(MavType.GROUND_ROVER, "Rover", "rover"),
        new(MavType.SUBMARINE, "Sub", "sub"),
        new(MavType.HELICOPTER, "Heli", "heli"),
        new(MavType.ANTENNA_TRACKER, "Antenna Tracker", "tracker"),
    ];
}

public sealed record ReleaseOption(ReleaseType Type, string Name)
{
    public static IReadOnlyList<ReleaseOption> All { get; } =
    [
        new(ReleaseType.OFFICIAL, "Official"),
        new(ReleaseType.BETA, "Beta"),
        new(ReleaseType.DEV, "Dev (latest)"),
    ];

    public override string ToString() => Name;
}

/// <summary>A serial port in the board list, with the board ids its USB details point to.</summary>
public sealed record PortRow(SerialDevice Device, IReadOnlyList<long> BoardIds, string BoardText, string BoardName)
{
    public string PortName => Device.PortName;

    public string Description => Device.Description;

    public bool IsRecognised => BoardIds.Count > 0;

    public bool IsBootloader => Device.LooksLikeBootloader;

    public static PortRow Create(SerialDevice device, FirmwareCatalog? catalog)
    {
        var ids = catalog?.BoardIdsFor(device.Board, device.HardwareId) ?? [];
        var product = device.Board.EndsWith("-BL", StringComparison.OrdinalIgnoreCase) ? device.Board[..^3] : device.Board;
        var name = product;
        string text;
        if (ids.Count == 1)
        {
            // the board's own name when the USB product string is one of the id's platforms
            name = catalog!.PlatformsFor(ids[0]).FirstOrDefault(p => string.Equals(p, product, StringComparison.OrdinalIgnoreCase))
                   ?? catalog.BoardName(ids[0]);
            text = $"{name} · board id {ids[0]}";
        }
        else if (ids.Count > 1)
            text = $"{ids.Count} possible boards ({string.Join(", ", ids.Take(4))}{(ids.Count > 4 ? ", …" : "")})";
        else if (catalog == null && device.Usb != null)
            text = "checking…";
        else
            text = device.Usb == null ? "no USB details" : "board not recognised";
        if (device.LooksLikeBootloader)
            text += " · in bootloader";
        return new PortRow(device, ids, text, name.Length > 0 ? name : device.PortName);
    }
}

/// <summary>Reports progress on the UI thread through the app's dispatcher (works without a synchronization context).</summary>
internal sealed class DispatcherProgress<T>(IUiDispatcher dispatcher, Action<T> handler) : IProgress<T>
{
    public void Report(T value) => dispatcher.Post(() => handler(value));
}
