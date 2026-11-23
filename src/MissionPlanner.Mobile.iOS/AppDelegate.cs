using Avalonia;
using Avalonia.iOS;
using Foundation;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.ViewModels.Mobile;
using UIKit;

namespace MissionPlanner.Mobile.iOS;

/// <summary>
/// Starts the shared phone app: places the data folder, gives it Bluetooth LE, keeps the screen
/// on, and turns the screen as set on the Link tab.
/// </summary>
[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<MobileApp>
{
    private ScreenOrientationMode _orientation = ScreenOrientationMode.Auto;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        PrepareData();
        LinkFactory.BluetoothHost = new IosBluetoothHost();
        if (MobileApp.Shell is { } shell)
            Follow(shell);
        else
            MobileApp.ShellCreated += Follow;
        // the screen stays on while the app is open; settings are saved when it is closed
        UIApplication.SharedApplication.IdleTimerDisabled = true;
        UIApplication.Notifications.ObserveWillTerminate((_, _) =>
        {
            if (MobileApp.AppServices is { } services)
                MobileApp.Stop(services);
        });
        return base.CustomizeAppBuilder(builder);
    }

    [Export("application:supportedInterfaceOrientationsForWindow:")]
    public UIInterfaceOrientationMask SupportedOrientations(UIApplication application, UIWindow? forWindow) => Mask(_orientation);

    private static UIInterfaceOrientationMask Mask(ScreenOrientationMode mode) => mode switch
    {
        ScreenOrientationMode.Portrait => UIInterfaceOrientationMask.Portrait,
        ScreenOrientationMode.Landscape => UIInterfaceOrientationMask.Landscape,
        _ => UIInterfaceOrientationMask.AllButUpsideDown,
    };

    private void Follow(MobileShellViewModel shell)
    {
        _orientation = shell.Orientation;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MobileShellViewModel.Orientation))
                return;
            _orientation = shell.Orientation;
            UIApplication.SharedApplication.InvokeOnMainThread(ApplyOrientation);
        };
    }

    private void ApplyOrientation()
    {
        var mask = Mask(_orientation);
        foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
        {
            if (scene is not UIWindowScene windowScene)
                continue;
            foreach (var window in windowScene.Windows)
            {
                if (OperatingSystem.IsIOSVersionAtLeast(16))
                    window.RootViewController?.SetNeedsUpdateOfSupportedInterfaceOrientations();
            }

            if (OperatingSystem.IsIOSVersionAtLeast(16))
                windowScene.RequestGeometryUpdate(new UIWindowSceneGeometryPreferencesIOS(mask), _ => { });
        }

        if (!OperatingSystem.IsIOSVersionAtLeast(16))
            UIViewController.AttemptRotationToDeviceOrientation();
    }

    /// <summary>
    /// Settings, logs and tiles live in Application Support; the shared libraries read
    /// mavcmd.xml and the parameter documentation from the data folder, so the bundled copies are
    /// put there (again when the app is updated).
    /// </summary>
    private static void PrepareData()
    {
        var root = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0].Path
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        MobileApp.DataDirectory = root;
        var dir = Path.Combine(root, "Mission Planner");
        Directory.CreateDirectory(dir);
        var stamp = Path.Combine(dir, ".assets-version");
        var version = NSBundle.MainBundle.InfoDictionary?["CFBundleVersion"]?.ToString() + "/" +
                      NSBundle.MainBundle.InfoDictionary?["CFBundleShortVersionString"]?.ToString();
        if (File.Exists(stamp) && File.ReadAllText(stamp) == version)
            return;
        foreach (var name in new[] { "mavcmd.xml", "ParameterMetaDataBackup.xml" })
        {
            var source = Path.Combine(NSBundle.MainBundle.BundlePath, name);
            try
            {
                if (File.Exists(source))
                    File.Copy(source, Path.Combine(dir, name), overwrite: true);
            }
            catch (IOException)
            {
                // the app runs without them (generic waypoint labels, no parameter help)
            }
        }

        File.WriteAllText(stamp, version);
    }
}
