using log4net;

namespace MissionPlanner.App.Core.Mission;

/// <summary>
/// Mission Planner's "Load Waypoints on connect" (SETTINGS > Connection, key loadwpsonconnect):
/// after a connect the vehicle's mission is read into the plan, as PLAN's Read does, without
/// switching screens. DATA and PLAN show it from the shared mission store.
/// </summary>
public static class MissionOnConnect
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MissionOnConnect));

    /// <summary>
    /// Reads the mission when the option is on. Planned points are never replaced unasked: with
    /// <paramref name="ask"/> (a connect the user started) it asks as Read does, otherwise (auto-connect)
    /// they are kept. Returns whether the mission was read. Call on the UI thread.
    /// </summary>
    public static async Task<bool> RunAsync(AppServices services, bool ask)
    {
        if (!ConnectOptions.LoadWaypoints || !services.Session.IsConnected)
            return false;

        var store = services.Mission;
        if (store.Mission.Count > 0 &&
            (!ask || !await services.Dialogs.ConfirmAsync(L.Format("Read {0}", "mission"),
                "This will clear your existing points, Continue?", "Continue")))
            return false;

        try
        {
            await store.ReadFromVehicleAsync(services.Session, MAVLink.MAV_MISSION_TYPE.MISSION);
            var n = store.Mission.Count;
            services.Notify($"Read {n} mission item{(n == 1 ? "" : "s")} from the vehicle");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("load waypoints on connect", ex);
            services.Notify("Could not read the mission from the vehicle: " + ex.Message, MessageKind.Warning);
            return false;
        }
    }
}
