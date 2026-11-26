using System.Diagnostics;
using System.Text;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>A program to run: its path, arguments (passed as they are, no shell) and extra environment variables.</summary>
public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    public string CommandLine => string.Join(" ", new[] { FileName }.Concat(Arguments).Select(Quote));

    private static string Quote(string s) => s.Length > 0 && s.All(c => !char.IsWhiteSpace(c) && c != '"') ? s : "\"" + s + "\"";
}

/// <summary>Runs external tools (dfu-util); a fake one in tests.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the program to its end. Each line it prints goes to <paramref name="output"/> (true for
    /// standard error), split at '\r' as well as '\n' so progress bars that redraw one line arrive
    /// as they change. Cancelling kills the program and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <returns>The exit code.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The program could not be started.</exception>
    Task<int> RunAsync(ProcessRequest request, Action<string, bool> output, CancellationToken cancel);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public static SystemProcessRunner Instance { get; } = new();

    public async Task<int> RunAsync(ProcessRequest request, Action<string, bool> output, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in request.Arguments)
            psi.ArgumentList.Add(a);
        if (request.Environment != null)
        {
            foreach (var (k, v) in request.Environment)
                psi.Environment[k] = v;
        }

        using var process = Process.Start(psi) ?? throw new System.ComponentModel.Win32Exception("The program did not start.");
        var stdout = PumpAsync(process.StandardOutput, line => output(line, false));
        var stderr = PumpAsync(process.StandardError, line => output(line, true));
        try
        {
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // already gone
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> line)
    {
        var splitter = new LineSplitter(line);
        var buffer = new char[1024];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                splitter.Add(buffer.AsSpan(0, n));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the stream closes when the process is killed
        }

        splitter.Flush();
    }
}

/// <summary>Splits streamed text into lines at '\n', '\r\n' and a bare '\r'; empty lines are dropped.</summary>
public sealed class LineSplitter(Action<string> line)
{
    private readonly StringBuilder _current = new();

    public void Add(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is '\r' or '\n')
                Flush();
            else
                _current.Append(c);
        }
    }

    public void Flush()
    {
        if (_current.Length == 0)
            return;
        var s = _current.ToString();
        _current.Clear();
        if (!string.IsNullOrWhiteSpace(s))
            line(s);
    }
}
