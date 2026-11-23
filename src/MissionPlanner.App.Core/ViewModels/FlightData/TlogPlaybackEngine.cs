using System.Diagnostics;
using log4net;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

public enum TlogPlaybackState
{
    /// <summary>No log is open; the interface is free for a live link.</summary>
    Unloaded,
    Paused,
    Playing,
    /// <summary>Played to the end of the file; Play starts again from the beginning.</summary>
    Ended,
}

/// <summary>Time source for playback pacing; replaced by a virtual clock in tests.</summary>
public interface ITlogClock
{
    /// <summary>Monotonic time in milliseconds.</summary>
    double NowMs { get; }

    Task DelayAsync(double milliseconds, CancellationToken cancel);
}

public sealed class SystemTlogClock : ITlogClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public static SystemTlogClock Instance { get; } = new();

    public double NowMs => _watch.Elapsed.TotalMilliseconds;

    public Task DelayAsync(double milliseconds, CancellationToken cancel) =>
        Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancel);
}

/// <summary>
/// Mission Planner's tlog pacing (FlightData mainloop): sleep for the log time between two
/// packets divided by the speed, corrected by how much longer or shorter the previous sleep and
/// read really took, so playback keeps the log's pace on average whatever the timer resolution.
/// </summary>
internal struct TlogPacer
{
    /// <summary>Gaps longer than this (or negative) are not waited for.</summary>
    public const double MaxGapMs = 9999;

    /// <summary>Longest single sleep, and the most the accumulated error may grow.</summary>
    public const double MaxSleepMs = 1000;

    private double _timeError;
    private double _plannedMs;
    private double _lastNowMs;

    public TlogPacer(double nowMs)
    {
        _timeError = 0;
        _plannedMs = 0;
        _lastNowMs = nowMs;
    }

    /// <summary>Milliseconds to sleep before the next packet; zero or less means don't sleep.</summary>
    public double Next(double logGapMs, double speed, double nowMs)
    {
        if (!(logGapMs >= 0 && logGapMs <= MaxGapMs))
            logGapMs = 0;

        var ts = Math.Min(logGapMs / speed, MaxSleepMs);

        var took = nowMs - _lastNowMs;
        _timeError = Math.Clamp(_timeError + (_plannedMs - took), -MaxSleepMs, MaxSleepMs);
        if (ts != 0)
        {
            ts += _timeError;
            _timeError = 0;
        }

        ts = Math.Clamp(ts, -MaxSleepMs, MaxSleepMs);
        _plannedMs = ts;
        _lastNowMs = nowMs;
        return ts;
    }
}

