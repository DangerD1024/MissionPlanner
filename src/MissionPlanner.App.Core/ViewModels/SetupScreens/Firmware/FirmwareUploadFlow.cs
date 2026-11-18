namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

public enum UploadStage
{
    Idle,
    WaitingForBootloader,
    RebootingToBootloader,
    CheckingFirmware,
    Erasing,
    Programming,
    Verifying,
    Done,
    NotNeeded,
    Failed,
    Cancelled,
}

/// <summary>Progress of a flash: stage, percent of the stage (-1 = indeterminate) and a line for the user.</summary>
public readonly record struct UploadProgress(UploadStage Stage, double Percent, string Text);

public enum UploadOutcome
{
    /// <summary>Firmware written and verified; the board is rebooting.</summary>
    Flashed,

    /// <summary>The board already had this firmware and the user chose not to write it again.</summary>
    NotNeeded,

    /// <summary>No bootloader for this firmware's board answered in time.</summary>
    NoBoard,

    Failed,

    Cancelled,
}

public sealed record UploadResult(UploadOutcome Outcome, string Message, BootloaderInfo? Board = null);

/// <summary>Finds a board in its bootloader by probing serial ports (<c>Uploader.identify</c> on each).</summary>
public static class BootloaderScanner
{
    /// <summary>One probe round over <paramref name="ports"/> in parallel.</summary>
    /// <param name="accept">Which boards to keep; a probed board that is not accepted is closed and reported in <paramref name="rejected"/>.</param>
    /// <returns>The first accepted bootloader, open, or null.</returns>
    public static async Task<(IBootloader Bootloader, BootloaderInfo Info)?> ProbeAsync(IBootloaderFactory factory,
        IReadOnlyList<string> ports, Func<BootloaderInfo, bool> accept, ICollection<BootloaderInfo>? rejected,
        Action<string>? log, CancellationToken cancel)
    {
        var found = 0;
        (IBootloader, BootloaderInfo)? winner = null;
        var gate = new object();

        var probes = ports.Select(port => Task.Run(() =>
        {
            if (cancel.IsCancellationRequested || Volatile.Read(ref found) != 0)
                return;

            IBootloader bl;
            try
            {
                bl = factory.Open(port);
            }
            catch (Exception)
            {
                // busy, missing or not a serial port
                return;
            }

            var keep = false;
            try
            {
                if (Volatile.Read(ref found) != 0)
                    return;
                var info = bl.Identify();
                if (!accept(info))
                {
                    lock (gate)
                        rejected?.Add(info);
                    log?.Invoke($"Found a bootloader: {info} — not the board this firmware is for.");
                    return;
                }

                if (Interlocked.CompareExchange(ref found, 1, 0) == 0)
                {
                    winner = (bl, info);
                    keep = true;
                }
            }
            catch (Exception)
            {
                // no bootloader on this port
            }
            finally
            {
                if (!keep)
                    bl.Dispose();
            }
        }, CancellationToken.None)).ToArray();

        await Task.WhenAll(probes).ConfigureAwait(false);
        return winner;
    }
}

/// <summary>
/// Flashing a board through its bootloader, as <c>Firmware.UploadPX4</c> does it: wait up to 30 s
/// for a bootloader whose board id matches the firmware (the board must be plugged in or replugged,
/// or rebooted into the bootloader), compare checksums and offer to skip identical firmware,
/// then erase, program, verify and reboot. The bootloader and port list are injected.
/// </summary>
public sealed class FirmwareUploadFlow
{
    private readonly IBootloaderFactory _factory;
    private readonly Func<IReadOnlyList<string>> _listPorts;

    public FirmwareUploadFlow(IBootloaderFactory factory, Func<IReadOnlyList<string>> listPorts)
    {
        _factory = factory;
        _listPorts = listPorts;
    }

    public TimeSpan BootloaderTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Pause between probe rounds.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Pause after finding the bootloader before talking to it again (Mission Planner waits 500 ms).</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Asked when the board already holds this firmware; true uploads anyway. Null means skip.</summary>
    public Func<string, Task<bool>>? ConfirmSameFirmware { get; init; }

    /// <summary>
    /// Optional: asks a running ArduPilot to reboot into its bootloader. Called once, after the first
    /// probe round finds nothing; returns true when a reboot command went out.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? RebootToBootloader { get; init; }

    public IProgress<UploadProgress>? Progress { get; init; }

    public Action<string>? Log { get; init; }

    public UploadStage Stage { get; private set; } = UploadStage.Idle;

