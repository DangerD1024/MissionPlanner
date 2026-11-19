using MissionPlanner.App.Core.Params;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Tests.ConfigScreens;

internal static class ParamPagesTestSupport
{
    /// <summary>
    /// Gives the services the parameter documentation shipped with the app (the legacy
    /// ParameterMetaDataBackup.xml copied next to the tests), without any download: the index is
    /// built offline first, so ParamService finds it ready.
    /// </summary>
    public static async Task LoadOfflineDocsAsync(AppServices services)
    {
        var cs = services.Session.Cs;
        // without a vehicle the session does not know the firmware: these are Copter's parameters
        if (!services.Session.IsConnected)
            cs.firmware = MissionPlanner.ArduPilot.Firmwares.ArduCopter2;
        // before AUTOPILOT_VERSION the version is 0.0, which ParamMetadata cannot key (ToString(3))
        if (cs.version is { Build: < 0 })
            cs.version = null;
        var docs = await ParamMetadata.LoadAsync(cs.firmware, cs.version, download: false);
        Assert.IsTrue(docs.ContainsKey("LOG_BITMASK"),
            $"the offline parameter documentation is missing ({docs.Count} entries, running directory {MissionPlanner.Utilities.Settings.GetRunningDirectory()})");
        await services.Params.LoadMetadataAsync();
        Assert.IsNotNull(services.Params.Meta("LOG_BITMASK"));
    }
}
