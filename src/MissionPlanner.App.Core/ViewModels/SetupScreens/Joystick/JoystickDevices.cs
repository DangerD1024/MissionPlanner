namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;

/// <summary>Hat switch position, as SDL reports it (SDL_HAT_UP = 1, RIGHT = 2, DOWN = 4, LEFT = 8).</summary>
[Flags]
public enum HatDirection : byte
{
    Centered = 0,
    Up = 1,
    Right = 2,
    Down = 4,
    Left = 8,
}

/// <summary>One reading of every axis (-32768..32767), button and hat of a device.</summary>
public sealed class JoystickSnapshot(short[] axes, bool[] buttons, HatDirection[] hats)
{
    public static readonly JoystickSnapshot Empty = new([], [], []);

    public IReadOnlyList<short> Axes { get; } = axes;

    public IReadOnlyList<bool> Buttons { get; } = buttons;

    public IReadOnlyList<HatDirection> Hats { get; } = hats;

    public short Axis(int index) => index >= 0 && index < Axes.Count ? Axes[index] : (short)0;

    public bool Button(int index) => index >= 0 && index < Buttons.Count && Buttons[index];

    public HatDirection Hat(int index) => index >= 0 && index < Hats.Count ? Hats[index] : HatDirection.Centered;
}

/// <summary>A joystick or gamepad as a backend lists it.</summary>
/// <param name="Id">Backend-specific handle (SDL instance id, /dev/input path).</param>
/// <param name="Name">Product name; also the key of the saved profile.</param>
public sealed record JoystickDeviceInfo(string Id, string Name, string Backend)
{
    public override string ToString() => Name;
}

/// <summary>An open device. <see cref="Read"/> is cheap: backends sample on their own thread.</summary>
public interface IJoystickDevice : IDisposable
{
    JoystickDeviceInfo Info { get; }

    int AxisCount { get; }

    int ButtonCount { get; }

    int HatCount { get; }

    /// <summary>False once the device was unplugged.</summary>
    bool IsAttached { get; }

    JoystickSnapshot Read();
}

/// <summary>A way to reach joysticks on this machine (SDL2, Linux joydev).</summary>
public interface IJoystickBackend : IDisposable
{
    string Name { get; }

    /// <summary>What the page tells the user about joystick support.</summary>
    string Status { get; }

    IReadOnlyList<JoystickDeviceInfo> GetDevices();

    /// <summary>Opens a listed device; throws <see cref="IOException"/> when it is gone.</summary>
    IJoystickDevice Open(JoystickDeviceInfo device);
}

/// <summary>No joystick support on this machine; the page explains why.</summary>
public sealed class NoJoystickBackend(string status) : IJoystickBackend
{
    public string Name => "none";

    public string Status { get; } = status;

    public IReadOnlyList<JoystickDeviceInfo> GetDevices() => [];

    public IJoystickDevice Open(JoystickDeviceInfo device) => throw new IOException(Status);

    public void Dispose()
    {
    }
}

/// <summary>
/// Chooses the joystick backend: SDL2 through its native library when present (Linux, macOS,
/// Windows), else on Linux the kernel joystick devices /dev/input/js* (Mission Planner's
/// JoystickLinux approach). <c>MP_JOYSTICK_BACKEND=sdl|joydev|none</c> forces one.
/// </summary>
public static class JoystickBackends
{
    public static IJoystickBackend Create()
    {
        var forced = Environment.GetEnvironmentVariable("MP_JOYSTICK_BACKEND")?.Trim().ToLowerInvariant();
        string? sdlError = null;

        if (forced is null or "" or "sdl" or "sdl2")
        {
            if (Sdl2JoystickBackend.TryCreate(out var sdl, out sdlError))
                return sdl!;
            if (forced is "sdl" or "sdl2")
                return new NoJoystickBackend(sdlError!);
        }

        if (forced is null or "" or "joydev" && OperatingSystem.IsLinux())
            return new LinuxJoydevBackend(sdlError != null ? MissingSdlHint() + " Using the Linux joystick driver (/dev/input/js*)." : null);

        if (forced == "none")
            return new NoJoystickBackend("Joystick input is turned off (MP_JOYSTICK_BACKEND=none).");

        return new NoJoystickBackend("No joystick support: " + MissingSdlHint());
    }

    internal static string MissingSdlHint()
    {
        if (OperatingSystem.IsLinux())
            return "SDL2 was not found (libSDL2-2.0.so.0; install the libsdl2-2.0-0 package).";
        if (OperatingSystem.IsMacOS())
            return "SDL2 was not found (libSDL2-2.0.0.dylib; install it with 'brew install sdl2').";
        if (OperatingSystem.IsWindows())
            return "SDL2 was not found (put SDL2.dll from libsdl.org next to the application).";
        return "SDL2 was not found.";
    }
}