    public async Task<UploadResult> RunAsync(ApjFirmware firmware, CancellationToken cancel)
    {
        IBootloader? bootloader = null;
        try
        {
            var found = await WaitForBootloaderAsync(firmware, cancel).ConfigureAwait(false);
            if (found is not { } f)
            {
                return Finish(UploadOutcome.NoBoard, UploadStage.Failed, _lastRejected is { } r
                    ? $"Found a bootloader for board id {r.BoardId} on {r.PortName}, but this firmware is for board id {firmware.BoardId}."
                    : "No response from the board's bootloader. Unplug the board, click upload again and plug it back in when asked.");
            }

            bootloader = f.Bootloader;
            var board = f.Info;
            Log?.Invoke("Found " + board);

            await Task.Delay(SettleDelay, cancel).ConfigureAwait(false);

            Report(UploadStage.CheckingFirmware, -1, "Checking the firmware on the board…");
            bool same;
            try
            {
                same = await Task.Run(() => bootloader.HasSameFirmware(firmware), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                return Finish(UploadOutcome.Failed, UploadStage.Failed, "Lost communication with the board: " + ex.Message, board);
            }

            if (same)
            {
                Log?.Invoke("The board already has this firmware.");
                var again = ConfirmSameFirmware != null &&
                            await ConfirmSameFirmware("The board already has the same firmware.\nUpload it again anyway?").ConfigureAwait(false);
                if (!again)
                {
                    var b = bootloader;
                    await Task.Run(b.Reboot, CancellationToken.None).ConfigureAwait(false);
                    return Finish(UploadOutcome.NotNeeded, UploadStage.NotNeeded, "The board already has this firmware; no upload needed.", board);
                }
            }

            cancel.ThrowIfCancellationRequested();

            var bl = bootloader;
            await Task.Run(() => bl.Upload(firmware, OnFlashProgress, line => Log?.Invoke(line), cancel), CancellationToken.None)
                .ConfigureAwait(false);

            return Finish(UploadOutcome.Flashed, UploadStage.Done, "Upload done. The board is rebooting into the new firmware.", board);
        }
        catch (OperationCanceledException)
        {
            return Finish(UploadOutcome.Cancelled, UploadStage.Cancelled, bootloader == null
                ? "Cancelled."
                : "Cancelled. The board stays in its bootloader until it is flashed or replugged.");
        }
        catch (Exception ex)
        {
            return Finish(UploadOutcome.Failed, UploadStage.Failed, "Upload failed: " + ex.Message);
        }
        finally
        {
            bootloader?.Dispose();
        }
    }

    private BootloaderInfo? _lastRejected;

    private async Task<(IBootloader Bootloader, BootloaderInfo Info)?> WaitForBootloaderAsync(ApjFirmware firmware, CancellationToken cancel)
    {
        var start = Environment.TickCount64;
        var rejected = new List<BootloaderInfo>();
        var rebootTried = RebootToBootloader == null;
        var logged = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var left = BootloaderTimeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - start);
            if (left <= TimeSpan.Zero)
                return null;

            Report(UploadStage.WaitingForBootloader, -1,
                $"Waiting for the bootloader ({Math.Ceiling(left.TotalSeconds):0} s)… plug the board in, or unplug and replug it.");

            // listing ports can stall on some drivers: keep it off the caller's thread
            var ports = await Task.Run(() =>
            {
                try
                {
                    return _listPorts();
                }
                catch (Exception)
                {
                    return (IReadOnlyList<string>)[];
                }
            }, CancellationToken.None).ConfigureAwait(false);

            var hit = await BootloaderScanner.ProbeAsync(_factory, ports, info => info.BoardId == firmware.BoardId, rejected,
                line =>
                {
                    // say it once per board, not every round
                    lock (logged)
                    {
                        if (!logged.Add(line))
                            return;
                    }

                    Log?.Invoke(line);
                }, cancel).ConfigureAwait(false);
            if (hit != null)
            {
                if (cancel.IsCancellationRequested)
                {
                    hit.Value.Bootloader.Dispose();
                    cancel.ThrowIfCancellationRequested();
                }

                return hit;
            }

            if (rejected.Count > 0)
                _lastRejected = rejected[^1];

            if (!rebootTried)
            {
                rebootTried = true;
                Report(UploadStage.RebootingToBootloader, -1, "Asking the board to reboot into its bootloader…");
                try
                {
                    if (await RebootToBootloader!(cancel).ConfigureAwait(false))
                        Log?.Invoke("Sent reboot-to-bootloader over MAVLink.");
                    else
                        Log?.Invoke("No ArduPilot answered over MAVLink; unplug the board and plug it back in.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log?.Invoke("Reboot to bootloader failed: " + ex.Message);
                }
            }

            await Task.Delay(ScanInterval, cancel).ConfigureAwait(false);
        }
    }

    private void OnFlashProgress(FlashStep step, double percent)
    {
        var (stage, text) = step switch
        {
            FlashStep.Erase => (UploadStage.Erasing, "Erasing…"),
            FlashStep.EraseExternal => (UploadStage.Erasing, "Erasing external flash…"),
            FlashStep.ProgramExternal => (UploadStage.Programming, "Programming external flash…"),
            FlashStep.VerifyExternal => (UploadStage.Verifying, "Verifying external flash…"),
            FlashStep.Program => (UploadStage.Programming, "Programming…"),
            _ => (UploadStage.Verifying, "Verifying…"),
        };
        Report(stage, percent, text);
    }

    private void Report(UploadStage stage, double percent, string text)
    {
        Stage = stage;
        Progress?.Report(new UploadProgress(stage, percent, text));
    }

    private UploadResult Finish(UploadOutcome outcome, UploadStage stage, string message, BootloaderInfo? board = null)
    {
        Report(stage, stage == UploadStage.Done ? 100 : -1, message);
        Log?.Invoke(message);
        return new UploadResult(outcome, message, board);
    }
}
