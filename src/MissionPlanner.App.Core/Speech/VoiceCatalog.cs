using MissionPlanner.App.Core.Localization;

namespace MissionPlanner.App.Core.Speech;

/// <summary>The kind of neural model, which decides how sherpa-onnx is configured.</summary>
public enum VoiceModelKind
{
    /// <summary>Piper (VITS): one .onnx and tokens.txt.</summary>
    Piper,

    /// <summary>Kokoro: model, voices.bin, tokens.txt and its own pronunciation lexicons.</summary>
    Kokoro,

    /// <summary>KittenTTS: model, voices.bin and tokens.txt.</summary>
    Kitten,
}

/// <summary>One speaker of a voice model.</summary>
/// <param name="Id">sherpa-onnx's speaker id (sid).</param>
/// <param name="British">Pronounced with the British lexicon (Kokoro's UK speakers).</param>
public sealed record VoiceSpeaker(int Id, string Name, string Accent, string Gender, bool British = false)
{
    public string DisplayName => $"{Name} ({Accent}, {Gender})";
}

/// <summary>
/// A downloadable neural voice: an archive of sherpa-onnx's
/// <see href="https://github.com/k2-fsa/sherpa-onnx/releases/tag/tts-models">tts-models</see> release.
/// </summary>
/// <param name="Id">Stable id, saved in the settings.</param>
/// <param name="Archive">The release asset (.tar.bz2); its name without the extension is the folder inside.</param>
/// <param name="DownloadBytes">The asset's size, checked after the download.</param>
/// <param name="InstalledBytes">Roughly what the installed files take (the archive's unused parts are skipped).</param>
/// <param name="ModelFile">The .onnx file in the folder.</param>
/// <param name="License">The licence of the model (and the data it was trained on).</param>
/// <param name="LicenseUrl">Where the licence and the model card are.</param>
/// <param name="SharedLexicon">Needs sherpa-onnx's English pronunciation lexicon (<see cref="VoiceCatalog.SharedLexicon"/>).</param>
public sealed record VoiceInfo(
    string Id,
    string Name,
    string Description,
    string Language,
    string Quality,
    string Archive,
    long DownloadBytes,
    long InstalledBytes,
    VoiceModelKind Kind,
    string ModelFile,
    IReadOnlyList<VoiceSpeaker> Speakers,
    int DefaultSpeaker,
    string License,
    string LicenseUrl,
    bool SharedLexicon,
    bool Recommended = false)
{
    /// <summary>The folder name inside the archive, and under the voices folder once installed.</summary>
    public string Folder => Archive.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) ? Archive[..^8] : Path.GetFileNameWithoutExtension(Archive);

    public Uri Url => new(VoiceCatalog.ReleaseUrl + Archive);

    /// <summary>What the download takes from the network: the archive, and the shared lexicon if missing.</summary>
    public long TotalDownloadBytes(bool lexiconInstalled) =>
        DownloadBytes + (SharedLexicon && !lexiconInstalled ? VoiceCatalog.SharedLexicon.Bytes : 0);

    public VoiceSpeaker SpeakerOrDefault(int id) =>
        Speakers.FirstOrDefault(s => s.Id == id) ?? Speakers.FirstOrDefault(s => s.Id == DefaultSpeaker) ?? Speakers[0];
}

/// <summary>A file downloaded once and shared by several voices.</summary>
public sealed record SharedVoiceFile(string Name, long Bytes, string License)
{
    public Uri Url => new(VoiceCatalog.ReleaseUrl + Name);
}

/// <summary>
/// The neural voices the app offers, all downloaded on demand into the data folder (nothing ships with the
/// installer but the engine). Chosen for a licence that allows redistribution and any use, commercial
/// included: Kokoro and KittenTTS are Apache-2.0. Piper voices are not offered: the ones in the
/// release that sherpa-onnx 2.x pronounces correctly (U.S. English, eSpeak "en-us" phonemes) are fine-tuned
/// from the research-only Lessac voice or are non-commercial, and the public-domain ones were trained on
/// British phonemes, which the 2.x lexicon front end does not produce.
/// </summary>
public static class VoiceCatalog
{
    public const string ReleaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/";

    public const string KokoroLicense =
        "Apache-2.0 (Kokoro-82M by hexgrad, trained on permissive and synthetic data; lexicons from misaki, Apache-2.0)";

