using System.Buffers.Binary;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>Writes telemetry logs the way <c>MAVLinkInterface.SaveToTlog</c> does: 8-byte big-endian µs timestamp + raw packet.</summary>
internal sealed class TlogWriter : IDisposable
{
    public const byte VehicleSysId = 7;
    public const byte VehicleCompId = 1;

    private readonly FileStream _file;
    private readonly MAVLink.MavlinkParse _parse = new();
    private int _seq;

    public TlogWriter(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write);
    }

    public static DateTime T0 { get; } = new(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    public string Path { get; }

    public List<(long Offset, DateTime Time)> Records { get; } = new();

    public void Write(DateTime utc, MAVLink.MAVLINK_MSG_ID id, object msg, byte sysid = VehicleSysId, byte compid = VehicleCompId, bool mavlink1 = false)
    {
        Records.Add((_file.Position, utc));
        Span<byte> stamp = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(stamp, (ulong)((utc - DateTime.UnixEpoch).Ticks / 10));
        _file.Write(stamp);
        var bytes = mavlink1
            ? _parse.GenerateMAVLinkPacket10(id, msg, sysid, compid, _seq++ & 0xff)
            : _parse.GenerateMAVLinkPacket20(id, msg, false, sysid, compid, _seq++ & 0xff);
        _file.Write(bytes);
    }

    public void WriteRaw(ReadOnlySpan<byte> bytes) => _file.Write(bytes);

    public void Heartbeat(DateTime utc, bool gcs = false) =>
        Write(utc, MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t(
                custom_mode: 0,
                type: (byte)(gcs ? MAVLink.MAV_TYPE.GCS : MAVLink.MAV_TYPE.QUADROTOR),
                autopilot: (byte)(gcs ? MAVLink.MAV_AUTOPILOT.INVALID : MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA),
                base_mode: (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED,
                system_status: (byte)MAVLink.MAV_STATE.STANDBY,
                mavlink_version: 3),
            gcs ? (byte)255 : VehicleSysId, gcs ? (byte)190 : VehicleCompId);

    public void Attitude(DateTime utc, float rollDegrees) =>
        Write(utc, MAVLink.MAVLINK_MSG_ID.ATTITUDE,
            new MAVLink.mavlink_attitude_t((uint)(utc - T0).TotalMilliseconds, rollDegrees * (float)(Math.PI / 180), 0, 0, 0, 0, 0));

    public void Dispose() => _file.Dispose();

    /// <summary>
    /// A GCS heartbeat and a vehicle heartbeat at T0, then <paramref name="count"/> ATTITUDE records
    /// every 100 ms with roll = 1, 2, 3 … degrees, and a vehicle heartbeat every second.
    /// </summary>
    public static string CreateFlight(string path, int count)
    {
        using var w = new TlogWriter(path);
        w.Heartbeat(T0, gcs: true);
        w.Heartbeat(T0);
        for (var k = 1; k <= count; k++)
        {
            var t = T0.AddMilliseconds(100 * k);
            w.Attitude(t, k);
            if (k % 10 == 0)
                w.Heartbeat(t);
        }

        return path;
    }

    public static string TempPath(string name) =>
        System.IO.Path.Combine(TestEnvironment.DataRoot, name + "-" + Guid.NewGuid().ToString("N") + ".tlog");
}

/// <summary>
/// Virtual time for playback: a delay advances the clock by its length (plus <see cref="OversleepMs"/>)
/// without waiting. Delays after <see cref="BlockAfter"/> wait until cancelled, to hold playback mid-file.
/// </summary>
internal sealed class FakeTlogClock : ITlogClock
{
    private readonly object _lock = new();
    private double _now;
    private double _slept;
    private int _delays;

    public double OversleepMs { get; set; }

    public int BlockAfter { get; set; } = int.MaxValue;

    public double NowMs
    {
        get
        {
            lock (_lock)
                return _now;
        }
    }

    public double TotalSleptMs
    {
        get
        {
            lock (_lock)
                return _slept;
        }
    }

    public int Delays => Volatile.Read(ref _delays);

    public List<double> Requested { get; } = new();

    public async Task DelayAsync(double milliseconds, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var n = Interlocked.Increment(ref _delays);
        if (n > BlockAfter)
            await Task.Delay(Timeout.Infinite, cancel);

        lock (_lock)
        {
            Requested.Add(milliseconds);
            _now += milliseconds + OversleepMs;
            _slept += milliseconds + OversleepMs;
        }
    }
}

/// <summary>Inline dispatcher and dialogs that record what was shown.</summary>
internal sealed class TlogTestUi : IUiDispatcher, IDialogService, IWindowService
{
    public List<string> Messages { get; } = new();

    public string? FileToOpen { get; set; }

    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();

    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK") => Task.FromResult(true);

    public Task<string?> PromptAsync(string title, string message, string initialValue = "") => Task.FromResult<string?>(initialValue);

    public Task<string?> OpenFileAsync(string title, params FileFilter[] filters) => Task.FromResult(FileToOpen);

    public Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters) => Task.FromResult<string?>(null);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public void Show(PageViewModel page, double width = 1100, double height = 750)
    {
    }

    public Task<bool> ShowDialogAsync(PageViewModel page, double width = 1000, double height = 700) => Task.FromResult(false);

    public static AppServices Services(out TlogTestUi ui)
    {
        ui = new TlogTestUi();
        return new AppServices(ui, ui, ui);
    }
}

internal static class TlogTestExtensions
{
    /// <summary>Waits for the playback loop to finish (it runs on its own task).</summary>
    public static async Task WaitForLoopAsync(this TlogPlaybackEngine engine, int timeoutMs = 20000)
    {
        var loop = engine.LoopTask;
        var done = await Task.WhenAny(loop, Task.Delay(timeoutMs));
        Assert.AreSame(loop, done, "playback loop did not finish");
        await loop;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                Assert.Fail("condition not met in time");
            await Task.Delay(10);
        }
    }
}
