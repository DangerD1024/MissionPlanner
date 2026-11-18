namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

/// <summary>Where one compass is in an onboard calibration.</summary>
public sealed record MagCalCompassStatus(
    byte CompassId,
    int Percent,
    int Attempt,
    MAVLink.MAG_CAL_STATUS? Result,
    float OffsetX,
    float OffsetY,
    float OffsetZ,
    float Fitness,
    bool Autosaved)
{
    public bool HasResult => Result != null;

    public bool Succeeded => Result == MAVLink.MAG_CAL_STATUS.MAG_CAL_SUCCESS;

    /// <summary>"Success", "Failed radius"… from the report status.</summary>
    public string ResultName => Result is { } r ? StatusName(r) : "";

    public static string StatusName(MAVLink.MAG_CAL_STATUS status)
    {
        var s = status.ToString().Replace("MAG_CAL_", "").Replace('_', ' ').ToLowerInvariant();
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }
}

/// <summary>
/// Onboard magnetometer calibration as Mission Planner runs it (ConfigHWCompass2.cs):
/// DO_START_MAG_CAL (all compasses, retry, autosave, no delay, no autoreboot), then
/// MAG_CAL_PROGRESS per compass and a MAG_CAL_REPORT when each finishes; Accept and Cancel send
/// DO_ACCEPT_MAG_CAL / DO_CANCEL_MAG_CAL. It is complete when every compass that reported
/// progress has an autosaved report; the autopilot then needs a reboot.
/// Events are raised on the link's thread.
/// </summary>
public sealed class MagCalibrationProcess
{
    private readonly ICalibrationLink _link;
    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly SortedDictionary<byte, MAVLink.mavlink_mag_cal_progress_t> _progress = new();
    private readonly SortedDictionary<byte, MAVLink.mavlink_mag_cal_report_t> _reports = new();

    public MagCalibrationProcess(ICalibrationLink link) => _link = link;

    /// <summary>Listening for progress (between Start and Accept/Cancel/completion).</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Every compass that reported progress has saved its result.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Raised on every progress message, report and state change.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once when the calibration completes.</summary>
    public event EventHandler? Completed;

    /// <summary>
    /// Starts calibrating all compasses. Returns false when the vehicle refuses (it is armed, or
    /// already calibrating); throws when it does not answer.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        var ok = await _link.CommandAsync(MAVLink.MAV_CMD.DO_START_MAG_CAL, 0, 1, 1, 0, 0, 0, 0).ConfigureAwait(false);
        if (!ok)
            return false;

        lock (_gate)
        {
            Unsubscribe();
            _progress.Clear();
            _reports.Clear();
            IsComplete = false;
            IsRunning = true;
            _subscriptions.Add(_link.Subscribe(MAVLink.MAVLINK_MSG_ID.MAG_CAL_PROGRESS, HandleMessage));
            _subscriptions.Add(_link.Subscribe(MAVLink.MAVLINK_MSG_ID.MAG_CAL_REPORT, HandleMessage));
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Accepts the results (needed only when they were not saved automatically).</summary>
    public Task<bool> AcceptAsync() => StopAsync(MAVLink.MAV_CMD.DO_ACCEPT_MAG_CAL);

    public Task<bool> CancelAsync() => StopAsync(MAVLink.MAV_CMD.DO_CANCEL_MAG_CAL);

    private async Task<bool> StopAsync(MAVLink.MAV_CMD command)
    {
        try
        {
            return await _link.CommandAsync(command, 0, 0, 1, 0, 0, 0, 0).ConfigureAwait(false);
        }
        finally
        {
            // stop listening whatever the vehicle said, as Mission Planner does
            lock (_gate)
            {
                Unsubscribe();
                IsRunning = false;
            }

            RaiseChanged();
        }
    }

    /// <summary>Stops listening without telling the vehicle (link lost).</summary>
    public void Detach()
    {
        lock (_gate)
        {
            Unsubscribe();
            IsRunning = false;
        }

        RaiseChanged();
    }

    public void HandleMessage(MAVLink.MAVLinkMessage msg)
    {
        if (msg.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MAG_CAL_PROGRESS)
            HandleProgress(msg.ToStructure<MAVLink.mavlink_mag_cal_progress_t>());
        else if (msg.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MAG_CAL_REPORT)
            HandleReport(msg.ToStructure<MAVLink.mavlink_mag_cal_report_t>());
    }

    public void HandleProgress(MAVLink.mavlink_mag_cal_progress_t progress)
    {
        lock (_gate)
        {
            if (!IsRunning)
                return;
            _progress[progress.compass_id] = progress;

            // with retry on, a failed compass starts again: its old report no longer applies
            if (_reports.TryGetValue(progress.compass_id, out var old) &&
                old.cal_status != (byte)MAVLink.MAG_CAL_STATUS.MAG_CAL_SUCCESS)
                _reports.Remove(progress.compass_id);
        }

        RaiseChanged();
    }

    public void HandleReport(MAVLink.mavlink_mag_cal_report_t report)
    {
        var completed = false;
        lock (_gate)
        {
            if (!IsRunning)
                return;

            // ignored by Mission Planner: an empty report the autopilot sends for compass 0
            if (report.compass_id == 0 && report.ofs_x == 0)
                return;

            _reports[report.compass_id] = report;

            if (_progress.Count > 0 && _progress.Keys.All(id => _reports.TryGetValue(id, out var r) && r.autosaved == 1))
            {
                IsComplete = true;
                IsRunning = false;
                Unsubscribe();
                completed = true;
            }
        }

        RaiseChanged();
        if (completed)
            Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Each compass seen so far, by id.</summary>
    public IReadOnlyList<MagCalCompassStatus> Snapshot()
    {
        lock (_gate)
        {
            var ids = _progress.Keys.Union(_reports.Keys).Distinct().OrderBy(i => i);
            return ids.Select(id =>
            {
                _progress.TryGetValue(id, out var p);
                if (_reports.TryGetValue(id, out var r))
                {
                    return new MagCalCompassStatus(id, 100, p.attempt, (MAVLink.MAG_CAL_STATUS)r.cal_status,
                        r.ofs_x, r.ofs_y, r.ofs_z, r.fitness, r.autosaved == 1);
                }

                return new MagCalCompassStatus(id, p.completion_pct, p.attempt, null, 0, 0, 0, 0, false);
            }).ToList();
        }
    }

    private void Unsubscribe()
    {
        foreach (var s in _subscriptions)
            s.Dispose();
        _subscriptions.Clear();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