    /// <summary>sherpa-onnx 2.x reads U.S. English through this lexicon (word → phonemes) for Piper and Kitten models.</summary>
    public static readonly SharedVoiceFile SharedLexicon = new("lexicon-en-us.txt", 6_305_773,
        "sherpa-onnx's English pronunciation lexicon (CMUdict and eSpeak NG word lists)");

    /// <summary>Kokoro v1.0's English speakers, best first (grades from the model's VOICES.md).</summary>
    public static readonly IReadOnlyList<VoiceSpeaker> KokoroSpeakers =
    [
        new(3, "Heart", "US", L.N("female")),
        new(2, "Bella", "US", L.N("female")),
        new(6, "Nicole", "US", L.N("female, soft")),
        new(1, "Aoede", "US", L.N("female")),
        new(5, "Kore", "US", L.N("female")),
        new(9, "Sarah", "US", L.N("female")),
        new(7, "Nova", "US", L.N("female")),
        new(10, "Sky", "US", L.N("female")),
        new(0, "Alloy", "US", L.N("female")),
        new(4, "Jessica", "US", L.N("female")),
        new(8, "River", "US", L.N("female")),
        new(16, "Michael", "US", L.N("male")),
        new(14, "Fenrir", "US", L.N("male")),
        new(18, "Puck", "US", L.N("male")),
        new(12, "Echo", "US", L.N("male")),
        new(13, "Eric", "US", L.N("male")),
        new(15, "Liam", "US", L.N("male")),
        new(17, "Onyx", "US", L.N("male")),
        new(11, "Adam", "US", L.N("male")),
        new(21, "Emma", "UK", L.N("female"), British: true),
        new(22, "Isabella", "UK", L.N("female"), British: true),
        new(20, "Alice", "UK", L.N("female"), British: true),
        new(23, "Lily", "UK", L.N("female"), British: true),
        new(26, "George", "UK", L.N("male"), British: true),
        new(25, "Fable", "UK", L.N("male"), British: true),
        new(27, "Lewis", "UK", L.N("male"), British: true),
        new(24, "Daniel", "UK", L.N("male"), British: true),
    ];

    /// <summary>KittenTTS 0.8's voices (speaker ids follow the model's expr-voice-2-m ... expr-voice-5-f).</summary>
    public static readonly IReadOnlyList<VoiceSpeaker> KittenSpeakers =
    [
        new(1, "Bella", "US", L.N("female")),
        new(3, "Luna", "US", L.N("female")),
        new(5, "Rosie", "US", L.N("female")),
        new(7, "Kiki", "US", L.N("female")),
        new(0, "Jasper", "US", L.N("male")),
        new(2, "Bruno", "US", L.N("male")),
        new(4, "Hugo", "US", L.N("male")),
        new(6, "Leo", "US", L.N("male")),
    ];

    public static readonly IReadOnlyList<VoiceInfo> All =
    [
        new("kokoro-v1", "Kokoro",
            L.N("Natural, clear assistant voices, the closest to a drone app's voice prompts. Needs a recent PC: about 1 s to prepare a short sentence."),
            L.N("English (US, UK)"), L.N("Best"), "kokoro-multi-lang-v1_0.tar.bz2", 349_886_476, 367_000_000,
            VoiceModelKind.Kokoro, "model.onnx", KokoroSpeakers, 3,
            KokoroLicense, "https://huggingface.co/hexgrad/Kokoro-82M", SharedLexicon: false, Recommended: true),
        new("kokoro-v1-compact", "Kokoro compact",
            L.N("The same voices in an 8-bit model: a third of the download, but about three times slower to speak on most PCs."),
            L.N("English (US, UK)"), L.N("Very good"), "kokoro-int8-multi-lang-v1_0.tar.bz2", 132_353_410, 155_000_000,
            VoiceModelKind.Kokoro, "model.int8.onnx", KokoroSpeakers, 3,
            KokoroLicense, "https://huggingface.co/hexgrad/Kokoro-82M", SharedLexicon: false),
        new("kitten-nano-v0.8", "Kitten TTS nano",
            L.N("A small, quick model with eight voices."),
            L.N("English (US)"), L.N("Good"), "kitten-nano-en-v0_8-fp32.tar.bz2", 63_815_222, 61_000_000,
            VoiceModelKind.Kitten, "model.fp32.onnx", KittenSpeakers, 1,
            "Apache-2.0 (KittenTTS by KittenML)", "https://github.com/KittenML/KittenTTS", SharedLexicon: true),
    ];

    public static VoiceInfo? Find(string? id) => All.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    public static VoiceInfo Recommended => All.First(v => v.Recommended);
}