/// <summary>
/// Replays a telemetry log through the app's single <see cref="MAVLinkInterface"/> the way the
/// WinForms Flight Data screen does: <c>logreadmode</c> + <c>logplaybackfile</c>, the first
/// vehicle heartbeat selects the shown sysid/compid, each packet is read with
/// <c>readPacketAsync</c> (which decodes it into the vehicle's CurrentState) and paced by the
/// log's timestamps. Everything that reads <c>Session.Cs</c> then shows the log.
/// The playback loop runs on its own task; the control methods are thread-safe and return once
/// the loop has stopped touching the interface.
/// </summary>
public sealed class TlogPlaybackEngine : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(TlogPlaybackEngine));

    private readonly MAVLinkInterface _mav;
    private readonly ITlogClock _clock;

    // control operations (load, play, pause, seek, stop) one at a time
    private readonly object _gate = new();

    // the log stream and the decoded state: one packet read, or one seek, at a time
    private readonly SemaphoreSlim _io = new(1, 1);

    private CancellationTokenSource? _cancel;
    private Task _loop = Task.CompletedTask;
    private volatile TlogPlaybackState _state;
    private double _speed = 1;
    private double _position;
    private long _timeTicks = DateTime.MinValue.Ticks;
    private long _packets;

    public TlogPlaybackEngine(MAVLinkInterface mav, ITlogClock? clock = null)
    {
        _mav = mav;
        _clock = clock ?? SystemTlogClock.Instance;
    }

    /// <summary>The speeds offered, as in Mission Planner (x0.25 … x10).</summary>
    public static IReadOnlyList<double> Speeds { get; } = [0.1, 0.25, 0.5, 1, 2, 5, 10];

    public TlogPlaybackState State => _state;

    public bool IsLoaded => _state != TlogPlaybackState.Unloaded;

    public TlogInfo? Info { get; private set; }

    /// <summary>Playback speed; 1 is real time. Can change while playing.</summary>
    public double Speed
    {
        get => Volatile.Read(ref _speed);
        set => Volatile.Write(ref _speed, value > 0 ? Math.Min(value, 100) : 0.01);
    }

    /// <summary>Read position in the file, 0 … 1.</summary>
    public double Position => Volatile.Read(ref _position);

    /// <summary>Log time (local) of the last packet played, or <see cref="DateTime.MinValue"/> if not known yet.</summary>
    public DateTime CurrentTime => new(Interlocked.Read(ref _timeTicks), DateTimeKind.Local);

    /// <summary>Packets read since the log was loaded.</summary>
    public long PacketsRead => Interlocked.Read(ref _packets);

    /// <summary>The running (or last) playback loop; for tests.</summary>
    internal Task LoopTask => _loop;

    /// <summary>
    /// Opens a log, replacing any open one, and reads up to its first vehicle heartbeat to select
    /// the vehicle shown. Leaves it paused. Blocking (file I/O): call it off the UI thread.
    /// Throws <see cref="InvalidDataException"/> or an I/O exception for an unusable file.
    /// </summary>
    public void Load(string path)
    {
        var info = TlogScanner.Inspect(path);

        lock (_gate)
        {
            UnloadCore();

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            _io.Wait();
            try
            {
                Interlocked.Exchange(ref _packets, 0);
                SetTime(DateTime.MinValue);

                // the setter clears the vehicle list: the log starts from a blank state
                _mav.logreadmode = true;
                _mav.logplaybackfile = new BinaryReader(stream);
                _mav.lastlogread = DateTime.MinValue;

                // the GCS that recorded the log may have another sysid than ours, and if its heartbeat comes
                // first it becomes the current MAV; getHeartBeat skips GCS heartbeats, so take the vehicle from it
                var hb = _mav.getHeartBeat();
                if (hb.Length > 5)
                {
                    _mav.sysidcurrent = hb.sysid;
                    _mav.compidcurrent = hb.compid;
                }

                _mav.logreadmode = false;
                NoteTime(_mav.lastlogread, info);
                Volatile.Write(ref _position, Fraction(stream));
            }
            catch
            {
                _mav.logreadmode = false;
                CloseFile();
                throw;
            }
            finally
            {
                _io.Release();
            }

            Info = info;
            _state = TlogPlaybackState.Paused;
            Log.Info("tlog loaded " + path);
        }
    }

    /// <summary>Starts or resumes playback; after the end it starts again from the beginning.</summary>
    public void Play()
    {
        lock (_gate)
        {
            if (_state is TlogPlaybackState.Unloaded or TlogPlaybackState.Playing)
                return;

            JoinLoop();
            if (_state == TlogPlaybackState.Ended)
                SeekCore(0);

            _mav.logreadmode = true;
            var cancel = new CancellationTokenSource();
            _cancel = cancel;
            _state = TlogPlaybackState.Playing;
            _loop = Task.Run(() => RunAsync(cancel.Token));
        }
    }

    /// <summary>Pauses: reading stops and the interface leaves log-read mode, as in Mission Planner.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_state != TlogPlaybackState.Playing)
                return;

            JoinLoop();
            _mav.logreadmode = false;
            if (_state == TlogPlaybackState.Playing)
                _state = TlogPlaybackState.Paused;
        }
    }

    /// <summary>Stops playback and closes the log; the vehicle state it produced is cleared.</summary>
    public void Stop()
    {
        lock (_gate)
            UnloadCore();
    }

    /// <summary>
    /// Jumps to a fraction (0 … 1) of the file, at the next record boundary. The shown vehicle's
    /// state is reset, as Mission Planner does, so stale values don't linger. Works while playing.
    /// </summary>
    public void Seek(double fraction)
    {
        lock (_gate)
        {
            if (_state == TlogPlaybackState.Unloaded)
                return;
            SeekCore(double.IsNaN(fraction) ? 0 : fraction);
        }
    }

    public void Dispose()
    {
        Stop();
        _io.Dispose();
    }

    private void SeekCore(double fraction)
    {
        _io.Wait();
        try
        {
            var file = _mav.logplaybackfile;
            if (file == null)
                return;

            var stream = file.BaseStream;
            var target = (long)(stream.Length * Math.Clamp(fraction, 0, 1));
            var (position, time) = TlogScanner.AlignToRecord(stream, target);

            _mav.lastlogread = DateTime.MinValue;
            _mav.MAV.cs.ResetInternals();
            stream.Position = position;

            Volatile.Write(ref _position, Fraction(stream));
            SetTime(time ?? EstimateTime(fraction));
            if (_state == TlogPlaybackState.Ended)
                _state = TlogPlaybackState.Paused;
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        var pacer = new TlogPacer(_clock.NowMs);
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                double gapMs;
                await _io.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    var file = _mav.logplaybackfile;
                    if (file == null || !_mav.logreadmode)
                    {
                        _state = TlogPlaybackState.Ended;
                        return;
                    }

                    var previous = _mav.lastlogread;
                    if (!_mav.giveComport)
                    {
                        try
                        {
                            await _mav.readPacketAsync().ConfigureAwait(false);
                            Interlocked.Increment(ref _packets);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("tlog read", ex);
                        }
                    }

                    var now = _mav.lastlogread;
                    gapMs = previous == DateTime.MinValue || now == DateTime.MinValue ? 0 : (now - previous).TotalMilliseconds;
                    NoteTime(now, Info);

                    var stream = file.BaseStream;
                    Volatile.Write(ref _position, Fraction(stream));
                    var atEnd = stream.Position >= stream.Length;

                    // derived values (link quality, time in air, wind) and the vehicle's callbacks, as the live reader does
                    foreach (var mav in _mav.MAVlist)
                    {
                        try
                        {
                            mav.cs.UpdateCurrentSettings(null, atEnd, _mav, mav);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("UpdateCurrentSettings", ex);
                        }
                    }

                    if (atEnd)
                    {
                        // decided while holding the stream, so a seek that follows sees Ended and can resume
                        _mav.logreadmode = false;
                        _state = TlogPlaybackState.Ended;
                        Log.Info("tlog playback reached the end");
                        return;
                    }
                }
                finally
                {
                    _io.Release();
                }

                var sleep = pacer.Next(gapMs, Speed, _clock.NowMs);
                if (sleep > 0)
                    await _clock.DelayAsync(sleep, cancel).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // paused or stopped
        }
        catch (Exception ex)
        {
            Log.Error("tlog playback", ex);
            _mav.logreadmode = false;
            _state = TlogPlaybackState.Ended;
        }
    }

    /// <summary>Cancels the playback loop and waits until it has let go of the interface.</summary>
    private void JoinLoop()
    {
        var cancel = _cancel;
        _cancel = null;
        if (cancel == null)
            return;

        cancel.Cancel();
        try
        {
            if (!_loop.Wait(TimeSpan.FromSeconds(10)))
                Log.Error("tlog playback loop did not stop");
        }
        catch (AggregateException ex)
        {
            Log.Warn("tlog playback loop", ex);
        }

        cancel.Dispose();
    }

    private void UnloadCore()
    {
        if (_state == TlogPlaybackState.Unloaded && _mav.logplaybackfile == null)
            return;

        JoinLoop();
        _io.Wait();
        try
        {
            _mav.logreadmode = false;
            CloseFile();
            _mav.lastlogread = DateTime.MinValue;
        }
        finally
        {
            _io.Release();
        }

        Info = null;
        Volatile.Write(ref _position, 0);
        SetTime(DateTime.MinValue);
        _state = TlogPlaybackState.Unloaded;
    }

    private void CloseFile()
    {
        try
        {
            _mav.logplaybackfile?.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("tlog close", ex);
        }

        // the setter clears the vehicle list, so nothing from the log stays on screen
        _mav.logplaybackfile = null;
    }

    /// <summary>Keeps the last plausible log time: right after a seek the reader can see a stray timestamp.</summary>
    private void NoteTime(DateTime time, TlogInfo? info)
    {
        if (time == DateTime.MinValue)
            return;
        if (info != null && (time < info.Start.AddMinutes(-1) || time > info.End.AddMinutes(1)))
            return;
        SetTime(time);
    }

    private void SetTime(DateTime time) => Interlocked.Exchange(ref _timeTicks, time.Ticks);

    private DateTime EstimateTime(double fraction) =>
        Info is { } info ? info.Start + info.Duration * Math.Clamp(fraction, 0, 1) : DateTime.MinValue;

    private static double Fraction(Stream stream)
    {
        var length = stream.Length;
        return length > 0 ? Math.Clamp(stream.Position / (double)length, 0, 1) : 0;
    }
}
