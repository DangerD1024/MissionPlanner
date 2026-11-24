using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>Mission Planner's FFT window (fftui): spectra of the IMU data of a DataFlash log, or of a WAV file.</summary>
public sealed partial class FftViewModel : ToolPageViewModel
{
    private const string KeyBins = "next_fft_bins";
    private const string KeyStart = "next_fft_startfreq";
    private const string KeyMagnitude = "next_fft_magnitude";
    private const string KeyWavRate = "next_fft_wavrate";

    private static readonly FileFilter DataFlash = new("DataFlash logs", "bin", "BIN", "log", "LOG");
    private static readonly FileFilter Wav = new("WAV files", "wav", "WAV");

    private CancellationTokenSource? _cancel;

    public FftViewModel(AppServices services)
        : base("FFT",
            "Frequency spectrum of the gyros and accelerometers in a DataFlash log, to find vibration and the frequencies for the notch filter.",
            services)
    {
        var s = Settings.Instance;
        _bins = Math.Clamp(s.GetInt32(KeyBins, 10), FftOptions.MinBins, FftOptions.MaxBins);
        _startFrequency = (decimal)s.GetDouble(KeyStart, 5);
        _magnitude = s.GetBoolean(KeyMagnitude, false);
        _wavSampleRate = Math.Max(1, s.GetInt32(KeyWavRate, 8000));
    }

    public override bool NeedsVehicle => false;

    /// <summary>FFT length as a power of two (10 = 1024 samples).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BinsText))]
    private int _bins;

    /// <summary>Bins under this frequency (Hz) are left out of the average.</summary>
    [ObservableProperty]
    private decimal _startFrequency;

    /// <summary>Linear magnitude instead of dB.</summary>
    [ObservableProperty]
    private bool _magnitude;

    /// <summary>Rate of a raw (headerless) 16-bit file; a WAV header's own rate wins.</summary>
    [ObservableProperty]
    private int _wavSampleRate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlots))]
    private IReadOnlyList<FftPlot> _plots = [];

    public bool HasPlots => Plots.Count > 0;

    public string BinsText => $"{1 << Bins} samples";

    public string YLabel => Magnitude ? "Amplitude" : "Amplitude (dB)";

    partial void OnBinsChanged(int value) => Settings.Instance[KeyBins] = value.ToString(CultureInfo.InvariantCulture);

    partial void OnStartFrequencyChanged(decimal value) => Settings.Instance[KeyStart] = value.ToString(CultureInfo.InvariantCulture);

    partial void OnMagnitudeChanged(bool value)
    {
        Settings.Instance[KeyMagnitude] = value.ToString();
        OnPropertyChanged(nameof(YLabel));
    }

    partial void OnWavSampleRateChanged(int value) => Settings.Instance[KeyWavRate] = value.ToString(CultureInfo.InvariantCulture);

    private FftOptions Options => new(Math.Clamp(Bins, FftOptions.MinBins, FftOptions.MaxBins), (double)StartFrequency, !Magnitude);

    [RelayCommand]
    private Task BatchSamplerAsync() => RunLogAsync(FftSource.BatchSampler);

    [RelayCommand]
    private Task AccGyrAsync() => RunLogAsync(FftSource.AccGyr);

    [RelayCommand]
    private Task ImuMessagesAsync() => RunLogAsync(FftSource.ImuMessages);

    [RelayCommand]
    private Task FirstImuAsync() => RunLogAsync(FftSource.FirstImu);

    [RelayCommand]
    private void Cancel() => _cancel?.Cancel();

    private async Task RunLogAsync(FftSource source)
    {
        if (IsBusy)
            return;
        var file = await Services.Dialogs.OpenFileAsync("DataFlash log", DataFlash);
        if (file == null)
            return;
        var options = Options;
        var name = Path.GetFileName(file);
        await RunFftAsync("Reading " + name, cancel =>
        {
            var sensors = FftAnalysis.Read(file, source, cancel);
            var plots = new List<FftPlot>();
            foreach (var s in sensors)
            {
                cancel.ThrowIfCancellationRequested();
                if (FftAnalysis.Plot(s, options, name) is { } p)
                    plots.Add(p);
            }

            if (plots.Count == 0)
            {
                var what = source switch
                {
                    FftSource.BatchSampler => "ISBH/ISBD batch sampler data (set INS_LOG_BAT_MASK)",
                    FftSource.ImuMessages => "IMU messages",
                    _ => "ACC/GYR messages",
                };
                throw new InvalidOperationException($"{name} has no {what} with more than {options.N} samples.");
            }

            return plots;
        });
    }

    [RelayCommand]
    private async Task WavAsync()
    {
        if (IsBusy)
            return;
        var file = await Services.Dialogs.OpenFileAsync("16-bit mono WAV", Wav);
        if (file == null)
            return;
        var options = Options;
        var rate = WavSampleRate;
        var name = Path.GetFileName(file);
        await RunFftAsync("Reading " + name, _ =>
        {
            using var stream = File.OpenRead(file);
            var (samples, sampleRate) = FftAnalysis.ReadWav(stream, rate);
            var plot = FftAnalysis.WavPlot(samples, sampleRate, options, name)
                       ?? throw new InvalidOperationException($"{name} has fewer than {options.N} samples.");
            return new List<FftPlot> { plot };
        });
    }

    private async Task RunFftAsync(string what, Func<CancellationToken, List<FftPlot>> work)
    {
        IsBusy = true;
        Status = what + "…";
        StatusIsError = false;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        try
        {
            var plots = await Task.Run(() => work(token), token);
            FftAnalysis.ShareScales(plots);
            Plots = plots;
            var peaks = string.Join(", ", plots.Select(p => $"{p.Sensor} {p.PeakFrequency(0, (double)StartFrequency):0.#} Hz"));
            Report($"{plots.Count} graphs. Highest peaks: {peaks}");
        }
        catch (OperationCanceledException)
        {
            Report("Cancelled.");
        }
        catch (Exception ex)
        {
            Report(ex.Message, true);
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            IsBusy = false;
        }
    }

    /// <summary>Text for a point under the mouse: Mission Planner showed hz and rpm.</summary>
    public static string PointText(double hz, double value, bool magnitude) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.0} Hz / {1:0} rpm   {2:0.###}{3}", hz, hz * 60.0, value, magnitude ? "" : " dB");
}
