using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;

public enum JoystickInputKind
{
    /// <summary>The channel is not overridden; the RC receiver keeps it.</summary>
    None,

    /// <summary>An analogue axis.</summary>
    Axis,

    /// <summary>A hat's up/down presses move the channel up and down and it stays there.</summary>
    HatVertical,

    /// <summary>A hat's right/left presses move the channel up and down.</summary>
    HatHorizontal,

    /// <summary>Two PWM values switched by a button with the Button_axis0/1 function (Mission Planner's Custom1/2).</summary>
    ButtonAxis,

    /// <summary>A device button held down: 2000 while pressed, 1000 released (Reverse swaps).</summary>
    Button,

    /// <summary>The channel's trim (Mission Planner's "Pass").</summary>
    Trim,
}

/// <summary>What drives an RC channel: kind plus axis, hat or button-axis number.</summary>
[JsonConverter(typeof(JoystickInputJsonConverter))]
public readonly record struct JoystickInput(JoystickInputKind Kind, int Index = 0)
{
    public static readonly JoystickInput None = new(JoystickInputKind.None);

    public static JoystickInput Axis(int index) => new(JoystickInputKind.Axis, index);

    public string Label => Kind switch
    {
        JoystickInputKind.None => "None",
        JoystickInputKind.Axis => $"Axis {Index + 1}",
        JoystickInputKind.HatVertical => $"Hat {Index + 1} up/down",
        JoystickInputKind.HatHorizontal => $"Hat {Index + 1} left/right",
        JoystickInputKind.ButtonAxis => $"Button axis {Index + 1}",
        JoystickInputKind.Button => $"Button {Index + 1} (1000/2000)",
        JoystickInputKind.Trim => "Trim (fixed)",
        _ => Kind.ToString(),
    };

    /// <summary>Stable text form for profiles: none, axis:2, hatud:0, hatlr:0, buttonaxis:1, trim.</summary>
    public override string ToString() => Kind switch
    {
        JoystickInputKind.None => "none",
        JoystickInputKind.Axis => "axis:" + Index.ToString(CultureInfo.InvariantCulture),
        JoystickInputKind.HatVertical => "hatud:" + Index.ToString(CultureInfo.InvariantCulture),
        JoystickInputKind.HatHorizontal => "hatlr:" + Index.ToString(CultureInfo.InvariantCulture),
        JoystickInputKind.ButtonAxis => "buttonaxis:" + Index.ToString(CultureInfo.InvariantCulture),
        JoystickInputKind.Button => "button:" + Index.ToString(CultureInfo.InvariantCulture),
        JoystickInputKind.Trim => "trim",
        _ => "none",
    };

    public static JoystickInput Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return None;
        var parts = text.Trim().ToLowerInvariant().Split(':');
        var index = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 ? i : 0;
        return parts[0] switch
        {
            "axis" => new(JoystickInputKind.Axis, index),
            "hatud" => new(JoystickInputKind.HatVertical, index),
            "hatlr" => new(JoystickInputKind.HatHorizontal, index),
            "buttonaxis" => new(JoystickInputKind.ButtonAxis, Math.Min(index, 1)),
            "button" => new(JoystickInputKind.Button, index),
            "trim" or "pass" => new(JoystickInputKind.Trim),
            _ => None,
        };
    }
}

internal sealed class JoystickInputJsonConverter : JsonConverter<JoystickInput>
{
    public override JoystickInput Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JoystickInput.Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, JoystickInput value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>Button actions, named as in Mission Planner's ButtonFunctions so saved names carry over.</summary>
public enum JoystickButtonFunction
{
    None,
    ChangeMode,
    Arm,
    Disarm,
    TakeOff,
    Do_Set_Relay,
    Do_Repeat_Relay,
    Do_Set_Servo,
    Do_Repeat_Servo,
    Digicam_Control,
    Mount_Mode,
    Toggle_Pan_Stab,
    Gimbal_pnt_track,
    Mount_Control_0,
    Button_axis0,
    Button_axis1,
}

/// <summary>What the parameters of a button function mean (empty label: unused).</summary>
public sealed record ButtonFunctionInfo(JoystickButtonFunction Function, string Label, bool UsesMode, string P1, string P2, string P3, string P4,
    float D1 = 0, float D2 = 0, float D3 = 0, float D4 = 0)
{
    public static readonly IReadOnlyList<ButtonFunctionInfo> All =
    [
        new(JoystickButtonFunction.None, "No action", false, "", "", "", ""),
        new(JoystickButtonFunction.ChangeMode, "Change mode", true, "", "", "", ""),
        new(JoystickButtonFunction.Arm, "Arm", false, "", "", "", ""),
        new(JoystickButtonFunction.Disarm, "Disarm", false, "", "", "", ""),
        new(JoystickButtonFunction.TakeOff, "Take off (Guided)", false, "Altitude m", "", "", "", D1: 2),
        new(JoystickButtonFunction.Do_Set_Relay, "Relay on while pressed", false, "Relay", "", "", ""),
        new(JoystickButtonFunction.Do_Repeat_Relay, "Repeat relay", false, "Relay", "Repeat", "Cycle s", "", D2: 1, D3: 1),
        new(JoystickButtonFunction.Do_Set_Servo, "Set servo", false, "Servo", "PWM", "", "", D1: 5, D2: 1500),
        new(JoystickButtonFunction.Do_Repeat_Servo, "Repeat servo", false, "Servo", "PWM", "Repeat", "Cycle s", D1: 5, D2: 1500, D3: 1, D4: 1),
        new(JoystickButtonFunction.Digicam_Control, "Trigger camera", false, "", "", "", ""),
        new(JoystickButtonFunction.Mount_Mode, "Mount mode", false, "Mode", "", "", "", D1: 3),
        new(JoystickButtonFunction.Toggle_Pan_Stab, "Toggle pan stabilisation", false, "", "", "", ""),
        new(JoystickButtonFunction.Gimbal_pnt_track, "Gimbal: point at ROI", false, "", "", "", ""),
        new(JoystickButtonFunction.Mount_Control_0, "Gimbal: centre", false, "", "", "", ""),
        new(JoystickButtonFunction.Button_axis0, "Button axis 1", false, "PWM off", "PWM on", "", "", D1: 1100, D2: 1900),
        new(JoystickButtonFunction.Button_axis1, "Button axis 2", false, "PWM off", "PWM on", "", "", D1: 1100, D2: 1900),
    ];

    public static ButtonFunctionInfo For(JoystickButtonFunction f) => All.First(i => i.Function == f);

    public override string ToString() => Label;
}

public sealed class ChannelMapping
{
    /// <summary>RC channel, 1-16.</summary>
    public int Channel { get; set; }

