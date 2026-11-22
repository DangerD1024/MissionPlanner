using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Usb;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Avalonia;
using Avalonia.Android;

namespace MissionPlanner.Mobile.Android;

/// <summary>
/// Turns with the phone (or as set on the Link tab); the screen stays on while the app is open.
/// Plugging in a telemetry radio or flight controller offers to open the app (device_filter.xml).
/// </summary>
[Activity(
    Label = "Mission Planner",
    Theme = "@style/MissionPlanner.Theme",
    Icon = "@drawable/icon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.FullUser,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
[IntentFilter([UsbManager.ActionUsbDeviceAttached])]
[MetaData(UsbManager.ActionUsbDeviceAttached, Resource = "@xml/device_filter")]
public sealed class MainActivity : AvaloniaMainActivity
{
    private static MainActivity? _current;
    private static TaskCompletionSource<bool>? _permissions;
    private const int PermissionRequest = 4711;

    /// <summary>Asks for runtime permissions (Bluetooth); true when all are granted.</summary>
    public static Task<bool> RequestPermissionsAsync(string[] permissions)
    {
        var activity = _current;
        if (activity == null)
            return Task.FromResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _permissions, tcs)?.TrySetResult(false);
        activity.RunOnUiThread(() => activity.RequestPermissions(permissions, PermissionRequest));
        return tcs.Task;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == PermissionRequest)
            Interlocked.Exchange(ref _permissions, null)?.TrySetResult(grantResults.Length > 0 && grantResults.All(g => g == Permission.Granted));
    }

    protected override void OnDestroy()
    {
        if (_current == this)
            _current = null;
        base.OnDestroy();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent?.Action == UsbManager.ActionUsbDeviceAttached)
            MissionPlannerApplication.Usb?.NotifyChanged();
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        _current = this;
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        // the app's dark background also behind the camera cutout and system bars in landscape
        Window?.SetBackgroundDrawable(new global::Android.Graphics.Drawables.ColorDrawable(global::Android.Graphics.Color.ParseColor("#0B0E13")));
        if (MobileApp.Shell is { } shell)
            Follow(shell);
        else
            MobileApp.ShellCreated += Follow;
    }

    private void Follow(global::MissionPlanner.App.Core.ViewModels.Mobile.MobileShellViewModel shell)
    {
        Apply(shell.Orientation);
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(shell.Orientation))
                Apply(shell.Orientation);
        };
    }

    private void Apply(global::MissionPlanner.App.Core.ViewModels.Mobile.ScreenOrientationMode mode) =>
        RequestedOrientation = mode switch
        {
            global::MissionPlanner.App.Core.ViewModels.Mobile.ScreenOrientationMode.Portrait => ScreenOrientation.UserPortrait,
            global::MissionPlanner.App.Core.ViewModels.Mobile.ScreenOrientationMode.Landscape => ScreenOrientation.UserLandscape,
            _ => ScreenOrientation.FullUser,
        };
}

/// <summary>Places the data folder and the data files before the shared app starts.</summary>
[Application]
public sealed class MissionPlannerApplication : AvaloniaAndroidApplication<MobileApp>
{
    public MissionPlannerApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    /// <summary>The phone's USB host, for serial links to radios and flight controllers.</summary>
    public static AndroidUsbHost? Usb { get; private set; }

    public override void OnCreate()
    {
        PrepareData();
        // not gated on FEATURE_USB_HOST: some phones (and the emulator) leave it out although
        // OTG works; without a host the device list is simply empty
        if (GetSystemService(UsbService) is UsbManager)
        {
            Usb = new AndroidUsbHost(this);
            global::MissionPlanner.App.Core.Links.LinkFactory.UsbHost = Usb;
        }

        if (GetSystemService(BluetoothService) is global::Android.Bluetooth.BluetoothManager { Adapter: not null })
            global::MissionPlanner.App.Core.Links.LinkFactory.BluetoothHost = new AndroidBluetoothHost(this);

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
