using System.Runtime.InteropServices;
using System.Text;

namespace MissionPlanner.App.Core.Speech;

/// <summary>
/// Paths the speech engine can open. On Windows sherpa-onnx opens its model files with narrow (ANSI)
/// paths, so a folder name in another script ("OneDrive\Документы\...") reaches it as "?????" and the
/// model "does not exist". Such a path is handed over by its 8.3 short name, which is plain ASCII.
/// </summary>
public static class NativePaths
{
    public static bool IsAscii(string path)
    {
        foreach (var c in path)
            if (c > 0x7E)
                return false;
        return true;
    }

    /// <summary>
    /// The path as the native engine can open it: unchanged when it is ASCII (or not on Windows), else its
    /// Windows short name; null when there is none in ASCII (8.3 names switched off on that drive).
    /// </summary>
    public static string? ForNative(string path) => ForNative(path, OperatingSystem.IsWindows(), ShortName);

    internal static string? ForNative(string path, bool windows, Func<string, string?> shortName)
    {
        if (!windows || IsAscii(path))
            return path;
        return shortName(path) is { } s && IsAscii(s) ? s : null;
    }

    /// <summary>
    /// The voices folder: <paramref name="preferred"/> when the engine can open files in it, else a folder
    /// with an ASCII path that every user can write (Windows: ProgramData).
    /// </summary>
    public static string VoicesRoot(string preferred)
    {
        if (!OperatingSystem.IsWindows() || IsAscii(preferred))
            return preferred;
        try
        {
            // a short name exists only for a folder that exists
            Directory.CreateDirectory(preferred);
            if (ForNative(preferred) != null)
                return preferred;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return IsAscii(common) ? Path.Combine(common, "Mission Planner", "voices") : preferred;
    }

    private static string? ShortName(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var buffer = new StringBuilder(1024);
        var n = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
        return n > 0 && n < buffer.Capacity ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);
}