    public JoystickInput Input { get; set; } = JoystickInput.None;

    public bool Reverse { get; set; }

    /// <summary>Mission Planner's expo, -100..100 (0 = linear).</summary>
    public int Expo { get; set; }

    /// <summary>Added to the output, µs.</summary>
    public int Trim { get; set; }
}

public sealed class ButtonMapping
{
    /// <summary>Device button number (0-based), -1 when unassigned.</summary>
    public int Button { get; set; } = -1;

    [JsonConverter(typeof(JsonStringEnumConverter<JoystickButtonFunction>))]
    public JoystickButtonFunction Function { get; set; }

    public string? Mode { get; set; }

    public float P1 { get; set; }

    public float P2 { get; set; }

    public float P3 { get; set; }

    public float P4 { get; set; }
}

/// <summary>Everything about one device: channel mapping, buttons and options. Saved per device.</summary>
public sealed class JoystickProfile
{
    public const int ChannelCount = 16;

    public string Device { get; set; } = "";

    /// <summary>Send MANUAL_CONTROL (x, y, z, r and buttons) instead of RC_CHANNELS_OVERRIDE.</summary>
    public bool ManualControl { get; set; }

    /// <summary>The sending toggle was on when the page/app was left; resume it on the next
    /// connect so a bench or pilot setup survives restarts without re-clicking Enable.</summary>
    public bool Sending { get; set; }

    /// <summary>Mix roll and pitch onto channels 1 and 2 for elevon aircraft (Mission Planner's option).</summary>
    public bool Elevons { get; set; }

    public List<ChannelMapping> Channels { get; set; } = new();

    public List<ButtonMapping> Buttons { get; set; } = new();

    public ChannelMapping Channel(int channel) => Channels.First(c => c.Channel == channel);

    /// <summary>Fills in missing channels 1-16 and button rows for the device.</summary>
    public JoystickProfile Normalise(int buttonRows)
    {
        Channels ??= new();
        Buttons ??= new();
        for (var ch = 1; ch <= ChannelCount; ch++)
        {
            if (!Channels.Any(c => c.Channel == ch))
                Channels.Add(new ChannelMapping { Channel = ch });
        }

        Channels = Channels.Where(c => c.Channel is >= 1 and <= ChannelCount).GroupBy(c => c.Channel).Select(g => g.First())
            .OrderBy(c => c.Channel).ToList();
        while (Buttons.Count < buttonRows)
            Buttons.Add(new ButtonMapping());
        return this;
    }

    public static JoystickProfile CreateDefault(string device, int buttonRows) => new JoystickProfile { Device = device }.Normalise(buttonRows);
}

/// <summary>Profiles as JSON files in the data folder ("joystick/&lt;device&gt;.json").</summary>
public sealed class JoystickProfileStore(string? folder = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Folder { get; } = folder ?? Path.Combine(Settings.GetUserDataDirectory(), "joystick");

    public string PathFor(string device) => Path.Combine(Folder, FileName(device) + ".json");

    public JoystickProfile? Load(string device)
    {
        try
        {
            var path = PathFor(device);
            if (!File.Exists(path))
                return null;
            var p = JsonSerializer.Deserialize<JoystickProfile>(File.ReadAllText(path), Options);
            if (p != null)
                p.Device = device;
            return p;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(JoystickProfile profile)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathFor(profile.Device), JsonSerializer.Serialize(profile, Options));
    }

    /// <summary>A file name made of the device name's letters and digits.</summary>
    public static string FileName(string device)
    {
        var sb = new StringBuilder();
        foreach (var c in device.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        var s = sb.ToString().Trim('.', '_');
        return s.Length == 0 ? "joystick" : s.Length > 100 ? s[..100] : s;
    }
}
