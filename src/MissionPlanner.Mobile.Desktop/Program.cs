using Avalonia;
using MissionPlanner.Mobile;

namespace MissionPlanner.Mobile.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // its own settings and logs, so the preview never changes the desktop app's
        MobileApp.DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MissionPlannerPhonePreview");
        MobileApp.StartLink = Environment.GetEnvironmentVariable("MP_CONNECT");
        MobileApp.StartLandscape = args.Contains("--landscape");
        AppBuilder.Configure<MobileApp>().UsePlatformDetect().LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}
