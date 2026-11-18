using GMap.NET;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.FlightPlan;

namespace MissionPlanner.App.Tests.FlightPlan;

/// <summary>Runs everything inline: the tests are the UI thread.</summary>
internal sealed class InlineDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
}

/// <summary>Scripted dialogs: prompts answer from a queue, confirms answer <see cref="ConfirmAnswer"/>.</summary>
internal sealed class ScriptedDialogs : IDialogService
{
    public Queue<string?> Prompts { get; } = new();

    public bool ConfirmAnswer { get; set; } = true;

    public string? OpenPath { get; set; }

    public string? SavePath { get; set; }

    public List<string> Messages { get; } = new();

    public List<string> Confirms { get; } = new();

    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK")
    {
        Confirms.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string?> PromptAsync(string title, string message, string initialValue = "") =>
        Task.FromResult(Prompts.Count > 0 ? Prompts.Dequeue() : initialValue);

    public Task<string?> OpenFileAsync(string title, params FileFilter[] filters) => Task.FromResult(OpenPath);

    public Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters) => Task.FromResult(SavePath);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
}

internal sealed class NoWindows : IWindowService
{
    public List<PageViewModel> Shown { get; } = new();

    public Func<PageViewModel, bool> DialogResult { get; set; } = _ => false;

    public void Show(PageViewModel page, double width = 1100, double height = 750) => Shown.Add(page);

    public Task<bool> ShowDialogAsync(PageViewModel page, double width = 1000, double height = 700)
    {
        Shown.Add(page);
        return Task.FromResult(DialogResult(page));
    }
}

internal static class PlanTestSupport
{
    /// <summary>A Flight Plan page over fresh services; terrain is a fixed 600 m.</summary>
    public static (FlightPlanViewModel Vm, ScriptedDialogs Dialogs, NoWindows Windows) CreatePage()
    {
        MapTiles.Initialize();
        var dialogs = new ScriptedDialogs();
        var windows = new NoWindows();
        var services = new AppServices(new InlineDispatcher(), dialogs, windows);
        var vm = new FlightPlanViewModel(services)
        {
            TerrainLookup = (_, _) => 600,
            // the repository's copy; the test host has none next to it
            Catalog = MavCmdCatalog.Parse(RepoFile(MavCmdCatalog.FileName), MavCmdSection.Copter),
            // the page keeps these in the (shared) settings: start every test from the same values
            DefaultAlt = 100,
            DefaultFrame = AltFrame.Relative,
            WaypointRadius = 2,
            LoiterRadius = 45,
        };
        return (vm, dialogs, windows);
    }

    /// <summary>A file of the repository (mavcmd.xml lives at its root).</summary>
    public static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, name);
            if (File.Exists(path) && Directory.Exists(Path.Combine(dir.FullName, "ExtLibs")))
                return path;
        }

        throw new FileNotFoundException("repository file not found", name);
    }

    public static MissionItem Wp(double lat, double lng, float alt = 100,
        MAVLink.MAV_CMD cmd = MAVLink.MAV_CMD.WAYPOINT) =>
        new() { Command = cmd, Lat = lat, Lng = lng, Alt = alt };

    public static PointLatLng P(double lat, double lng) => new(lat, lng);

    /// <summary>A point <paramref name="north"/> and <paramref name="east"/> metres from a base point.</summary>
    public static PointLatLng Offset(PointLatLng from, double north, double east)
    {
        const double r = 6378137.0;
        var dLat = north / r * 180 / Math.PI;
        var dLng = east / (r * Math.Cos(from.Lat * Math.PI / 180)) * 180 / Math.PI;
        return new PointLatLng(from.Lat + dLat, from.Lng + dLng);
    }
}
