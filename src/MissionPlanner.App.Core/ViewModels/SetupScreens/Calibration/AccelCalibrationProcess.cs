using POS = MAVLink.ACCELCAL_VEHICLE_POS;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

public enum AccelCalState
{
    /// <summary>Not calibrating.</summary>
    Idle,

    /// <summary>PREFLIGHT_CALIBRATION sent, waiting for the command to go out.</summary>
    Starting,

    /// <summary>Calibrating; the vehicle has not asked for a position yet (or is sampling the last one).</summary>
    WaitingForVehicle,

    /// <summary>The vehicle asked for <see cref="AccelCalibrationProcess.Position"/>; the user confirms when it is placed.</summary>
    PositionRequested,

    Succeeded,
    Failed,
}

/// <summary>
/// The 6-position accelerometer calibration protocol, as Mission Planner runs it
/// (ConfigAccelerometerCalibration.cs): PREFLIGHT_CALIBRATION with param5 = 1 starts it; the
/// vehicle asks for each orientation with a COMMAND_LONG ACCELCAL_VEHICLE_POS (param1 = position)
/// and explains itself in STATUSTEXT; the ground station confirms a position by sending the same
/// command back with that position; a STATUSTEXT containing "calibration successful" or
/// "calibration failed" ends it. Message events come from <see cref="ICalibrationLink"/> on the
/// link's thread; <see cref="Changed"/> and <see cref="StatusText"/> are raised on that thread.
/// </summary>
public sealed class AccelCalibrationProcess
{
    private readonly ICalibrationLink _link;
    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = new();

    public AccelCalibrationProcess(ICalibrationLink link) => _link = link;

    public AccelCalState State { get; private set; }

    /// <summary>The last orientation the vehicle asked for, while calibrating.</summary>
    public POS? Position { get; private set; }

    /// <summary>The position most recently confirmed to the vehicle (it then samples and asks for the next).</summary>
    public POS? Confirmed { get; private set; }

    /// <summary>The vehicle's latest instruction ("Place vehicle LEFT and press any key."), or ours.</summary>
    public string Instruction { get; private set; } = "";

    /// <summary>How many positions have been confirmed in this run.</summary>
    public int ConfirmCount { get; private set; }

    public bool IsActive => State is AccelCalState.Starting or AccelCalState.WaitingForVehicle or AccelCalState.PositionRequested;

    public bool CanConfirm => State == AccelCalState.PositionRequested;

    /// <summary>Raised whenever the state, position or instruction changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Every STATUSTEXT from the vehicle while calibrating.</summary>
    public event EventHandler<string>? StatusText;

    /// <summary>
    /// Starts a calibration. Listens before sending so the first position request cannot be
    /// missed. Returns false when the command could not be sent; throws when the link fails.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        lock (_gate)
        {
            if (IsActive)
                return false;
            Unsubscribe();
            State = AccelCalState.Starting;
            Position = null;
            Confirmed = null;
            ConfirmCount = 0;
            Instruction = "Starting the calibration…";
            _subscriptions.Add(_link.Subscribe(MAVLink.MAVLINK_MSG_ID.STATUSTEXT, HandleMessage));
            _subscriptions.Add(_link.Subscribe(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, HandleMessage));
        }

        RaiseChanged();

        bool ok;
        try
        {
            // the library returns at once for this one; the vehicle answers with position requests
            ok = await _link.CommandAsync(MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, 0, 0, 0, 0, 1, 0, 0).ConfigureAwait(false);
        }
        catch
        {
            Reset("");
            throw;
        }

        if (!ok)
        {
            Reset("The vehicle did not accept the calibration command.");
            return false;
        }

        lock (_gate)
        {
            if (State == AccelCalState.Starting)
            {
                State = AccelCalState.WaitingForVehicle;
                Instruction = "Waiting for the vehicle…";
            }
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Tells the vehicle the requested position is in place. Returns false when none is pending.</summary>
    public async Task<bool> ConfirmPositionAsync()
    {
        POS pos;
        lock (_gate)
        {
            if (State != AccelCalState.PositionRequested || Position is not { } p)
                return false;
            pos = p;
        }

        await _link.SendAsync(new MAVLink.mavlink_command_long_t
        {
            command = (ushort)MAVLink.MAV_CMD.ACCELCAL_VEHICLE_POS,
            param1 = (float)pos,
        }).ConfigureAwait(false);

        lock (_gate)
        {
            // a duplicate confirmation is harmless (the vehicle ignores positions it is not waiting for),
            // so the button stays usable until the vehicle moves on
            if (Confirmed != pos)
                ConfirmCount++;
            Confirmed = pos;
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Stops listening. The vehicle abandons the calibration by itself after its timeout.</summary>
    public void Cancel() => Reset("");

    /// <summary>Feeds one message from the vehicle (STATUSTEXT or COMMAND_LONG).</summary>
    public void HandleMessage(MAVLink.MAVLinkMessage msg)
    {
        if (msg.msgid == (uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT)
            HandleStatusText(StatusTextDecoder.Decode(msg));
        else if (msg.msgid == (uint)MAVLink.MAVLINK_MSG_ID.COMMAND_LONG)
        {
            var cmd = msg.ToStructure<MAVLink.mavlink_command_long_t>();
            HandleCommandLong(cmd.command, cmd.param1);
        }
    }

    public void HandleStatusText(string text)
    {
        if (!IsActive)
            return;

        StatusText?.Invoke(this, text);

        var lower = text.ToLowerInvariant();
        if (lower.Contains("calibration successful"))
        {
            Finish(true, text);
            return;
        }

        if (lower.Contains("calibration failed"))
        {
            Finish(false, text);
            return;
        }

        // Mission Planner only shows the messages about placing the vehicle or the calibration itself
        if (lower.Contains("place vehicle") || lower.Contains("calibration"))
        {
            lock (_gate)
                Instruction = text;
            RaiseChanged();
        }
    }

    public void HandleCommandLong(ushort command, float param1)
    {
        if (!IsActive || command != (ushort)MAVLink.MAV_CMD.ACCELCAL_VEHICLE_POS)
            return;

        var pos = (POS)(int)param1;
        switch (pos)
        {
            case POS.SUCCESS:
                Finish(true, "Calibration successful");
                return;
            case POS.FAILED:
                Finish(false, "Calibration FAILED");
                return;
            case < POS.LEVEL or > POS.BACK:
                return;
        }

        lock (_gate)
        {
            if (!IsActive)
                return;
            State = AccelCalState.PositionRequested;
            Position = pos;
            Instruction = "Please place vehicle " + pos;
        }

        RaiseChanged();
    }

    private void Finish(bool success, string text)
    {
        lock (_gate)
        {
            if (!IsActive)
                return;
            Unsubscribe();
            State = success ? AccelCalState.Succeeded : AccelCalState.Failed;
            Position = null;
            Instruction = text;
        }

        RaiseChanged();
    }

    private void Reset(string instruction)
    {
        lock (_gate)
        {
            Unsubscribe();
            State = AccelCalState.Idle;
            Position = null;
            Confirmed = null;
            Instruction = instruction;
        }

        RaiseChanged();
    }

    private void Unsubscribe()
    {
        foreach (var s in _subscriptions)
            s.Dispose();
        _subscriptions.Clear();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
