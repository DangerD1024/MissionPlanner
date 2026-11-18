using System.Diagnostics;
using Avalonia;

namespace MissionPlanner.App;

internal static class Program
{
    /// <summary>Time since the OS started the process, runtime startup included.</summary>
    public static TimeSpan SinceProcessStart
    {
        get
        {
            using var self = Process.GetCurrentProcess();
            return DateTime.Now - self.StartTime;
        }
    }

    // No Avalonia types or SynchronizationContext-dependent code before AppMain runs.
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // also used by the IDE previewer
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
