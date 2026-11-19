using log4net;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// The write and refresh sequences shared by the parameter pages, following Mission Planner's
/// Full Parameter List: confirm the list of changes, write, then report failures (a dialog),
/// success and needed reboots (toasts) and parameter-count changes.
/// </summary>
public static class ParamWriteFlow
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ParamWriteFlow));

    /// <summary>Changes listed one by one in the confirmation; more are summarised.</summary>
    public const int MaxListed = 20;

    public static string ConfirmText(IReadOnlyList<string> lines)
    {
        if (lines.Count <= MaxListed)
        {
            return $"You are about to change {lines.Count} parameter{(lines.Count == 1 ? "" : "s")}. Please review the changes below:\n\n" +
                   string.Join("\n", lines) + "\n\nDo you want to proceed?";
        }

        return $"You are about to change {lines.Count} parameters. Are you sure you want to proceed?\n\n" +
               string.Join("\n", lines.Take(MaxListed)) + $"\n... and {lines.Count - MaxListed} more";
    }

    /// <summary>
    /// Asks, writes every pending edit and removes the ones that were written from
    /// <paramref name="edits"/>. Returns the names written (empty when nothing was written).
    /// Call on the UI thread.
    /// </summary>
    public static async Task<IReadOnlyCollection<string>> WriteAsync(AppServices s, ParamEditBuffer edits, Action<string>? status = null)
    {
        if (edits.Count == 0)
        {
            await s.Dialogs.ShowMessageAsync("No changes", "No parameters were changed.");
            return [];
        }

        if (!s.Session.IsConnected)
        {
            await s.Dialogs.ShowMessageAsync("Not connected", "Connect to the vehicle to write parameters.", MessageKind.Error);
            return [];
        }

        var lines = edits.Describe(s.Params.Get);
        if (!await s.Dialogs.ConfirmAsync("Confirm Parameter Changes", ConfirmText(lines), "Write"))
            return [];

        var values = new Dictionary<string, double>(edits.Pending, StringComparer.Ordinal);
        // "Writing NAME (40%)" in the page's status line while the busy bar runs
        var progress = new Progress<OperationProgress>(p =>
            status?.Invoke(values.Count > 1 && p.Percent is > 0 and < 100 ? $"{p.Status} ({p.Percent}%)" : p.Status));
        Params.ParamWriteResult result;
        try
        {
            result = await s.Params.SetManyAsync(values, progress);
        }
        catch (Exception ex)
        {
            Log.Error("write params", ex);
            await s.Dialogs.ShowMessageAsync("Write failed", "Writing parameters failed: " + ex.Message, MessageKind.Error);
            return [];
        }

        var written = values.Keys.Except(result.Failed, StringComparer.Ordinal).ToList();
        edits.RemoveAll(written);

        if (result.Failed.Count > 0)
        {
            await s.Dialogs.ShowMessageAsync("Not all parameters saved",
                $"{written.Count} of {values.Count} parameters were saved.\n\nFailed: {string.Join(", ", result.Failed)}",
                MessageKind.Error);
        }
        else
        {
            // no dialog for a successful write: a toast that goes away by itself
            s.Notify(written.Count == 1 ? L.Tr("1 parameter successfully saved.") : L.Format("{0} parameters successfully saved.", written.Count));
        }

        if (result.RebootRequired)
            s.Notify("Reboot is required for some parameters to take effect.", MessageKind.Warning);

        // enabling a feature adds parameters; ParamService refetches the list when disarmed
        var list = s.Session.Vehicle.param;
        if (list.TotalReceived != list.TotalReported && s.Session.Cs.armed)
        {
            await s.Dialogs.ShowMessageAsync("Params",
                "The number of available parameters changed. Until a full parameter refresh is done, some parameters will not be available.",
                MessageKind.Warning);
            list.TotalReported = list.TotalReceived;
        }

        return written;
    }

    /// <summary>Downloads the list again, warning first when the vehicle is armed. Returns true if it ran.</summary>
    public static async Task<bool> RefreshAsync(AppServices s, Action<string>? status = null)
    {
        if (!s.Session.IsConnected)
        {
            await s.Dialogs.ShowMessageAsync("Not connected", "Connect to the vehicle to read its parameters.", MessageKind.Error);
            return false;
        }

        if (s.Session.Cs.armed &&
            !await s.Dialogs.ConfirmAsync("Refresh Params", "The vehicle is armed.\n\nUpdate the parameter list?\nDON'T DO THIS IF YOU ARE IN THE AIR.", "Refresh"))
            return false;

        try
        {
            await s.Params.RefreshAsync(new Progress<OperationProgress>(p => status?.Invoke(p.Status)));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("refresh params", ex);
            await s.Dialogs.ShowMessageAsync("Error", "Error receiving parameters: " + ex.Message, MessageKind.Error);
            return false;
        }
    }
}
