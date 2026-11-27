using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Localization;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Speech;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>A speaker of the chosen voice, for the list.</summary>
public sealed record SpeakerItem(VoiceSpeaker Speaker, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One voice of the catalogue in the list: what it is, its licence, and its download.</summary>
public sealed partial class VoiceItemViewModel : ObservableObject
{
    private readonly SpeechVoiceViewModel _owner;

    public VoiceItemViewModel(VoiceInfo info, SpeechVoiceViewModel owner)
    {
        Info = info;
        _owner = owner;
        Refresh();
    }

    public VoiceInfo Info { get; }

    public string Name => Info.Name;

    public string Description => L.Tr(Info.Description);

    public bool Recommended => Info.Recommended;

    public string Details => L.Format("{0} · {1} quality · {2} voices · {3} download", L.Tr(Info.Language), L.Tr(Info.Quality),
        Info.Speakers.Count, VoiceStore.FormatSize(Info.TotalDownloadBytes(_owner.Store.SharedLexiconInstalled)));

    public string License => L.Tr("Licence:") + " " + Info.License;

    public string LicenseUrl => Info.LicenseUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanDelete))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanDelete))]
    private bool _isBusy;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressUnknown;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private bool _failed;

    public bool CanDownload => !IsInstalled && !IsBusy;

    public bool CanDelete => IsInstalled && !IsBusy;

    private long _size;

    /// <summary>Reads the store's state (called from the page's tick).</summary>
    public void Refresh()
    {
        var status = _owner.Store.Status(Info);
        IsInstalled = status.State == VoiceInstallState.Installed;
        if (!IsInstalled)
            _size = 0;
        else if (_size == 0)
            _size = Math.Max(1, _owner.Store.InstalledSize(Info));
        IsBusy = status.Busy;
        Failed = status.State == VoiceInstallState.Failed;
        ProgressUnknown = status.Fraction < 0;
        Progress = Math.Clamp(status.Fraction, 0, 1) * 100;
        StateText = status.State switch
        {
            VoiceInstallState.Installed => L.Format("Downloaded ({0})", VoiceStore.FormatSize(_size)),
            VoiceInstallState.Downloading => status.Fraction < 0 ? L.Tr("Downloading...") : L.Format("Downloading {0:0} %", status.Fraction * 100),
            VoiceInstallState.Installing => L.Format("Unpacking {0:0} %", status.Fraction * 100),
            VoiceInstallState.Failed => L.Tr("Download failed:") + " " + status.Error,
            _ => L.Tr("Not downloaded"),
        };
    }

    [RelayCommand]
    private void Select() => _owner.SelectedVoice = this;

    [RelayCommand]
    private void Download() => _owner.Download(this);

    [RelayCommand]
    private void Cancel() => _owner.Store.Cancel(Info);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);
}

/// <summary>
/// The "Engine &amp; voice" part of the speech settings: neural or system voice, the downloadable voices
/// (download, cancel, delete, progress), speaker, speed and volume, the system program's own voice, speed
/// and volume, and a test sentence with the choices as they are (before Save).
/// </summary>
public sealed partial class SpeechVoiceViewModel : ObservableObject
{
    /// <summary>What "Test voice" says.</summary>
    public const string SampleText = "Mode changed to Loiter. Battery 50 percent.";

    private readonly SpeechEngine _engine;
    private readonly IDialogService? _dialogs;
    private readonly string? _program;
    private long _ticks;
    private string _lastStates = "";

    public SpeechVoiceViewModel(SpeechEngine engine, IDialogService? dialogs, VoiceStore? store = null)
    {
        _engine = engine;
        _dialogs = dialogs;
        Store = store ?? engine.Voices ?? new VoiceStore(VoiceStore.DefaultRoot);
        _program = engine.BackendName;
        var s = SpeechVoiceSettings.Load();
        _useNeural = s.Engine == SpeechEngineKind.Neural;
        _speed = s.Speed;
        _volume = s.Volume;
        _systemSpeed = s.SystemSpeed;
        _systemVolume = s.SystemVolume;
        foreach (var v in VoiceCatalog.All)
            Voices.Add(new VoiceItemViewModel(v, this));

        var chosen = s.ResolveVoice(Store.IsInstalled) ?? VoiceCatalog.Recommended;
        _selectedVoice = Voices.First(v => v.Info.Id == chosen.Id);
        _selectedVoice.IsSelected = true;
        Speakers = SpeakersOf(chosen);
        _selectedSpeaker = Speakers.FirstOrDefault(x => x.Speaker.Id == (s.Speaker >= 0 && VoiceCatalog.Find(s.VoiceId)?.Id == chosen.Id ? s.Speaker : chosen.DefaultSpeaker))
                           ?? Speakers.FirstOrDefault();

        SystemVoiceList.Add(DefaultSystemVoice);
        _selectedSystemVoice = DefaultSystemVoice;
        _savedSystemVoice = s.SystemVoice;
        if (s.SystemVoice.Length > 0)
        {
            var saved = new SystemVoice(s.SystemVoice, s.SystemVoice);
            SystemVoiceList.Add(saved);
            _selectedSystemVoice = saved;
        }

        Store.CleanUp();
        UpdateStatus();
    }

