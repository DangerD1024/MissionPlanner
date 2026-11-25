namespace MissionPlanner.App.Core;

/// <summary>
/// Adds the app to the Linux applications menu (a freedesktop .desktop file in the user's data
/// folder), so launchers and docks show it with Mission Planner's icon.
/// </summary>
public static class DesktopEntry
{
    public const string Id = "mission-planner-next";

    /// <summary>The app's window class; docks use it to tie the window to this menu entry.</summary>
    public const string WindowClass = "MissionPlanner.App";

    public static string ApplicationsDir => Path.Combine(DataHome, "applications");

    public static string EntryPath => Path.Combine(ApplicationsDir, Id + ".desktop");

    public static string IconPath => Path.Combine(DataHome, "icons", "hicolor", "128x128", "apps", Id + ".png");

    private static string DataHome =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    /// <summary>The command that starts this copy of the app (apphost, or dotnet + dll).</summary>
    public static string ExecCommand(string? processPath, string entryAssembly)
    {
        if (processPath != null && !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return Quote(processPath);
        return $"{Quote(processPath ?? "dotnet")} {Quote(entryAssembly)}";
    }

    public static string Content(string exec, string iconPath) =>
        $"""
         [Desktop Entry]
         Type=Application
         Name=Mission Planner 3
         GenericName=Ground Control Station
         Comment=ArduPilot ground control station ({AppVersion.Display})
         Exec={exec}
         Icon={iconPath}
         Terminal=false
         Categories=Utility;
         Keywords=ArduPilot;drone;UAV;MAVLink;GCS;
         StartupWMClass={WindowClass}

         """;

    /// <summary>Writes the menu entry and its icon; returns a message for the user.</summary>
    public static string Install()
    {
        if (!OperatingSystem.IsLinux())
            return "Only needed on Linux.";

        var icon = Path.Combine(AppContext.BaseDirectory, "mission-planner-128.png");
        Directory.CreateDirectory(Path.GetDirectoryName(IconPath)!);
        if (File.Exists(icon))
            File.Copy(icon, IconPath, overwrite: true);

        var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? Path.Combine(AppContext.BaseDirectory, "MissionPlanner.App.dll");
        Directory.CreateDirectory(ApplicationsDir);
        File.WriteAllText(EntryPath, Content(ExecCommand(Environment.ProcessPath, entry), IconPath));
        return $"Added to the applications menu ({EntryPath}).";
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}
