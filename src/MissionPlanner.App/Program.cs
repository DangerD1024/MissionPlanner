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
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
        // Opt-in software rendering (MP_SOFTWARE_RENDER=1): GPU/EGL window surfaces
        // cannot be read back by X11 screen capture on some desktops, which breaks
        // screenshots and remote-assist tooling. Default stays GPU.
        if (Environment.GetEnvironmentVariable("MP_SOFTWARE_RENDER") == "1")
            builder = builder.With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Software } });
        return builder;
    }
}
