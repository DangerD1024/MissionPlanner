using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Logs;

public enum LogEntryState
{
    None,
    Queued,
    Downloading,
    Done,
    Failed,
    Skipped,
}

/// <summary>A row of the vehicle's log list.</summary>
public sealed partial class LogEntryRow : ObservableObject
{
    public LogEntryRow(LogEntryInfo info)
    {
        Info = info;
        Time = LogFileNaming.EntryTime(info.TimeUtc);
    }

    public LogEntryInfo Info { get; }

    public ushort Id => Info.Id;

    public uint Size => Info.Size;

    /// <summary>The vehicle clock when the log started (local time); null when the vehicle had no time.</summary>
    public DateTime? Time { get; }

    public string DateText => Time?.ToString("yyyy-MM-dd  HH:mm:ss", CultureInfo.InvariantCulture) ?? "no date";

    public string SizeText => FormatBytes(Size);

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsTransferring), nameof(IsFailed), nameof(IsDone))]
    private LogEntryState _state;

    /// <summary>Percent of this log received.</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloaded), nameof(LocalName))]
    private string? _localPath;

    public bool IsDownloaded => LocalPath != null;

    public string LocalName => LocalPath == null ? "" : System.IO.Path.GetFileName(LocalPath);

    public bool IsTransferring => State == LogEntryState.Downloading;

    public bool IsFailed => State == LogEntryState.Failed;

    public bool IsDone => State == LogEntryState.Done;

    public string StateText => State switch
    {
        LogEntryState.Queued => "Queued",
        LogEntryState.Downloading => "Downloading",
        LogEntryState.Done => "Downloaded",
        LogEntryState.Failed => "Failed",
        LogEntryState.Skipped => "Not downloaded",
        _ => "",
    };

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var u = 0;
        while (bytes >= 1024 && u < units.Length - 1)
        {
            bytes /= 1024;
            u++;
        }

        return u == 0
            ? $"{bytes:0} {units[u]}"
            : bytes.ToString(bytes >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[u];
    }
}

/// <summary>
/// DataFlash Logs: list the vehicle's onboard logs, download some or all into
/// &lt;log dir&gt;/&lt;vehicle type&gt;/&lt;sysid&gt;/ (renamed by GPS start time, as Mission Planner
/// does), erase them, and open a downloaded log in the log browser.
/// </summary>
public sealed partial class LogDownloadViewModel : PageViewModel, IDisposable
{
    private long _fileReceived;
    private long _fileSize;
    private long _doneBytes;
    private long _totalBytes;
    private long _startTicks;
    private LogEntryRow? _current;
    private bool _listedOnce;
    private bool _disposed;
    private CancellationTokenSource _cancel = new();

    public LogDownloadViewModel(AppServices services)
        : base("DataFlash Logs")
    {
        Services = services;
        Entries.CollectionChanged += (_, _) => OnSelectionChanged();
        _folder = Settings.Instance.LogDir;
    }

    public AppServices Services { get; }

