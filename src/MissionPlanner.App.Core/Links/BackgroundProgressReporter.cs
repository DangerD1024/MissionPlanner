using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Links;

/// <summary>Progress from a long MAVLinkInterface operation: percent (-1 = indeterminate) and status text.</summary>
public readonly record struct OperationProgress(int Percent, string Status);

/// <summary>
/// <see cref="IProgressReporterDialogue"/> without a dialog. MAVLinkInterface calls
/// <see cref="RunBackgroundOperationAsync"/> and expects it to return when the work is done
/// (the WinForms dialog blocks modally). This runs the work on the calling thread, which the
/// session keeps off the UI thread, forwards progress to an <see cref="IProgress{T}"/>, and keeps
/// the error that the library's own NoUIReporter would swallow.
/// </summary>
public sealed class BackgroundProgressReporter : IProgressReporterDialogue
{
    private readonly IProgress<OperationProgress>? _progress;

    public BackgroundProgressReporter(IProgress<OperationProgress>? progress = null, CancellationToken cancel = default)
    {
        _progress = progress;
        if (cancel.CanBeCanceled)
            cancel.Register(() => doWorkArgs.CancelRequested = true);
    }

    public event DoWorkEventHandler? DoWork;

    public ProgressWorkerEventArgs doWorkArgs { get; set; } = new();

    /// <summary>Exception thrown by the work, if any.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Error text the work reported through <see cref="ProgressWorkerEventArgs.ErrorMessage"/> or by throwing.</summary>
    public string? ErrorMessage => doWorkArgs.ErrorMessage ?? Error?.Message;

    public bool Cancelled => doWorkArgs.CancelAcknowledged;

    public void RunBackgroundOperationAsync()
    {
        try
        {
            DoWork?.Invoke(this);
        }
        catch (Exception ex)
        {
            Error = ex;
        }
    }

    public void UpdateProgressAndStatus(int progress, string status) =>
        _progress?.Report(new OperationProgress(progress, status ?? ""));

    // only the WinForms dialog has a UI thread to marshal to; run inline
    public void BeginInvoke(Delegate method) => method?.DynamicInvoke();

    public void Dispose()
    {
    }

    /// <summary>
    /// Supplies reporters to <c>MAVLinkInterface.CreateIProgressReporterDialogue</c>, a static
    /// event. The reporter for an operation is set on the thread that calls into the library, so
    /// concurrent sessions on different threads each get their own.
    /// </summary>
    public static class Scope
    {
        [ThreadStatic] private static BackgroundProgressReporter? _current;
        private static int _installed;

        /// <summary>Hooks the library's factory event once per process.</summary>
        public static void Install()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 1)
                return;
            MAVLinkInterface.CreateIProgressReporterDialogue +=_ => _current ?? new BackgroundProgressReporter();
        }

        /// <summary>Makes <paramref name="reporter"/> the one handed out on this thread until disposed.</summary>
        public static IDisposable Use(BackgroundProgressReporter reporter)
        {
            Install();
            var previous = _current;
            _current = reporter;
            return new Restore(previous);
        }

        private sealed class Restore(BackgroundProgressReporter? previous) : IDisposable
        {
            public void Dispose() => _current = previous;
        }
    }
}
