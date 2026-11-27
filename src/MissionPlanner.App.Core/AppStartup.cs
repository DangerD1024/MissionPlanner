using log4net;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Services;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core;

/// <summary>
/// Process-wide setup of the shared libraries: settings file, the static UI hooks they call,
/// terrain and map caches. Runs once, before any view-model is created.
/// </summary>
public static class AppStartup
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AppStartup));

    /// <summary>The new app keeps its own settings file next to the WinForms app's config.xml.</summary>
    public const string ConfigFileName = "config.next.xml";

    private static IUiDispatcher? _dispatcher;
    private static IDialogService? _dialogs;

    public static void Initialize(IUiDispatcher dispatcher, IDialogService dialogs)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;

        // the shared libraries block thread-pool threads while they wait for the vehicle
        // (sync-over-async); start with enough threads that this never stalls other work
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 32), io);

        // must be set before the first Settings.Instance access, which loads the file
        Settings.FileName = ConfigFileName;
        try
        {
            Settings.Instance.Load();
        }
        catch (Exception ex)
        {
            Log.Error("settings", ex);
        }

        // saved telemetry rates and display units, as the WinForms app applies them at start
        ViewModels.ConfigScreens.PlannerSettings.ApplyStored();

        // UI language: a user's corrections in <data>/Localization/<culture>.json override the built-in tables
        Localization.Localizer.Current.AddSource(new Localization.DirectoryTranslationSource(
            Path.Combine(Settings.GetUserDataDirectory(), "Localization")));
        Localization.Localizer.Current.ApplySaved();

        // the libraries call this from any thread and wait for the answer
        CustomMessageBox.ShowEvent += ShowMessageBox;

        Links.BackgroundProgressReporter.Scope.Install();

        // terrain tiles download next to the other shared data
        srtm.datadirectory = Path.Combine(Settings.GetDataDirectory(), "srtm");

        // identifies the app to tile and download servers (OpenStreetMap's usage policy asks for it)
        Settings.Instance.UserAgent = $"MissionPlanner/{AppVersion.Display} (+https://ardupilot.org/planner/)";

        MapTiles.Initialize();
    }

    public static void Shutdown()
    {
        ViewModels.FlightData.AppSpeech.Shutdown();
        try
        {
            Settings.Instance.Save();
        }
        catch (Exception ex)
        {
            Log.Error("settings save", ex);
        }
    }

    private static CustomMessageBox.DialogResult ShowMessageBox(string text, string caption,
        CustomMessageBox.MessageBoxButtons buttons, CustomMessageBox.MessageBoxIcon icon, string yesText, string noText)
    {
        var dispatcher = _dispatcher;
        var dialogs = _dialogs;
        if (dispatcher == null || dialogs == null)
            return CustomMessageBox.DialogResult.OK;

        var kind = icon switch
        {
            CustomMessageBox.MessageBoxIcon.Error => MessageKind.Error,
            CustomMessageBox.MessageBoxIcon.Warning => MessageKind.Warning,
            _ => MessageKind.Info,
        };
        var asks = buttons is CustomMessageBox.MessageBoxButtons.YesNo or CustomMessageBox.MessageBoxButtons.OKCancel
            or CustomMessageBox.MessageBoxButtons.YesNoCancel or CustomMessageBox.MessageBoxButtons.RetryCancel;

        if (dispatcher.CheckAccess())
        {
            // cannot block the UI thread for an answer: show it, and decline anything that asks
            _ = dialogs.ShowMessageAsync(caption, text, kind);
            Log.Warn($"message box on UI thread answered without the user: {caption}: {text}");
            return asks ? Negative(buttons) : CustomMessageBox.DialogResult.OK;
        }

        if (!asks)
        {
            dispatcher.InvokeAsync(() => dialogs.ShowMessageAsync(caption, text, kind).ContinueWith(_ => true))
                .GetAwaiter().GetResult();
            return CustomMessageBox.DialogResult.OK;
        }

        var confirm = buttons == CustomMessageBox.MessageBoxButtons.YesNo || buttons == CustomMessageBox.MessageBoxButtons.YesNoCancel
            ? yesText
            : "OK";
        var ok = dispatcher.InvokeAsync(() => dialogs.ConfirmAsync(caption, text, confirm)).GetAwaiter().GetResult();
        if (ok)
            return buttons is CustomMessageBox.MessageBoxButtons.YesNo or CustomMessageBox.MessageBoxButtons.YesNoCancel
                ? CustomMessageBox.DialogResult.Yes
                : CustomMessageBox.DialogResult.OK;
        return Negative(buttons);
    }

    private static CustomMessageBox.DialogResult Negative(CustomMessageBox.MessageBoxButtons buttons) => buttons switch
    {
        CustomMessageBox.MessageBoxButtons.YesNo => CustomMessageBox.DialogResult.No,
        _ => CustomMessageBox.DialogResult.Cancel,
    };
}