    public ObservableCollection<LogEntryRow> Entries { get; } = new();

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanList), nameof(CanDownload), nameof(CanErase), nameof(ShowNotConnected))]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isArmed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanList), nameof(CanDownload), nameof(CanErase), nameof(ShowEmpty))]
    private bool _isListing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanList), nameof(CanDownload), nameof(CanErase), nameof(CanCancel))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanList), nameof(CanDownload), nameof(CanErase))]
    private bool _isErasing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _cancelRequested;

    /// <summary>Percent of the current log.</summary>
    [ObservableProperty]
    private double _fileProgress;

    /// <summary>Percent of everything queued.</summary>
    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private string _fileProgressText = "";

    [ObservableProperty]
    private string _overallProgressText = "";

    /// <summary>Where logs of the connected vehicle are saved.</summary>
    [ObservableProperty]
    private string _folder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownloaded))]
    private string? _lastDownloaded;

    [ObservableProperty]
    private string _selectionText = "";

    public bool IsBusy => IsListing || IsDownloading || IsErasing;

    public bool CanList => IsConnected && !IsBusy;

    public bool CanDownload => IsConnected && !IsBusy && Entries.Count > 0;

    public bool CanErase => IsConnected && !IsBusy;

    public bool CanCancel => IsDownloading && !CancelRequested;

    public bool HasDownloaded => LastDownloaded != null;

    public bool ShowNotConnected => !IsConnected && Entries.Count == 0;

    public bool ShowEmpty => IsConnected && !IsListing && _listedOnce && Entries.Count == 0;

    /// <summary>Ticks or unticks every row.</summary>
    public bool? AllSelected
    {
        get
        {
            if (Entries.Count == 0)
                return false;
            var n = Entries.Count(e => e.IsSelected);
            return n == 0 ? false : n == Entries.Count ? true : null;
        }
        set
        {
            var select = value ?? false;
            foreach (var e in Entries)
                e.IsSelected = select;
        }
    }

    public override void OnShown()
    {
        Refresh();
        if (IsConnected && !_listedOnce && !IsBusy)
            _ = ListLogsAsync();
        else if (!IsConnected && Entries.Count == 0)
            SetStatus("Connect to a vehicle to list its logs.");
    }

    public override void Tick()
    {
        Refresh();
        if (!IsDownloading)
            return;

        var received = Math.Min(Interlocked.Read(ref _fileReceived), _fileSize);
        var size = Math.Max(1, _fileSize);
        FileProgress = 100.0 * received / size;
        if (_current != null)
            _current.Progress = FileProgress;
        FileProgressText = $"{LogEntryRow.FormatBytes(received)} of {LogEntryRow.FormatBytes(_fileSize)}";

        var total = Math.Max(1, _totalBytes);
        var done = Math.Min(_doneBytes + received, total);
        OverallProgress = 100.0 * done / total;
        var elapsed = (Environment.TickCount64 - _startTicks) / 1000.0;
        var rate = elapsed > 1 ? done / elapsed : 0;
        var text = $"{LogEntryRow.FormatBytes(done)} of {LogEntryRow.FormatBytes(total)}";
        if (rate > 0)
        {
            var left = TimeSpan.FromSeconds((total - done) / rate);
            text += $"  ·  {LogEntryRow.FormatBytes(rate)}/s  ·  " +
                    (left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : left.ToString(@"m\:ss", CultureInfo.InvariantCulture)) +
                    " left";
        }

        OverallProgressText = text;
    }

    private void Refresh()
    {
        var session = Services.Session;
        IsConnected = session.IsConnected;
        IsArmed = IsConnected && session.Cs.armed;
        if (IsConnected && !IsDownloading)
            Folder = VehicleFolder();
    }

    private string VehicleFolder()
    {
        var v = Services.Session.Vehicle;
        return LogFileNaming.VehicleFolder(Settings.Instance.LogDir, v.aptype.ToString(), v.sysid);
    }

    [RelayCommand]
    private Task ListLogs() => ListLogsAsync();

    private async Task ListLogsAsync()
    {
        Refresh();
        if (!IsConnected)
        {
            SetStatus("Not connected.", true);
            return;
        }

        if (IsBusy)
            return;

        IsListing = true;
        SetStatus("Asking the vehicle for its log list…");
        try
        {
            var session = Services.Session;
            var downloader = new LogDownloader(session.Mav, session.Vehicle.sysid, session.Vehicle.compid);
            var entries = await Task.Run(downloader.ListAsync);
            if (_disposed)
                return;

            Entries.Clear();
            foreach (var e in entries)
                AddRow(new LogEntryRow(e));
            _listedOnce = true;

            SetStatus(entries.Count == 0
                ? "The vehicle has no logs."
                : $"{entries.Count} log{(entries.Count == 1 ? "" : "s")}, {LogEntryRow.FormatBytes(entries.Sum(e => (double)e.Size))} in all.");
        }
        catch (TimeoutException)
        {
            SetStatus("The vehicle did not answer the log list request.", true);
        }
        catch (Exception ex)
        {
            SetStatus("Could not list the logs: " + ex.Message, true);
        }
        finally
        {
            IsListing = false;
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    private void AddRow(LogEntryRow row)
    {
        row.PropertyChanged += OnRowChanged;
        Entries.Add(row);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogEntryRow.IsSelected))
            OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        var selected = Entries.Where(r => r.IsSelected).ToList();
        SelectionText = selected.Count == 0
            ? $"{Entries.Count} log{(Entries.Count == 1 ? "" : "s")}"
            : $"{selected.Count} of {Entries.Count} selected  ·  {LogEntryRow.FormatBytes(selected.Sum(r => (double)r.Size))}";
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowNotConnected));
    }

    [RelayCommand]
    private Task DownloadSelected()
    {
        var rows = Entries.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
        {
            SetStatus("Tick the logs to download, or use Download all.");
            return Task.CompletedTask;
        }

        return DownloadAsync(rows);
    }

    [RelayCommand]
    private Task DownloadAll() => DownloadAsync(Entries.ToList());

    /// <summary>Downloads the rows one after another. Cancel stops after the log in progress.</summary>
    internal async Task DownloadAsync(IReadOnlyList<LogEntryRow> rows)
    {
        Refresh();
        if (!IsConnected)
        {
            SetStatus("Not connected.", true);
            return;
        }

        if (IsBusy || rows.Count == 0)
            return;

        var folder = VehicleFolder();
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            SetStatus($"Cannot create the log folder {folder}: {ex.Message}", true);
            return;
        }

        Folder = folder;
        if (IsArmed)
            SetStatus("The vehicle is armed: downloading logs in flight is slow and can disturb the vehicle.", true);

        CancelRequested = false;
        _cancel.Dispose();
        _cancel = new CancellationTokenSource();
        IsDownloading = true;
        foreach (var r in rows)
        {
            r.State = LogEntryState.Queued;
            r.Progress = 0;
        }

        _totalBytes = rows.Sum(r => (long)r.Size);
        _doneBytes = 0;
        _startTicks = Environment.TickCount64;
        int done = 0, failed = 0;
        string? error = null;

        var session = Services.Session;
        var downloader = new LogDownloader(session.Mav, session.Vehicle.sysid, session.Vehicle.compid);
        try
        {
            foreach (var row in rows)
            {
                if (CancelRequested || _disposed || error != null || !session.IsConnected)
                {
                    row.State = LogEntryState.Skipped;
                    continue;
                }

                _current = row;
                _fileSize = row.Size;
                Interlocked.Exchange(ref _fileReceived, 0);
                row.State = LogEntryState.Downloading;
                SetStatus($"Downloading log {row.Id} ({row.SizeText})…");
                Tick();

                try
                {
                    var token = _cancel.Token;
                    var path = await Task.Run(() => downloader.DownloadAsync(row.Info, folder,
                        bytes => Interlocked.Exchange(ref _fileReceived, bytes), token));
                    row.LocalPath = path;
                    row.Progress = 100;
                    row.State = LogEntryState.Done;
                    LastDownloaded = path;
                    done++;
                }
                catch (OperationCanceledException)
                {
                    row.State = LogEntryState.Skipped;
                    row.Progress = 0;
                }
                catch (Exception ex)
                {
                    row.State = LogEntryState.Failed;
                    failed++;
                    // a timeout means the vehicle or the link stopped answering: the rest would fail too
                    error = $"Log {row.Id} failed: {(ex is TimeoutException ? "the vehicle stopped sending it" : ex.Message)}";
                }

                _doneBytes += row.Size;
                Interlocked.Exchange(ref _fileReceived, 0);
            }
        }
        finally
        {
            _current = null;
            Tick();
            IsDownloading = false;
        }

        var skipped = rows.Count(r => r.State == LogEntryState.Skipped);
        var summary = $"{done} log{(done == 1 ? "" : "s")} saved to {folder}";
        if (skipped > 0)
            summary += $", {skipped} not downloaded";
        if (CancelRequested && error == null)
            summary = "Cancelled. " + summary;
        SetStatus(error != null ? error + ". " + summary + "." : summary + ".", error != null || failed > 0);
        OverallProgressText = "";
        FileProgressText = "";
    }

    /// <summary>Stops the log in progress (nothing of it is kept) and leaves the rest of the queue.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (!IsDownloading)
            return;
        CancelRequested = true;
        _cancel.Cancel();
        SetStatus("Cancelling…");
    }

    [RelayCommand]
    private async Task ClearLogs()
    {
        Refresh();
        if (!CanErase)
            return;

        var ok = await Services.Dialogs.ConfirmAsync("Clear logs",
            "Erase all DataFlash logs on the vehicle? This cannot be undone.", "Erase logs");
        if (!ok || !Services.Session.IsConnected)
            return;

        IsErasing = true;
        try
        {
            var session = Services.Session;
            var downloader = new LogDownloader(session.Mav, session.Vehicle.sysid, session.Vehicle.compid);
            await Task.Run(downloader.EraseAll);
            Entries.Clear();
            _listedOnce = false;
            SetStatus("Erase sent. The vehicle does not confirm it: list the logs again in a few seconds to check.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not erase the logs: " + ex.Message, true);
        }
        finally
        {
            IsErasing = false;
        }
    }

    [RelayCommand]
    private async Task OpenFolder()
    {
        var folder = Folder;
        try
        {
            Directory.CreateDirectory(folder);
            await Task.Run(() =>
            {
                using var _ = Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Logs are in {folder} (no file manager could be opened: {ex.Message})");
        }
    }

    /// <summary>Opens the log browser with a downloaded log: the row's, else the last one, else empty.</summary>
    [RelayCommand]
    private void Review(LogEntryRow? row)
    {
        var path = row?.LocalPath ?? LastDownloaded;
        var browser = new LogBrowserViewModel(Services);
        Services.Windows.Show(browser, 1320, 840);
        if (path != null)
            _ = browser.OpenFileAsync(path);
    }

    private void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    /// <summary>The window closed: a download in progress stops, the link stays up.</summary>
    public void Dispose()
    {
        _disposed = true;
        CancelRequested = true;
        _cancel.Cancel();
    }
}
