using log4net;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

/// <summary>How a u-blox M8P/F9P base fixes its own position.</summary>
/// <param name="FixedPosition">Known antenna position; null runs a survey-in.</param>
/// <param name="SurveyInSeconds">Minimum survey-in duration.</param>
/// <param name="SurveyInAccuracyM">Survey-in stops once the mean position is this accurate.</param>
/// <param name="Msm4">Output MSM4 instead of MSM7 (Mission Planner's "M8P fw 130+" option).</param>
public sealed record UbloxBaseSettings(PointLatLngAlt? FixedPosition, int SurveyInSeconds, double SurveyInAccuracyM, bool Msm4);

/// <summary>Survey-in progress from UBX-NAV-SVIN.</summary>
public sealed record UbloxSurveyIn(bool Valid, bool Active, uint DurationS, uint Observations, double MeanAccuracyM,
    double Lat, double Lng, double Alt);

/// <summary>Configures a u-blox receiver as an RTK base with Mission Planner's <see cref="Ubx"/> commands.</summary>
public static class UbloxBase
{
    /// <summary>
    /// Sets 460800 baud, stationary mode, the RTCM output messages, then survey-in or the fixed
    /// position. Blocking (several seconds); the port's baud rate ends at 460800.
    /// </summary>
    public static void Configure(ICommsSerial port, UbloxBaseSettings settings)
    {
        var ubx = new Ubx();
        ubx.SetupM8P(port, settings.Msm4);
        ApplyPosition(ubx, port, settings);
    }

    /// <summary>Discards the current survey-in and starts a new one (resets the receiver like Mission Planner).</summary>
    public static void RestartSurveyIn(ICommsSerial port, UbloxBaseSettings settings)
    {
        var ubx = new Ubx();
        ubx.SetupBasePos(port, PointLatLngAlt.Zero, 0, 0, disable: true);
        ubx.SetupM8P(port, settings.Msm4);
        ApplyPosition(ubx, port, settings with { FixedPosition = null });
    }

    /// <summary>Switches a configured base to a fixed position, or back to survey-in.</summary>
    public static void ApplyPosition(ICommsSerial port, UbloxBaseSettings settings) => ApplyPosition(new Ubx(), port, settings);

    private static void ApplyPosition(Ubx ubx, ICommsSerial port, UbloxBaseSettings settings)
    {
        var pos = settings.FixedPosition ?? PointLatLngAlt.Zero;
        ubx.SetupBasePos(port, pos, Math.Max(1, settings.SurveyInSeconds), Math.Max(0.01, settings.SurveyInAccuracyM), disable: false);
        // read back TMODE3 so the page shows what the receiver uses
        ubx.poll_msg(port, 0x06, 0x71);
    }
}

/// <summary>Follows UBX messages in the base station's stream: survey-in, base mode and fix.</summary>
public sealed class UbloxMonitor
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(UbloxMonitor));
    private readonly Ubx _parser = new();

    public UbloxSurveyIn? SurveyIn { get; private set; }

    /// <summary>TMODE3 mode reported by the receiver ("SurveyIn", "FixedLLA", ...).</summary>
    public string? Mode { get; private set; }

    public PointLatLngAlt? FixedPosition { get; private set; }

    public long Messages { get; private set; }

    public void Push(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (_parser.Read(b) > 0)
                Handle();
        }
    }

    private void Handle()
    {
        Messages++;
        try
        {
            var cls = _parser.@class;
            var id = _parser.subclass;
            if (cls == 0x01 && id == 0x3b)
            {
                var svin = _parser.packet.ByteArrayToStructure<Ubx.ubx_nav_svin>(6);
                var llh = new double[3];
                rtcm3.ecef2pos(svin.getECEF(), ref llh);
                SurveyIn = new UbloxSurveyIn(svin.valid == 1, svin.active == 1, svin.dur, svin.obs, svin.meanAcc / 10000.0,
                    llh[0] * rtcm3.R2D, llh[1] * rtcm3.R2D, llh[2]);
            }
            else if (cls == 0x06 && id == 0x71)
            {
                var tmode = _parser.packet.ByteArrayToStructure<Ubx.ubx_cfg_tmode3>(6);
                Mode = ((Ubx.ubx_cfg_tmode3.modeflags)tmode.flags).ToString();
                FixedPosition = tmode.getPointLatLngAlt();
            }
        }
        catch (Exception ex)
        {
            Log.Debug("ubx decode", ex);
        }
    }
}
