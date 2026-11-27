using System.Globalization;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Speech;

public enum SpeechEngineKind
{
    /// <summary>The platform's speech program (SAPI, say, spd-say / espeak-ng).</summary>
    System,

    /// <summary>A downloaded neural voice (sherpa-onnx), the system voice while none is ready.</summary>
    Neural,
}

/// <summary>
/// Which voice speaks and how (settings keys <c>next_speech_*</c>, new in this app). The neural engine is
/// the default: it speaks as soon as a voice has been downloaded, the system voice until then.
/// </summary>
/// <param name="VoiceId">A <see cref="VoiceCatalog"/> id; empty: the recommended voice if installed, else the first installed.</param>
/// <param name="Speaker">The voice's speaker id; -1: the voice's default.</param>
/// <param name="Speed">Speaking rate, 1 = normal (0.5..2).</param>
/// <param name="Volume">0..100 %.</param>
/// <param name="SystemVoice">The system program's voice name; empty: its default.</param>
public sealed record SpeechVoiceSettings(
    SpeechEngineKind Engine = SpeechEngineKind.Neural,
    string VoiceId = "",
    int Speaker = -1,
    double Speed = 1.0,
    int Volume = 100,
    string SystemVoice = "",
    double SystemSpeed = 1.0,
    int SystemVolume = 100)
{
    public const string EngineKey = "next_speech_engine";
    public const string VoiceKey = "next_speech_voice";
    public const string SpeakerKey = "next_speech_speaker";
    public const string RateKey = "next_speech_rate";
    public const string VolumeKey = "next_speech_volume";
    public const string SystemVoiceKey = "next_speech_system_voice";
    public const string SystemRateKey = "next_speech_system_rate";
    public const string SystemVolumeKey = "next_speech_system_volume";

    public static readonly string[] Keys = [EngineKey, VoiceKey, SpeakerKey, RateKey, VolumeKey, SystemVoiceKey, SystemRateKey, SystemVolumeKey];

    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 2.0;

    public static SpeechVoiceSettings Load()
    {
        var s = Settings.Instance;
        return new SpeechVoiceSettings(
            string.Equals(s[EngineKey], "system", StringComparison.OrdinalIgnoreCase) ? SpeechEngineKind.System : SpeechEngineKind.Neural,
            s[VoiceKey] ?? "",
            Int(s[SpeakerKey], -1),
            Math.Clamp(Double(s[RateKey], 1.0), MinSpeed, MaxSpeed),
            Math.Clamp(Int(s[VolumeKey], 100), 0, 100),
            s[SystemVoiceKey] ?? "",
            Math.Clamp(Double(s[SystemRateKey], 1.0), MinSpeed, MaxSpeed),
            Math.Clamp(Int(s[SystemVolumeKey], 100), 0, 100));
    }

    public void Save()
    {
        var s = Settings.Instance;
        s[EngineKey] = Engine == SpeechEngineKind.System ? "system" : "neural";
        s[VoiceKey] = VoiceId;
        s[SpeakerKey] = Speaker.ToString(CultureInfo.InvariantCulture);
        s[RateKey] = Math.Round(Speed, 2).ToString(CultureInfo.InvariantCulture);
        s[VolumeKey] = Volume.ToString(CultureInfo.InvariantCulture);
        s[SystemVoiceKey] = SystemVoice;
        s[SystemRateKey] = Math.Round(SystemSpeed, 2).ToString(CultureInfo.InvariantCulture);
        s[SystemVolumeKey] = SystemVolume.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The options for the system speech program.</summary>
    public SystemVoiceOptions SystemOptions => new(SystemVoice, SystemSpeed, SystemVolume);

    /// <summary>
    /// The voice that speaks for these settings: the chosen one when installed, else the recommended or the
    /// first installed one; the chosen one (not installed) or null when there is none.
    /// </summary>
    public VoiceInfo? ResolveVoice(Func<VoiceInfo, bool> installed)
    {
        var chosen = VoiceCatalog.Find(VoiceId);
        if (chosen != null && installed(chosen))
            return chosen;
        var recommended = VoiceCatalog.Recommended;
        if (installed(recommended))
            return recommended;
        return VoiceCatalog.All.FirstOrDefault(installed) ?? chosen;
    }

    private static int Int(string? s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double Double(string? s, double fallback) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;
}

/// <summary>Voice, rate and volume for the system speech program (each program takes what it supports).</summary>
public sealed record SystemVoiceOptions(string? Voice = null, double Speed = 1.0, int Volume = 100)
{
    public static readonly SystemVoiceOptions Default = new();

    public bool HasVoice => !string.IsNullOrWhiteSpace(Voice);

    public bool NormalSpeed => Math.Abs(Speed - 1.0) < 0.01;

    public bool FullVolume => Volume >= 100;

    public bool IsDefault => !HasVoice && NormalSpeed && FullVolume;
}
