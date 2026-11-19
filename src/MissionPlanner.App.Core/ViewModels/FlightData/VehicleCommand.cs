namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>
/// One command for the selected vehicle, as the extra Flight Data tabs build it. Building is
/// pure (and tested); <see cref="Send"/> blocks on the link, so call it from a worker thread.
/// A command with <see cref="IsInt"/> goes out as COMMAND_INT (x/y in degrees × 1e7).
/// </summary>
public sealed record VehicleCommand(
    MAVLink.MAV_CMD Command,
    float P1 = 0,
    float P2 = 0,
    float P3 = 0,
    float P4 = 0,
    float P5 = 0,
    float P6 = 0,
    float P7 = 0)
{
    public bool IsInt { get; init; }

    public int X { get; init; }

    public int Y { get; init; }

    public MAVLink.MAV_FRAME Frame { get; init; } = MAVLink.MAV_FRAME.GLOBAL;

    /// <summary>A COMMAND_INT with a position.</summary>
    public static VehicleCommand Int(MAVLink.MAV_CMD command, double lat, double lng, float z, MAVLink.MAV_FRAME frame,
        float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0) =>
        new(command, p1, p2, p3, p4, 0, 0, z)
        {
            IsInt = true,
            X = (int)Math.Round(lat * 1e7),
            Y = (int)Math.Round(lng * 1e7),
            Frame = frame,
        };

    /// <summary>
    /// Sends the command. With <paramref name="requireAck"/> it waits for COMMAND_ACK (true when
    /// accepted; the library throws <see cref="TimeoutException"/> without an answer); without, it
    /// returns true once sent.
    /// </summary>
    public bool Send(MAVLinkInterface mav, uint sysid, byte compid, bool requireAck = true) =>
        IsInt
            ? mav.doCommandInt(sysid, compid, Command, P1, P2, P3, P4, X, Y, P7, requireAck, frame: Frame)
            : mav.doCommand(sysid, compid, Command, P1, P2, P3, P4, P5, P6, P7, requireAck);

    public override string ToString() =>
        IsInt
            ? $"{Command}({P1}, {P2}, {P3}, {P4}, x={X}, y={Y}, z={P7}, {Frame})"
            : $"{Command}({P1}, {P2}, {P3}, {P4}, {P5}, {P6}, {P7})";
}

/// <summary>
/// What the extra tabs need from the DATA screen: its command runner (one command at a time,
/// outcome shown on the Actions status line and as a toast) and the connection state.
/// </summary>
public interface IFlightCommands
{
    /// <summary>Connected and no other command running.</summary>
    bool CanCommand { get; }

    bool IsConnected { get; }

    /// <summary>Runs a blocking library call on a worker thread with the selected vehicle's ids and reports the outcome.</summary>
    Task RunAsync(string what, Func<MAVLinkInterface, uint, byte, CurrentState, bool> action, string okText, string failText);

    /// <summary>Shows a note (toast and status line).</summary>
    void Report(string text, bool isError);
}
