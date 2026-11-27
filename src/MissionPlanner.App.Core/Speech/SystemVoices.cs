using System.Diagnostics;
using System.Text.RegularExpressions;
using log4net;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Core.Speech;

/// <summary>A voice of the system speech program: the name it takes, and how it is shown.</summary>
public sealed record SystemVoice(string Id, string Label);

/// <summary>
/// The voices the system speech program has installed: SAPI's on Windows, <c>say -v ?</c> on macOS,
/// <c>spd-say -L</c> or <c>espeak-ng --voices</c> on Linux. Empty when the program cannot list them.
/// </summary>
public static partial class SystemVoices
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SystemVoices));

    private const string WindowsList =
        "Add-Type -AssemblyName System.Speech; (New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices() | " +
        "Where-Object { $_.Enabled } | ForEach-Object { $_.VoiceInfo.Name + '|' + $_.VoiceInfo.Culture.Name + '|' + $_.VoiceInfo.Gender }";

    public static async Task<IReadOnlyList<SystemVoice>> ListAsync(CancellationToken cancel = default)
    {
        var find = SpeechBackend.FindOnPath;
        try
        {
            switch (SpeechBackend.Current)
            {
                case SpeechBackend.Os.Windows:
                    return find("powershell.exe") is { } ps
                        ? ParseWindows(await RunAsync(ps, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", WindowsList], cancel))
                        : [];
                case SpeechBackend.Os.MacOs:
                    return find("say") is { } say ? ParseSay(await RunAsync(say, ["-v", "?"], cancel)) : [];
                default:
                    if (find("spd-say") is { } spd)
                        return ParseSpdSay(await RunAsync(spd, ["-L"], cancel));
                    if ((find("espeak-ng") ?? find("espeak")) is { } espeak)
                        return ParseEspeak(await RunAsync(espeak, ["--voices"], cancel));
                    return [];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info("system voices", ex);
            return [];
        }
    }

    /// <summary>"Microsoft Zira Desktop|en-US|Female" lines.</summary>
    internal static IReadOnlyList<SystemVoice> ParseWindows(string output) =>
        Lines(output).Select(l => l.Split('|')).Where(p => p[0].Length > 0)
            .Select(p => new SystemVoice(p[0], p.Length >= 3 ? $"{p[0]} ({p[1]}, {p[2].ToLowerInvariant()})" : p[0]))
            .ToList();

    /// <summary>"Samantha            en_US    # Hello! My name is Samantha."</summary>
    internal static IReadOnlyList<SystemVoice> ParseSay(string output) =>
        Lines(output).Select(l => SayLine().Match(l)).Where(m => m.Success)
            .Select(m => new SystemVoice(m.Groups[1].Value.Trim(), $"{m.Groups[1].Value.Trim()} ({m.Groups[2].Value})"))
            .ToList();

    /// <summary>"     NAME                 LANGUAGE        VARIANT" then "English (America)    en-US    none".</summary>
    internal static IReadOnlyList<SystemVoice> ParseSpdSay(string output) =>
        Lines(output).Select(l => SpdLine().Match(l)).Where(m => m.Success && m.Groups[1].Value.Trim() != "NAME")
            .Select(m => new SystemVoice(m.Groups[1].Value.Trim(), $"{m.Groups[1].Value.Trim()} ({m.Groups[2].Value})"))
            .ToList();

    /// <summary>"Pty Language Age/Gender VoiceName File ..." then " 5  en-us  --/M  English_(America)  gmw/en-US".</summary>
    internal static IReadOnlyList<SystemVoice> ParseEspeak(string output)
    {
        var list = new List<SystemVoice>();
        foreach (var line in Lines(output).Skip(1))
        {
            var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 4)
                continue;
            list.Add(new SystemVoice(cols[1], $"{cols[3].Replace('_', ' ')} ({cols[1]})"));
        }

        return list;
    }

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0);

    private static async Task<string> RunAsync(string file, IReadOnlyList<string> args, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException(file + " did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var text = await p.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return text;
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            throw;
        }
    }

    [GeneratedRegex(@"^(.+?)\s+([a-z]{2,3}[_-][A-Za-z0-9]+)\s+#")]
    private static partial Regex SayLine();

    [GeneratedRegex(@"^\s*(.+?)\s{2,}(\S+)\s{2,}(\S+)\s*$")]
    private static partial Regex SpdLine();
}
