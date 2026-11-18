namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>What a board's bootloader says about itself (<c>Uploader.identify</c>).</summary>
public sealed record BootloaderInfo(
    string PortName,
    int BoardId,
    int BoardRevision,
    int BootloaderRevision,
    int FlashSize,
    int ExtFlashSize = 0,
    int Chip = 0,
    string? ChipDescription = null,
    string? SerialNumber = null)
{
    public override string ToString() =>
        $"board id {BoardId} rev {BoardRevision}, bootloader v{BootloaderRevision}, flash {FlashSize / 1024} KB" +
        (ExtFlashSize > 0 ? $" + {ExtFlashSize / 1024} KB external" : "") +
        (string.IsNullOrEmpty(ChipDescription) ? "" : ", " + ChipDescription) +
        $" on {PortName}";
}

/// <summary>Steps of a flash, for progress display.</summary>
public enum FlashStep
{
    Erase,
    EraseExternal,
    ProgramExternal,
    VerifyExternal,
    Program,
    Verify,
}

/// <summary>
/// A board's bootloader on an open port (the role of <c>px4uploader.Uploader</c>). Methods block;
/// call them off the UI thread.
/// </summary>
public interface IBootloader : IDisposable
{
    string PortName { get; }

    /// <summary>Syncs and reads the board's details; throws when no bootloader answers.</summary>
    BootloaderInfo Identify();

    /// <summary>True when the board's flash already holds this image (<c>Uploader.currentChecksum</c>).</summary>
    bool HasSameFirmware(ApjFirmware firmware);

    /// <summary>Erases, programs and verifies, then reboots the board (<c>Uploader.upload</c>).</summary>
    /// <param name="progress">Step and percent of that step (0..100).</param>
    void Upload(ApjFirmware firmware, Action<FlashStep, double> progress, Action<string> log, CancellationToken cancel);

    /// <summary>Leaves the bootloader and starts the firmware; never throws.</summary>
    void Reboot();
}

/// <summary>Opens a bootloader on a port; throws when the port cannot be opened.</summary>
public interface IBootloaderFactory
{
    IBootloader Open(string portName);
}

/// <summary>The real thing: <see cref="Px4Bootloader"/> on a serial port at 115200 baud.</summary>
public sealed class SerialBootloaderFactory : IBootloaderFactory
{
    public static SerialBootloaderFactory Instance { get; } = new();

    public IBootloader Open(string portName) =>
        new Px4Bootloader(new MissionPlanner.Comms.SerialPort(portName, 115200));
}
