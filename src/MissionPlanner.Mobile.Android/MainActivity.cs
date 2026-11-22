using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Avalonia;
using Avalonia.Android;

namespace MissionPlanner.Mobile.Android;

/// <summary>Portrait only, the screen kept on while the app is open (a flight).</summary>
[Activity(
    Label = "Mission Planner",
    Theme = "@style/MissionPlanner.Theme",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
    }
}

/// <summary>Places the data folder and the data files before the shared app starts.</summary>
[Application]
public sealed class MissionPlannerApplication : AvaloniaAndroidApplication<MobileApp>
{
    public MissionPlannerApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        PrepareData();
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder);

    /// <summary>
    /// Settings, logs and tiles live in the app's private storage; the shared libraries read
    /// mavcmd.xml and the parameter documentation from the "running directory", which is that
    /// folder here, so the packaged copies are put there (again when the app is updated).
    /// </summary>
    private void PrepareData()
    {
        var root = FilesDir?.AbsolutePath ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        MobileApp.DataDirectory = root;
        var dir = Path.Combine(root, "Mission Planner");
        Directory.CreateDirectory(dir);
        var stamp = Path.Combine(dir, ".assets-version");
        var version = PackageManager?.GetPackageInfo(PackageName ?? "", 0)?.LongVersionCode.ToString() ?? "0";
        if (File.Exists(stamp) && File.ReadAllText(stamp) == version)
            return;
        foreach (var name in new[] { "mavcmd.xml", "ParameterMetaDataBackup.xml" })
        {
            try
            {
                using var src = Assets!.Open(name);
                using var dst = File.Create(Path.Combine(dir, name));
                src.CopyTo(dst);
            }
            catch (Exception ex) when (ex is IOException or Java.IO.IOException)
            {
                // the app runs without them (generic waypoint labels, no parameter help)
            }
        }

        File.WriteAllText(stamp, version);
    }
}
