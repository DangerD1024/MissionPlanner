using System.Globalization;
using log4net;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Links;

/// <summary>
/// Mission Planner's connection options (Config > Planner), with its own settings keys so values
/// carry over: the ground station's MAVLink system ID, the serial port resets, loading the
/// mission after a connect, and no RC receiver (which the shared CurrentState reads itself).
/// </summary>
public static class ConnectOptions
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ConnectOptions));

    public const string GcsIdKey = "gcsid";
    public const string ResetOnConnectKey = "CHK_resetapmonconnect";
    public const string Esp32RtsKey = "CHK_rtsresetesp32";
    public const string LoadWaypointsKey = "loadwpsonconnect";

    /// <summary>Read by CurrentState: a vehicle without an RC receiver does not warn "No RC Receiver".</summary>
    public const string NoRcReceiverKey = "norcreceiver";

    /// <summary>Mission Planner's GCS ID unless set.</summary>
    public const int DefaultGcsId = 255;

    /// <summary>The stored GCS ID, 1 to 255 (anything else counts as the default).</summary>
    public static int GcsId =>
        uint.TryParse(Settings.Instance[GcsIdKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id is >= 1 and <= 255
            ? (int)id
            : DefaultGcsId;

    public static bool ResetOnConnect => Settings.Instance.GetBoolean(ResetOnConnectKey);

    public static bool Esp32RtsFix => Settings.Instance.GetBoolean(Esp32RtsKey);

    public static bool LoadWaypoints => Settings.Instance.GetBoolean(LoadWaypointsKey);

    public static bool NoRcReceiver => Settings.Instance.GetBoolean(NoRcReceiverKey);

    /// <summary>
    /// Gives the link the stored GCS ID (the system ID of every packet the app sends), as Mission
    /// Planner does at start-up. The library keeps one ID for all links.
    /// </summary>
    public static void ApplyGcsId() => MAVLinkInterface.gcssysid = (uint)GcsId;

    /// <summary>Stores and applies a GCS ID (clamped to 1..255); returns the one in use.</summary>
    public static int SetGcsId(int id)
    {
        id = Math.Clamp(id, 1, 255);
        Settings.Instance[GcsIdKey] = id.ToString(CultureInfo.InvariantCulture);
        ApplyGcsId();
        return id;
    }

    /// <summary>
    /// Mission Planner's serial options, applied to a link before it opens (MainV2's connect): the
    /// ESP32 fix holds RTS through the open (the runtime's serial port, mainly on Windows), and
    /// "reset on connect" pulses DTR and RTS so the board restarts. Desktop serial ports only: the
    /// phone's USB host and Bluetooth links are left alone. A failed reset does not stop the connect.
    /// </summary>
    public static void BeforeOpen(ICommsSerial link)
    {
        if (link is not SerialPort serial)
            return;

        serial.espFix = Esp32RtsFix;
        if (!ResetOnConnect)
            return;

        try
        {
            Log.Info($"reset on connect: toggling DTR on {link.PortName}");
            link.DtrEnable = false;
            link.RtsEnable = false;
            link.toggleDTR();
        }
        catch (Exception ex)
        {
            Log.Warn("reset on connect (toggle DTR)", ex);
        }
    }
}
