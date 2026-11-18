using System.Collections.Concurrent;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>
/// A single "UI" thread with a synchronization context, so view-model continuations and
/// dispatcher posts run where they would in the app.
/// </summary>
public sealed class ToolsUiThread : SynchronizationContext, IUiDispatcher, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
    private readonly Thread _thread;

    public ToolsUiThread()
    {
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                callback(state);
        }) { IsBackground = true, Name = "test UI" };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => InvokeAsync(() => d(state)).GetAwaiter().GetResult();

    public bool CheckAccess() => Thread.CurrentThread == _thread;

    public void Post(Action action) => Post(_ => action(), null);

    public Task InvokeAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                done.SetResult(await action());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task;
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread and waits for it, including its awaits.</summary>
    public Task Run(Func<Task> work) => InvokeAsync<bool>(async () =>
    {
        await work();
        return true;
    });

    public Task<T> Run<T>(Func<T> work) => InvokeAsync(() => Task.FromResult(work()));

    public Task Run(Action work) => InvokeAsync(work);

    /// <summary>Waits until <paramref name="condition"/> (checked on the UI thread) holds.</summary>
    public async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 10000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!await Run(condition))
        {
            if (Environment.TickCount64 > until)
                Assert.Fail("timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(2000);
    }
}

/// <summary>Runs posts immediately, on the caller's thread.</summary>
public sealed class ToolsInlineDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
}

/// <summary>Dialogs with scripted answers; records what was asked.</summary>
public sealed class ToolsDialogs : IDialogService
{
    public Queue<bool> Confirms { get; } = new();
    public Queue<string?> Prompts { get; } = new();
    public Queue<string?> Files { get; } = new();
    public List<string> Asked { get; } = new();

    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info)
    {
        Asked.Add("message: " + message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK")
    {
        Asked.Add("confirm: " + message);
        return Task.FromResult(Confirms.Count > 0 && Confirms.Dequeue());
    }

    public Task<string?> PromptAsync(string title, string message, string initialValue = "")
    {
        Asked.Add("prompt: " + message);
        return Task.FromResult(Prompts.Count > 0 ? Prompts.Dequeue() : null);
    }

    public Task<string?> OpenFileAsync(string title, params FileFilter[] filters) => NextFile("open: " + title);

    public Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters) =>
        NextFile("save: " + suggestedName);

    public Task<string?> PickFolderAsync(string title) => NextFile("folder: " + title);

    private Task<string?> NextFile(string what)
    {
        Asked.Add(what);
        return Task.FromResult(Files.Count > 0 ? Files.Dequeue() : null);
    }
}

public sealed class ToolsWindows : IWindowService
{
    public List<PageViewModel> Shown { get; } = new();

    public void Show(PageViewModel page, double width = 1100, double height = 750) => Shown.Add(page);

    public Task<bool> ShowDialogAsync(PageViewModel page, double width = 1000, double height = 700)
    {
        Shown.Add(page);
        return Task.FromResult(false);
    }
}

/// <summary>An in-memory vehicle file system behind <see cref="IFtpClient"/>; records calls and checks they never overlap.</summary>
public sealed class ToolsFakeFtpClient : IFtpClient
{
    private readonly object _lock = new();
    private int _active;

    public HashSet<string> Dirs { get; } = new(StringComparer.Ordinal) { "/" };
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<string> Calls { get; } = new();
    public int MaxConcurrent { get; private set; }
    public bool CorruptCrc { get; set; }
    public bool FailCreate { get; set; }
    public string? ThrowOnList { get; set; }

    /// <summary>When set, GetFile waits here until cancelled.</summary>
    public bool BlockDownloads { get; set; }

    public int ResetCount;

    public event Action<string, int>? Progress;

    public IReadOnlyList<FtpEntry> ListDirectory(string directory, CancellationTokenSource cancel) => Do("list " + directory, () =>
    {
        if (ThrowOnList == directory)
            throw new Exception("Failed to OpenFile - kCmdListDirectory kErrFailErrno EIO");
        var dir = Norm(directory);
        if (!Dirs.Contains(dir))
            throw new FileNotFoundException("File Not Found", directory);
        Progress?.Invoke(directory + " Listing", 0);
        var prefix = dir == "/" ? "/" : dir + "/";
        var list = new List<FtpEntry> { new(directory, ".", true), new(directory, "..", true), new(directory, "", true) };
        list.AddRange(Dirs.Where(d => d != dir && d.StartsWith(prefix) && !d[prefix.Length..].Contains('/'))
            .Select(d => new FtpEntry(directory, d[prefix.Length..], true)));
        list.AddRange(Files.Where(f => f.Key.StartsWith(prefix) && !f.Key[prefix.Length..].Contains('/'))
            .Select(f => new FtpEntry(directory, f.Key[prefix.Length..], false, (ulong)f.Value.Length,
                new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc))));
        return (IReadOnlyList<FtpEntry>)list;
    });

    public MemoryStream? GetFile(string path, CancellationTokenSource cancel) => Do("get " + path, () =>
    {
        if (BlockDownloads)
        {
            while (!cancel.IsCancellationRequested)
                Thread.Sleep(5);
            return null;
        }

        Progress?.Invoke(path, 50);
        return Files.TryGetValue(path, out var d) ? new MemoryStream(d) : null;
    });

    public void UploadFile(string remotePath, string localPath, CancellationTokenSource cancel) =>
        Do("put " + remotePath, () => Files[remotePath] = File.ReadAllBytes(localPath));

    public bool CalcFileCrc32(string path, ref uint crc, CancellationTokenSource cancel)
    {
        var data = Do("crc " + path, () => Files.TryGetValue(path, out var d) ? d : null);
        if (data == null)
            return false;
        crc = LocalCrc32(data) ^ (CorruptCrc ? 1u : 0u);
        return true;
    }

    public bool CreateDirectory(string path, CancellationTokenSource cancel) => Do("mkdir " + path, () => !FailCreate && Dirs.Add(path));

    public bool RemoveFile(string path, CancellationTokenSource cancel) => Do("rm " + path, () => Files.Remove(path));

    public bool RemoveDirectoryRecursive(string path, CancellationTokenSource cancel) => Do("rmdir -r " + path, () =>
    {
        foreach (var f in Files.Keys.Where(k => k.StartsWith(path + "/")).ToList())
            Files.Remove(f);
        foreach (var d in Dirs.Where(k => k.StartsWith(path + "/")).ToList())
            Dirs.Remove(d);
        return Dirs.Remove(path);
    });

    public bool Rename(string from, string to, CancellationTokenSource cancel) => Do($"mv {from} {to}", () =>
    {
        if (Files.Remove(from, out var d))
        {
            Files[to] = d;
            return true;
        }

        return Dirs.Remove(from) && Dirs.Add(to);
    });

    public bool ResetSessions()
    {
        Interlocked.Increment(ref ResetCount);
        Calls.Enqueue("reset");
        return true;
    }

    public uint LocalCrc32(byte[] data) => MissionPlanner.ArduPilot.Mavlink.MAVFtp.crc_crc32(0, data);

    private static string Norm(string dir) => dir.Length > 1 ? dir.TrimEnd('/') : dir;

    private T Do<T>(string call, Func<T> work)
    {
        Calls.Enqueue(call);
        lock (_lock)
        {
            _active++;
            MaxConcurrent = Math.Max(MaxConcurrent, _active);
        }

        try
        {
            // long enough for overlapping calls to show
            Thread.Sleep(2);
            lock (Files)
                return work();
        }
        finally
        {
            lock (_lock)
                _active--;
        }
    }
}