    private static readonly SystemVoice DefaultSystemVoice = new("", L.Tr("Default"));
    private readonly string _savedSystemVoice;

    public VoiceStore Store { get; }

    public ObservableCollection<VoiceItemViewModel> Voices { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseSystem))]
    private bool _useNeural;

    public bool UseSystem
    {
        get => !UseNeural;
        set => UseNeural = !value;
    }

    [ObservableProperty] private VoiceItemViewModel? _selectedVoice;

    [ObservableProperty] private IReadOnlyList<SpeakerItem> _speakers = [];

    [ObservableProperty] private SpeakerItem? _selectedSpeaker;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    private double _speed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    private double _volume;

    public string SpeedText => $"{Speed:0.00}×";

    public string VolumeText => $"{Volume:0} %";

    public ObservableCollection<SystemVoice> SystemVoiceList { get; } = new();

    [ObservableProperty] private SystemVoice? _selectedSystemVoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SystemSpeedText))]
    private double _systemSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SystemVolumeText))]
    private double _systemVolume;

    public string SystemSpeedText => $"{SystemSpeed:0.00}×";

    public string SystemVolumeText => $"{SystemVolume:0} %";

    /// <summary>The system program takes a voice, rate and volume (not festival).</summary>
    public bool SystemHasOptions => SpeechBackend.HasOptions(_program);

    public string SystemProgramText => _program is { } p
        ? L.Format("System voice: {0}", p)
        : L.Tr("No system speech program found.");

    public double MinSpeed => SpeechVoiceSettings.MinSpeed;

    public double MaxSpeed => SpeechVoiceSettings.MaxSpeed;

    /// <summary>What speaks with the current choices, and why not the neural voice if so.</summary>
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private bool _statusIsWarning;

    partial void OnUseNeuralChanged(bool value) => UpdateStatus();

    partial void OnSelectedSpeakerChanged(SpeakerItem? value) => UpdateStatus();

    partial void OnSelectedVoiceChanged(VoiceItemViewModel? oldValue, VoiceItemViewModel? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;
        if (newValue == null)
            return;
        newValue.IsSelected = true;
        var keep = SelectedSpeaker?.Speaker.Id;
        Speakers = SpeakersOf(newValue.Info);
        SelectedSpeaker = Speakers.FirstOrDefault(s => s.Speaker.Id == keep && newValue.Info.Kind == oldValue?.Info.Kind)
                          ?? Speakers.FirstOrDefault(s => s.Speaker.Id == newValue.Info.DefaultSpeaker) ?? Speakers.FirstOrDefault();
        UpdateStatus();
    }

    private static IReadOnlyList<SpeakerItem> SpeakersOf(VoiceInfo voice) =>
        voice.Speakers.Select(s => new SpeakerItem(s, $"{s.Name} ({s.Accent}, {L.Tr(s.Gender)})")).ToList();

    /// <summary>The choices on the page, as settings (not saved).</summary>
    public SpeechVoiceSettings Current() => new(
        UseNeural ? SpeechEngineKind.Neural : SpeechEngineKind.System,
        SelectedVoice?.Info.Id ?? "",
        SelectedSpeaker?.Speaker.Id ?? -1,
        Math.Round(Math.Clamp(Speed, SpeechVoiceSettings.MinSpeed, SpeechVoiceSettings.MaxSpeed), 2),
        (int)Math.Round(Math.Clamp(Volume, 0, 100)),
        SelectedSystemVoice?.Id ?? "",
        Math.Round(Math.Clamp(SystemSpeed, SpeechVoiceSettings.MinSpeed, SpeechVoiceSettings.MaxSpeed), 2),
        (int)Math.Round(Math.Clamp(SystemVolume, 0, 100)));

    public void Save() => Current().Save();

    /// <summary>Lists the system program's voices (a second or two; the list starts with the saved one).</summary>
    public async Task LoadSystemVoicesAsync()
    {
        var voices = await SystemVoices.ListAsync();
        foreach (var v in voices)
        {
            if (SystemVoiceList.FirstOrDefault(x => x.Id == v.Id) is { } existing)
            {
                var i = SystemVoiceList.IndexOf(existing);
                SystemVoiceList[i] = v;
                if (ReferenceEquals(SelectedSystemVoice, existing) || SelectedSystemVoice?.Id == v.Id)
                    SelectedSystemVoice = v;
            }
            else
            {
                SystemVoiceList.Add(v);
            }
        }

        if (_savedSystemVoice.Length > 0 && SelectedSystemVoice?.Id != _savedSystemVoice)
            SelectedSystemVoice = SystemVoiceList.FirstOrDefault(v => v.Id == _savedSystemVoice) ?? SelectedSystemVoice;
    }

    [RelayCommand]
    private void TestVoice()
    {
        _engine.Neural?.Reset();
        _engine.SpeakAsyncCancelAll();
        _engine.SpeakNow(SampleText, Current());
    }

    internal void Download(VoiceItemViewModel item)
    {
        _engine.Neural?.Release(item.Info.Id);
        _ = InstallAsync(item);
    }

    private async Task InstallAsync(VoiceItemViewModel item)
    {
        item.Refresh();
        var ok = await Store.InstallAsync(item.Info);
        _engine.Neural?.Reset();
        if (ok && !(SelectedVoice?.IsInstalled ?? false))
            SelectedVoice = item;
        Refresh();
    }

    internal async Task DeleteAsync(VoiceItemViewModel item)
    {
        if (_dialogs != null && !await _dialogs.ConfirmAsync(L.Tr("Delete voice"),
                L.Format("Delete {0} ({1}) from this computer? It can be downloaded again.", item.Name, VoiceStore.FormatSize(Store.InstalledSize(item.Info))),
                L.Tr("Delete")))
            return;
        _engine.SpeakAsyncCancelAll();
        _engine.Neural?.Release(item.Info.Id);
        try
        {
            Store.Delete(item.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_dialogs != null)
                await _dialogs.ShowMessageAsync(L.Tr("Delete voice"), ex.Message, MessageKind.Error);
        }

        Refresh();
    }

    /// <summary>Reads the downloads' progress (from the page's tick, a few times a second).</summary>
    public void Tick()
    {
        if (++_ticks % 5 == 0)
            Refresh();
    }

    public void Refresh()
    {
        var states = new System.Text.StringBuilder();
        foreach (var v in Voices)
        {
            v.Refresh();
            states.Append(v.IsInstalled).Append(v.IsBusy).Append(v.Failed).Append(';');
        }

        if (states.ToString() != _lastStates)
        {
            _lastStates = states.ToString();
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        var settings = Current();
        var system = _program is { } p ? L.Format("the system voice ({0})", p) : L.Tr("no voice: no system speech program was found");
        if (!UseNeural)
        {
            StatusText = L.Format("Speech uses {0}.", system);
            StatusIsWarning = _program == null;
            return;
        }

        var problem = _engine.Neural is { } neural ? neural.Check(settings) : L.Tr("The neural speech engine is not available in this app.");
        if (problem == null)
        {
            var voice = settings.ResolveVoice(Store.IsInstalled)!;
            var speaker = voice.SpeakerOrDefault(voice.Id == settings.VoiceId ? settings.Speaker : voice.DefaultSpeaker);
            StatusText = voice.Id == settings.VoiceId
                ? L.Format("Speech uses the neural voice {0}, {1}.", voice.Name, speaker.Name)
                : L.Format("{0} is not downloaded: speech uses {1}, {2}.", SelectedVoice?.Name, voice.Name, speaker.Name);
            StatusIsWarning = false;
        }
        else
        {
            StatusText = problem + " " + L.Format("Until then speech uses {0}.", system);
            StatusIsWarning = true;
        }
    }
}
