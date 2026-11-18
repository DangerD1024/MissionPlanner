using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

/// <summary>RC input and servo output PWM by channel number, without reflection.</summary>
public static class Channels
{
    public const double PwmMin = 800;
    public const double PwmMax = 2200;

    public static float In(CurrentState cs, int ch) => ch switch
    {
        1 => cs.ch1in, 2 => cs.ch2in, 3 => cs.ch3in, 4 => cs.ch4in,
        5 => cs.ch5in, 6 => cs.ch6in, 7 => cs.ch7in, 8 => cs.ch8in,
        9 => cs.ch9in, 10 => cs.ch10in, 11 => cs.ch11in, 12 => cs.ch12in,
        13 => cs.ch13in, 14 => cs.ch14in, 15 => cs.ch15in, 16 => cs.ch16in,
        _ => 0,
    };

    public static float Out(CurrentState cs, int ch) => ch switch
    {
        1 => cs.ch1out, 2 => cs.ch2out, 3 => cs.ch3out, 4 => cs.ch4out,
        5 => cs.ch5out, 6 => cs.ch6out, 7 => cs.ch7out, 8 => cs.ch8out,
        9 => cs.ch9out, 10 => cs.ch10out, 11 => cs.ch11out, 12 => cs.ch12out,
        13 => cs.ch13out, 14 => cs.ch14out, 15 => cs.ch15out, 16 => cs.ch16out,
        17 => cs.ch17out, 18 => cs.ch18out, 19 => cs.ch19out, 20 => cs.ch20out,
        21 => cs.ch21out, 22 => cs.ch22out, 23 => cs.ch23out, 24 => cs.ch24out,
        25 => cs.ch25out, 26 => cs.ch26out, 27 => cs.ch27out, 28 => cs.ch28out,
        29 => cs.ch29out, 30 => cs.ch30out, 31 => cs.ch31out, 32 => cs.ch32out,
        _ => 0,
    };

    /// <summary>"No GPS", "3D fix", "RTK fixed"... for <c>cs.gpsstatus</c>.</summary>
    public static string GpsText(float fix) => (int)fix switch
    {
        0 => "No GPS",
        1 => "No fix",
        2 => "2D fix",
        3 => "3D fix",
        4 => "DGPS",
        5 => "RTK float",
        6 => "RTK fixed",
        _ => "GPS " + (int)fix,
    };
}

/// <summary>A live PWM value for a bar (RC input or servo output).</summary>
public sealed partial class PwmChannel(int number, string label) : ObservableObject
{
    public int Number { get; } = number;

    public string Label { get; } = label;

    [ObservableProperty]
    private double _pwm;

    /// <summary>Updates the value; returns true if it changed.</summary>
    public bool Set(double pwm)
    {
        if (Pwm == pwm)
            return false;
        Pwm = pwm;
        return true;
    }
}
