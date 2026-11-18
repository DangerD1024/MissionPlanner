using System.Diagnostics;
using log4net;

namespace MissionPlanner.App.Core.ViewModels.Simulation;

/// <summary>
/// A running simulator with its console output captured line by line. Killed with the app (the
/// WinForms page kills it from a finalizer). Output and exit callbacks arrive on worker threads.
/// </summary>
public sealed class SitlProcess : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SitlProcess));
    private static readonly List<SitlProcess> Running = new();
    private static int _exitHooked;

    private readonly Process _process;

    private SitlProcess(Process process) => _process = process;

    public int Id => _process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// How the simulator is started: in its model folder, HOME pointing there (it writes
    /// eeprom.bin and logs relative to it), output redirected so the page can show it; on Windows
    /// the sitl folder goes on PATH so the cygwin DLLs are found.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string executable, string arguments, string workingDirectory, string sitlDir,
        bool windows)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        psi.Environment["HOME"] = workingDirectory;
        if (windows)
        {
            var path = psi.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH");
            psi.Environment["PATH"] = sitlDir + ";" + workingDirectory + ";" + path;
        }

        return psi;
    }

    /// <summary>Starts the process; throws when it cannot be started. <paramref name="exited"/> follows the last output line.</summary>
    public static SitlProcess Start(ProcessStartInfo psi, Action<string> output, Action<int> exited)
    {
        HookExit();
        var process = new Process { StartInfo = psi };
        var sitl = new SitlProcess(process);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                output(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                output(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("The simulator did not start.");
        lock (Running)
            Running.Add(sitl);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // waits for the exit and for the redirected output to drain
        _ = Task.Run(async () =>
        {
            int code;
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                code = process.ExitCode;
            }
            catch (Exception)
            {
                code = -1;
            }

            lock (Running)
                Running.Remove(sitl);
            exited(code);
        });
        return sitl;
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn("kill sitl", ex);
        }
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        try
        {
            return _process.WaitForExit(timeout);
        }
        catch (Exception)
        {
            return true;
        }
    }

    public void Dispose()
    {
        Kill();
        lock (Running)
            Running.Remove(this);
        _process.Dispose();
    }

    private static void HookExit()
    {
        if (Interlocked.Exchange(ref _exitHooked, 1) != 0)
            return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            SitlProcess[] all;
            lock (Running)
                all = Running.ToArray();
            foreach (var p in all)
                p.Kill();
        };
    }
}
