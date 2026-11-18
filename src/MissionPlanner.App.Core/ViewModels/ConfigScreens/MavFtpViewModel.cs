using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// MAVFtp: browse, download, upload, rename and delete files on the vehicle (Mission Planner's
/// MavFTPUI). Every transfer runs on a worker thread, one at a time; failures show inline.
/// </summary>
public sealed partial class MavFtpViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MavFtpViewModel));

    private readonly Func<IFtpClient?> _clientFactory;
    private readonly bool _ownsClientLifetime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FtpListing _listing = new();
    private IFtpClient? _client;
    private (uint SysId, byte CompId, DateTime ConnectedAt)? _clientKey;
    private CancellationTokenSource? _cancel;
    private int _pending;
    private bool _shown;
    private bool _stale = true;
    private long _nextProgressTick;
    private IReadOnlyList<FtpEntry> _selection = [];
    private readonly HashSet<FtpFolderNode> _inFlight = new();

    public MavFtpViewModel(AppServices services)
        : this(services, null)
    {
    }

    /// <summary>For tests: <paramref name="clientFactory"/> replaces the vehicle connection.</summary>
    internal MavFtpViewModel(AppServices services, Func<IFtpClient?>? clientFactory)
        : base("MAVFtp")
    {
        Services = services;
        _ownsClientLifetime = clientFactory == null;
        _clientFactory = clientFactory ?? CreateVehicleClient;
        services.Session.StateChanged += (_, _) => services.Dispatcher.Post(OnLinkChanged);
    }

    public AppServices Services { get; }

    public ObservableCollection<FtpFolderNode> Roots { get; } = new();

    /// <summary>Files and folders of <see cref="CurrentPath"/>, filtered and sorted.</summary>
    public ObservableCollection<FtpEntry> Entries { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand), nameof(UploadCommand), nameof(NewFolderCommand), nameof(RefreshCommand))]
    private FtpFolderNode? _selectedFolder;

    [ObservableProperty]
    private string _currentPath = "";

    [ObservableProperty]
    private string _filter = "";

    /// <summary>What a running operation is doing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string _progressText = "";

    /// <summary>How the last operation ended.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string _resultText = "";

    /// <summary>Status bar text: progress while busy, else the last result.</summary>
    public string Status => IsBusy ? ProgressText : ResultText;

    [ObservableProperty]
    private string _summary = "";

    /// <summary>0-100 for the current transfer.</summary>
    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(Status))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(UploadCommand), nameof(NewFolderCommand),
        nameof(RenameCommand), nameof(DeleteCommand), nameof(CrcCommand), nameof(CancelCommand), nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool IsIdle => !IsBusy;

    public FtpSortColumn SortColumn => _listing.SortColumn;

    public bool SortDescending => _listing.Descending;

    public IReadOnlyList<FtpEntry> SelectedEntries => _selection;

    /// <summary>Called by the view when the file list selection changes.</summary>
    public void SetSelection(IEnumerable<FtpEntry> entries)
    {
        _selection = entries.ToList();
        OnPropertyChanged(nameof(SelectedEntries));
        DownloadCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        CrcCommand.NotifyCanExecuteChanged();
    }

    public override void OnShown()
    {
        _shown = true;
        if (_stale || Roots.Count == 0 || !ClientIsCurrent())
            Reload();
    }

    public override void OnHidden() => _shown = false;

    /// <summary>Called by the view when a column header is clicked.</summary>
    public void SortBy(FtpSortColumn column)
    {
        _listing.ToggleSort(column);
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        ShowListing();
    }

    partial void OnFilterChanged(string value)
    {
        _listing.Filter = value;
        ShowListing();
    }

    partial void OnSelectedFolderChanged(FtpFolderNode? value)
    {
        if (value == null)
            return;
        if (value.IsPlaceholder)
        {
            // the stand-in is not a folder; keep showing its parent
            SelectedFolder = value.Parent;
            return;
        }

        CurrentPath = value.Path;
        _listing.Clear();
        ShowListing();
        _ = ListAsync(value);
    }

    // ---- commands ----

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        Error = null;
        if (Roots.Count == 0 || !ClientIsCurrent())
        {
            Reload();
            return;
        }

        if (SelectedFolder != null)
            await ListAsync(SelectedFolder);
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up()
    {
        if (SelectedFolder?.Parent is { } parent)
            SelectedFolder = parent;
    }

    private bool CanGoUp() => SelectedFolder?.Parent != null;

    /// <summary>Double-click: a folder opens, a file downloads.</summary>
    public async Task OpenEntryAsync(FtpEntry entry)
    {
        if (!entry.IsDirectory)
        {
            SetSelection([entry]);
            await DownloadAsync();
            return;
        }

        var folder = SelectedFolder;
        if (folder == null)
            return;
        folder.IsExpanded = true;
        var child = folder.FindChild(entry.Name);
        if (child == null)
        {
            // listed as a folder in the file view; add it to the tree if the tree missed it
            folder.SetChildren(folder.Children.Where(c => !c.IsPlaceholder).Select(c => new FtpEntry(folder.Path, c.Name, true)).Append(entry));
            child = folder.FindChild(entry.Name);
        }

        if (child != null)
            SelectedFolder = child;
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        Error = null;
        var files = _selection.Where(e => !e.IsDirectory).ToList();
        if (files.Count == 0)
        {
            Error = "Select one or more files to download (folders cannot be downloaded).";
            return;
        }

        // one file: choose its name; several: choose a folder
        var targets = new List<(FtpEntry Entry, string? Local)>();
        if (files.Count == 1)
        {
            var safe = FtpPath.SafeLocalName(files[0].Name);
            if (safe == null)
            {
                Error = "Refusing to save a file named \"" + files[0].Name + "\".";
                return;
            }

            var local = await Services.Dialogs.SaveFileAsync("Download " + files[0].Name, safe);
            if (string.IsNullOrEmpty(local))
                return;
            targets.Add((files[0], local));
        }
        else
        {
            var folder = await Services.Dialogs.PickFolderAsync($"Download {files.Count} files to");
            if (string.IsNullOrEmpty(folder))
                return;
            foreach (var f in files)
                targets.Add((f, FtpPath.SafeLocalName(f.Name) is { } safe ? Path.Combine(folder, safe) : null));
        }

        var (ok, result) = await RunAsync("Download", (client, cancel) =>
        {
            var failed = new List<string>();
            var saved = new List<string>();
            foreach (var (entry, target) in targets)
            {
                if (cancel.IsCancellationRequested)
                    break;
                if (target == null)
                {
                    failed.Add(entry.Name + ": unsafe file name");
                    continue;
                }

                ReportStatus("Downloading " + entry.Name);
                var data = client.GetFile(entry.Path, cancel);
                if (cancel.IsCancellationRequested)
                    break;
                if (data == null)
                {
                    failed.Add(entry.Name + ": could not be opened");
                    continue;
                }

                // a picked folder never overwrites; a picked file name was confirmed by the save dialog
                var local = targets.Count == 1 ? target : FtpPath.UniqueLocalPath(Path.GetDirectoryName(target)!, Path.GetFileName(target));
                File.WriteAllBytes(local, data.ToArray());
                saved.Add(local);
            }

            return (failed, saved);
        });
        if (!ok)
            return;

        if (result.failed.Count > 0)
            Error = "Download failed: " + string.Join("; ", result.failed);
        if (result.saved.Count == 1)
            ResultText = "Saved " + result.saved[0];
        else if (result.saved.Count > 1)
            ResultText = $"Saved {result.saved.Count} files to {Path.GetDirectoryName(result.saved[0])}";
    }

    private bool CanDownload() => !IsBusy && _selection.Any(e => !e.IsDirectory);

    [RelayCommand(CanExecute = nameof(CanChangeFolder))]
    private async Task UploadAsync()
    {
        Error = null;
        if (SelectedFolder == null)
            return;
        var local = await Services.Dialogs.OpenFileAsync("Upload to " + SelectedFolder.Path);
        if (string.IsNullOrEmpty(local))
            return;
        await UploadFilesAsync([local]);
    }

    /// <summary>Uploads local files into the open folder, checking each with the vehicle's CRC (also used for drag and drop).</summary>
    public async Task UploadFilesAsync(IReadOnlyList<string> localFiles)
    {
        Error = null;
        var folder = SelectedFolder;
        if (folder == null || localFiles.Count == 0)
            return;

        var uploads = new List<(string Local, string Remote, string Name)>();
        foreach (var local in localFiles)
        {
            if (!File.Exists(local))
            {
                Error = "Not a file: " + local;
                return;
            }

            var name = Path.GetFileName(local);
            if (FtpPath.ValidateNewName(folder.Path, name) is { } problem)
            {
                Error = name + ": " + problem;
                return;
            }

            uploads.Add((local, FtpPath.Join(folder.Path, name), name));
        }

        var existing = uploads.Where(u => _listing.All.Any(e => e.Name == u.Name)).Select(u => u.Name).ToList();
        if (existing.Count > 0 &&
            !await Services.Dialogs.ConfirmAsync("Upload",
                (existing.Count == 1 ? existing[0] + " already exists" : string.Join(", ", existing) + " already exist") +
                " in " + folder.Path + ". Replace?", "Replace"))
            return;

        var (ok, result) = await RunAsync("Upload", (client, cancel) =>
        {
            var problems = new List<string>();
            var verified = new List<string>();
            foreach (var (local, remote, name) in uploads)
            {
                if (cancel.IsCancellationRequested)
                    break;
                ReportStatus("Uploading " + name);
                client.UploadFile(remote, local, cancel);
                if (cancel.IsCancellationRequested)
                    break;

                // the vehicle's CRC of what it stored must match the local file
                ReportStatus("Checking CRC of " + name, -1);
                uint remoteCrc = 0;
                var answered = client.CalcFileCrc32(remote, ref remoteCrc, cancel);
                if (cancel.IsCancellationRequested)
                    break;
                var localCrc = client.LocalCrc32(File.ReadAllBytes(local));
                if (!answered)
                    problems.Add($"{name}: uploaded, but the vehicle did not report a CRC to verify it");
                else if (remoteCrc != localCrc)
                    problems.Add($"{name}: CRC mismatch (vehicle 0x{remoteCrc:X8}, local 0x{localCrc:X8}); the upload is corrupt");
                else
                    verified.Add(name);
            }

            return (problems, verified);
        });

        if (ok)
        {
            if (result.problems.Count > 0)
                Error = string.Join("; ", result.problems);
            if (result.verified.Count > 0)
                ResultText = result.verified.Count == 1
                    ? $"Uploaded {result.verified[0]} (CRC verified)"
                    : $"Uploaded {result.verified.Count} files (CRC verified)";
        }

        await ListAsync(folder);
    }

    private bool CanChangeFolder() => !IsBusy && SelectedFolder != null;

    [RelayCommand(CanExecute = nameof(CanChangeFolder))]
    private async Task NewFolderAsync()
    {
        Error = null;
        var folder = SelectedFolder;
        if (folder == null)
            return;
        var name = await Services.Dialogs.PromptAsync("New folder", "Name of the new folder in " + folder.Path + ":");
        if (name == null)
            return;
        if (FtpPath.ValidateNewName(folder.Path, name) is { } problem)
        {
            Error = problem;
            return;
        }

        if (_listing.All.Any(e => e.Name == name))
        {
            Error = name + " already exists in " + folder.Path + ".";
            return;
        }

        var path = FtpPath.Join(folder.Path, name);
        var (ok, created) = await RunAsync("New folder", (client, cancel) => client.CreateDirectory(path, cancel));
        if (ok && !created)
            Error = "The vehicle did not create the folder " + path + ".";
        else if (ok)
            ResultText = "Created " + path;
        await ListAsync(folder);
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync()
    {
        Error = null;
        var folder = SelectedFolder;
        if (folder == null || _selection.Count != 1)
            return;
        var entry = _selection[0];
        var name = await Services.Dialogs.PromptAsync("Rename", "New name for " + entry.Name + ":", entry.Name);
        if (name == null || name == entry.Name)
            return;
        if (FtpPath.ValidateNewName(folder.Path, name) is { } problem)
        {
            Error = problem;
            return;
        }

        if (_listing.All.Any(e => e.Name == name))
        {
            Error = name + " already exists in " + folder.Path + ".";
            return;
        }

        var target = FtpPath.Join(folder.Path, name);
        var (ok, renamed) = await RunAsync("Rename", (client, cancel) => client.Rename(entry.Path, target, cancel));
        if (ok && !renamed)
            Error = "The vehicle did not rename " + entry.Name + ".";
        else if (ok)
            ResultText = "Renamed " + entry.Name + " to " + name;
        await ListAsync(folder);
    }

    private bool CanRename() => !IsBusy && _selection.Count == 1;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        Error = null;
        var folder = SelectedFolder;
        var items = _selection.ToList();
        if (folder == null || items.Count == 0)
            return;

        // a folder is deleted with everything in it, so say so
        var folders = items.Count(e => e.IsDirectory);
        string question;
        if (items.Count == 1)
            question = items[0].IsDirectory
                ? "Delete the folder " + items[0].Name + " and everything in it?"
                : "Delete the file " + items[0].Name + "?";
        else
            question = $"Delete {items.Count} items" + (folders > 0 ? $" ({folders} folders, with everything in them)?" : "?");

        if (!await Services.Dialogs.ConfirmAsync("Delete", question, "Delete"))
            return;

        var (ok, failed) = await RunAsync("Delete", (client, cancel) =>
        {
            var failures = new List<string>();
            foreach (var entry in items)
            {
                if (cancel.IsCancellationRequested)
                    break;
                ReportStatus("Deleting " + entry.Name, -1);
                var removed = entry.IsDirectory
                    ? client.RemoveDirectoryRecursive(entry.Path, cancel)
                    : client.RemoveFile(entry.Path, cancel);
                if (!removed && !cancel.IsCancellationRequested)
                    failures.Add(entry.Name);
            }

            return failures;
        });

        if (ok && failed.Count > 0)
            Error = "Failed to delete " + string.Join(", ", failed) + ".";
        else if (ok)
            ResultText = items.Count == 1 ? "Deleted " + items[0].Name : $"Deleted {items.Count} items";
        await ListAsync(folder);
    }

    private bool CanDelete() => !IsBusy && _selection.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCrc))]
    private async Task CrcAsync()
    {
        Error = null;
        if (_selection is not [{ IsDirectory: false } entry])
            return;
        var (ok, result) = await RunAsync("CRC", (client, cancel) =>
        {
            uint crc = 0;
            var answered = client.CalcFileCrc32(entry.Path, ref crc, cancel);
            return (answered, crc);
        });
        if (!ok)
            return;
        if (result.answered)
            ResultText = $"{entry.Name}: CRC32 0x{result.crc:X8}";
        else
            Error = "The vehicle did not report a CRC for " + entry.Name + ".";
    }

    private bool CanCrc() => !IsBusy && _selection is [{ IsDirectory: false }];

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        var cancel = _cancel;
        var client = _client;
        if (cancel == null)
            return;
        ProgressText = "Cancelling…";
        cancel.Cancel();
        // as Mission Planner does: close whatever session the vehicle still has open
        if (client != null)
            _ = Task.Run(() =>
            {
                try
                {
                    client.ResetSessions();
                }
                catch (Exception ex)
                {
                    Log.Warn("reset sessions", ex);
                }
            });
    }

    [RelayCommand]
    private void DismissError() => Error = null;

    // ---- listing ----

    private void Reload()
    {
        _stale = false;
        _cancel?.Cancel();
        Roots.Clear();
        _listing.Clear();
        ShowListing();
        SetSelection([]);
        CurrentPath = "";
        Error = null;

        var client = EnsureClient();
        if (client == null)
        {
            SelectedFolder = null;
            ResultText = "Connect to a vehicle to browse its files.";
            return;
        }

        foreach (var root in FtpPath.Roots)
            Roots.Add(new FtpFolderNode(root, null, OnExpandRequested));
        ResultText = "Ready";
        SelectedFolder = Roots[0];
        Roots[0].IsExpanded = true;
    }

    private void OnExpandRequested(FtpFolderNode node)
    {
        // opening the folder lists it anyway
        if (node != SelectedFolder)
            _ = ListAsync(node);
    }

    /// <summary>Lists a folder: refreshes its tree children and, if it is still the open folder, the file list.</summary>
    private async Task ListAsync(FtpFolderNode node)
    {
        // a folder already queued for listing is not listed twice
        if (!_inFlight.Add(node))
            return;
        bool ok;
        IReadOnlyList<FtpEntry> entries;
        try
        {
            (ok, entries) = await RunAsync("List", (client, cancel) =>
            {
                ReportStatus("Listing " + node.Path, -1);
                return client.ListDirectory(node.Path, cancel);
            });
        }
        finally
        {
            _inFlight.Remove(node);
        }

        if (!ok)
        {
            if (!node.IsLoaded)
                node.SetChildren([]);
            return;
        }

        var listing = new FtpListing();
        listing.Load(node.Path, entries);
        node.SetChildren(listing.Folders);

        // ArduPilot 4.6+ lists @SYS (and @ROMFS) in "/", so drop the duplicate top-level folders
        if (node.Parent == null && node.Path == "/")
        {
            foreach (var dup in Roots.Where(r => r != node && listing.Folders.Any(f => f.Name == FtpPath.DisplayName(r.Path))).ToList())
            {
                if (SelectedFolder != dup)
                    Roots.Remove(dup);
            }
        }

        if (SelectedFolder == node)
        {
            _listing.Load(node.Path, entries);
            ShowListing();
        }
    }

    private void ShowListing()
    {
        var view = _listing.View();
        Entries.Clear();
        foreach (var e in view)
            Entries.Add(e);
        SetSelection([]);

        var folders = _listing.All.Count(e => e.IsDirectory);
        var files = _listing.All.Count - folders;
        var total = _listing.All.Where(e => !e.IsDirectory).Aggregate(0UL, (s, e) => s + e.Size);
        Summary = _listing.Directory.Length == 0
            ? ""
            : $"{folders} folder{(folders == 1 ? "" : "s")}, {files} file{(files == 1 ? "" : "s")} ({FtpFormat.Size(total)})" +
              (view.Count != _listing.All.Count ? $" · {view.Count} shown" : "");
    }

    // ---- running FTP work ----

    /// <summary>
    /// Runs blocking MAVFTP work on a worker thread, after any earlier operation finishes.
    /// Returns ok=false when it failed (the error is shown) or was cancelled.
    /// </summary>
    private async Task<(bool Ok, T Result)> RunAsync<T>(string what, Func<IFtpClient, CancellationTokenSource, T> work)
    {
        var client = EnsureClient();
        if (client == null)
        {
            Error = "Not connected.";
            return (false, default!);
        }

        _pending++;
        IsBusy = true;
        await _gate.WaitAsync();
        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        ProgressText = what + "…";
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        try
        {
            var result = await Task.Run(() => work(client, cancel));
            if (cancel.IsCancellationRequested)
            {
                ResultText = what + " cancelled";
                return (false, default!);
            }

            return (true, result);
        }
        catch (Exception ex) when (cancel.IsCancellationRequested)
        {
            Log.Info(what + " cancelled", ex);
            ResultText = what + " cancelled";
            return (false, default!);
        }
        catch (Exception ex)
        {
            Log.Warn(what, ex);
            Error = what + " failed: " + Describe(ex);
            return (false, default!);
        }
        finally
        {
            if (_cancel == cancel)
                _cancel = null;
            _gate.Release();
            _pending--;
            IsBusy = _pending > 0;
            ProgressPercent = 0;
            IsProgressIndeterminate = false;
        }
    }

    private static string Describe(Exception ex) =>
        ex is FileNotFoundException ? "not found on the vehicle" : ex.Message;

    /// <summary>Status line from a worker thread.</summary>
    private void ReportStatus(string text, int percent = 0) =>
        Services.Dispatcher.Post(() =>
        {
            ProgressText = text;
            IsProgressIndeterminate = percent < 0;
            if (percent >= 0)
                ProgressPercent = percent;
        });

    private void OnProgress(string message, int percent)
    {
        // the library reports every packet; 10 updates a second is plenty
        var now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _nextProgressTick) && percent is > 0 and < 100)
            return;
        Interlocked.Exchange(ref _nextProgressTick, now + 100);
        Services.Dispatcher.Post(() =>
        {
            if (!IsBusy)
                return;
            IsProgressIndeterminate = percent < 0;
            if (percent >= 0)
                ProgressPercent = Math.Clamp(percent, 0, 100);
            if (!string.IsNullOrEmpty(message))
                ProgressText = message;
        });
    }

    // ---- vehicle link ----

    private IFtpClient? EnsureClient()
    {
        if (_client != null && ClientIsCurrent())
            return _client;

        if (_client != null)
            _client.Progress -= OnProgress;
        _client = _clientFactory();
        _clientKey = CurrentKey();
        if (_client != null)
            _client.Progress += OnProgress;
        return _client;
    }

    private bool ClientIsCurrent() => !_ownsClientLifetime || (_client != null && _clientKey == CurrentKey());

    private (uint, byte, DateTime)? CurrentKey()
    {
        var s = Services.Session;
        if (!s.IsConnected)
            return null;
        return (s.Vehicle.sysid, s.Vehicle.compid, s.ConnectedAt);
    }

    private IFtpClient? CreateVehicleClient()
    {
        var s = Services.Session;
        return s.IsConnected ? new MavFtpClient(s.Mav, s.Vehicle.sysid, s.Vehicle.compid) : null;
    }

    private void OnLinkChanged()
    {
        if (!_ownsClientLifetime || ClientIsCurrent())
            return;

        // disconnected, reconnected or another vehicle selected: start over
        _cancel?.Cancel();
        if (_shown)
            Reload();
        else
            _stale = true;
    }
}
