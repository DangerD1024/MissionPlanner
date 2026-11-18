using System.Globalization;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Logs;

/// <summary>Where downloaded DataFlash logs go and what they are called, as in Mission Planner.</summary>
public static class LogFileNaming
{
    /// <summary>&lt;log dir&gt;/&lt;vehicle type&gt;/&lt;sysid&gt;/</summary>
    public static string VehicleFolder(string logDir, string vehicleType, uint sysid) =>
        System.IO.Path.Combine(logDir, vehicleType, sysid.ToString(CultureInfo.InvariantCulture));

    /// <summary>The vehicle's clock time of a log entry (time_utc, seconds since 1970); null when it has none.</summary>
    public static DateTime? EntryTime(uint timeUtc) =>
        timeUtc == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(timeUtc).LocalDateTime;

    /// <summary>Mission Planner's MakeValidFileName.</summary>
    public static string MakeValidFileName(string name) =>
        name.Replace('/', '-').Replace('\\', '-').Replace(':', '-').Replace('?', ' ').Replace('"', '\'')
            .Replace('<', '[').Replace('>', ']').Replace('|', ' ');

    /// <summary>"&lt;no&gt; &lt;entry time&gt;.bin", the name a log has before it is renamed by its GPS time.</summary>
    public static string EntryFileName(ushort id, DateTime? time) =>
        MakeValidFileName($"{id} {(time ?? DateTime.UnixEpoch.ToLocalTime()).ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)}") + ".bin";

    /// <summary>"yyyy-MM-dd HH-mm-ss.bin" from the log's first GPS time.</summary>
    public static string GpsFileName(DateTime gpsStart) =>
        gpsStart.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".bin";

    /// <summary>First GPS time of a DataFlash log; null without a valid fix (or before 1990, as Mission Planner checks).</summary>
    public static DateTime? GpsStartTime(string logPath)
    {
        var log = new DFLogBuffer(logPath);
        try
        {
            var t = log.dflog.gpsstarttime;
            return t.Year >= 1990 ? t : null;
        }
        finally
        {
            log.Dispose();
        }
    }

    /// <summary>
    /// Moves a downloaded temp file into the vehicle folder as "&lt;no&gt; &lt;time&gt;.bin", then renames it
    /// by the log's GPS start time when it has one. An existing file of that name is kept: the
    /// new one gets a " (2)" suffix. Returns the final path.
    /// </summary>
    public static string StoreDownloaded(string tempFile, string vehicleFolder, ushort id, DateTime? entryTime)
    {
        Directory.CreateDirectory(vehicleFolder);
        var path = Unique(System.IO.Path.Combine(vehicleFolder, EntryFileName(id, entryTime)));
        File.Move(tempFile, path);

        DateTime? gps = null;
        try
        {
            gps = GpsStartTime(path);
        }
        catch (Exception)
        {
            // an unreadable log keeps its entry name
        }

        if (gps is { } start)
        {
            var renamed = Unique(System.IO.Path.Combine(vehicleFolder, GpsFileName(start)));
            File.Move(path, renamed);
            path = renamed;
        }

        return path;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
            return path;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}

/// <summary>One onboard log as the vehicle lists it.</summary>
public readonly record struct LogEntryInfo(ushort Id, uint TimeUtc, uint Size);

/// <summary>
/// Lists, downloads and erases onboard DataFlash logs over a link whose reader loop is running
/// (<see cref="Session.VehicleSession"/>), for one vehicle. Run the calls off the UI thread.
/// Downloads do the LOG_REQUEST_DATA exchange here rather than through MAVLinkInterface.GetLog,
/// which cannot be cancelled and spends seconds per log serialising its event handlers to a debug log.
/// </summary>
public sealed class LogDownloader(MAVLinkInterface mav, uint sysid, byte compid)
{
    /// <summary>One log transfer at a time per process: the library's transfer state is per link.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public MAVLinkInterface Mav { get; } = mav;

    public uint SysId { get; } = sysid;

    public byte CompId { get; } = compid;

    /// <summary>True while a list, download or erase holds the link.</summary>
    public static bool Busy => Gate.CurrentCount == 0;

    /// <summary>The vehicle's log list, by id. Throws TimeoutException when the vehicle does not answer.</summary>
    public async Task<IReadOnlyList<LogEntryInfo>> ListAsync()
    {
        if (!await Gate.WaitAsync(0).ConfigureAwait(false))
            throw new InvalidOperationException("Another log transfer is running.");
        try
        {
            // the library asks the current vehicle (MAV.sysid/compid); there is no overload taking them
#pragma warning disable CS0618
            var entries = await Mav.GetLogEntry(0, ushort.MaxValue).ConfigureAwait(false);
#pragma warning restore CS0618
            return entries.Values.OrderBy(e => e.id).Select(e => new LogEntryInfo(e.id, e.time_utc, e.size)).ToList();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Downloads one log into <paramref name="vehicleFolder"/> and names it as Mission Planner does.
    /// <paramref name="bytesReceived"/> gets the bytes received so far, a few times a second.
    /// Cancelling stops the transfer (the vehicle is told to stop sending) and keeps nothing.
    /// </summary>
    public async Task<string> DownloadAsync(LogEntryInfo entry, string vehicleFolder, Action<long>? bytesReceived = null,
        CancellationToken cancel = default)
    {
        if (!await Gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            throw new InvalidOperationException("Another log transfer is running.");

        string temp;
        try
        {
            Directory.CreateDirectory(vehicleFolder);
            temp = System.IO.Path.Combine(vehicleFolder, $".log{entry.Id}.part");
            try
            {
                await TransferAsync(entry, temp, bytesReceived, cancel).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }

        try
        {
            return LogFileNaming.StoreDownloaded(temp, vehicleFolder, entry.Id, LogFileNaming.EntryTime(entry.TimeUtc));
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>LOG_DATA carries this many bytes; requests are made in whole blocks.</summary>
    internal const int BlockSize = 90;

    /// <summary>Silence after which missing blocks are asked for again.</summary>
    internal const int RetryMs = 500;

    /// <summary>Requests in a row without any answer before the transfer gives up.</summary>
    internal const int MaxRetries = 8;

    /// <summary>
    /// The LOG_REQUEST_DATA / LOG_DATA exchange, as Mission Planner's GetLog does it: ask for the
    /// whole log, write each block where it belongs, then ask again for the blocks that did not
    /// arrive. Blocks arrive through the session's reader loop (OnPacketReceived).
    /// </summary>
    private async Task TransferAsync(LogEntryInfo entry, string path, Action<long>? bytesReceived, CancellationToken cancel)
    {
        var size = (long)entry.Size;
        var blocks = (int)((size + BlockSize - 1) / BlockSize);
        var have = new bool[blocks];
        var missing = blocks;
        long received = 0;

        var queue = System.Threading.Channels.Channel.CreateUnbounded<MAVLink.mavlink_log_data_t>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        EventHandler<MAVLink.MAVLinkMessage> handler = (_, m) =>
        {
            if (m.msgid != (uint)MAVLink.MAVLINK_MSG_ID.LOG_DATA || m.sysid != SysId || m.compid != CompId)
                return;
            var data = (MAVLink.mavlink_log_data_t)m.data;
            if (data.id == entry.Id)
                queue.Writer.TryWrite(data);
        };

        Mav.OnPacketReceived += handler;
        try
        {
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16,
                FileOptions.Asynchronous);
            file.SetLength(size);
            if (blocks == 0)
                return;

            Request(entry.Id, 0, uint.MaxValue);
            var lastData = Environment.TickCount64;
            var lastRequest = lastData;
            var lastReport = 0L;
            var retries = 0;

            while (missing > 0)
            {
                cancel.ThrowIfCancellationRequested();
                if (Mav.BaseStream == null || !Mav.BaseStream.IsOpen)
                    throw new IOException("The link closed.");

                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    wait.CancelAfter(100);
                    try
                    {
                        await queue.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                    {
                    }
                }

                var now = Environment.TickCount64;
                while (queue.Reader.TryRead(out var data))
                {
                    lastData = now;
                    retries = 0;

                    // a short or empty block marks the end of the log, which can come before the listed size
                    if (data.count < BlockSize && data.ofs + data.count < size)
                    {
                        size = data.ofs + data.count;
                        file.SetLength(size);
                        for (var b = (int)((size + BlockSize - 1) / BlockSize); b < blocks; b++)
                        {
                            if (!have[b])
                            {
                                have[b] = true;
                                missing--;
                            }
                        }
                    }

                    if (data.count == 0 || data.ofs >= size || data.data == null)
                        continue;

                    var count = (int)Math.Min(Math.Min(data.count, data.data.Length), size - data.ofs);
                    file.Position = data.ofs;
                    await file.WriteAsync(data.data.AsMemory(0, count), CancellationToken.None).ConfigureAwait(false);

                    // a block counts once it is whole; the vehicle answers at the offsets asked for
                    if (data.ofs % BlockSize != 0)
                        continue;
                    var block = (int)(data.ofs / BlockSize);
                    if (block < blocks && !have[block] && count == Math.Min(BlockSize, size - data.ofs))
                    {
                        have[block] = true;
                        missing--;
                        received += count;
                    }
                }

                if (bytesReceived != null && now - lastReport >= 250)
                {
                    lastReport = now;
                    bytesReceived(received);
                }

                if (missing == 0 || now - lastData < RetryMs || now - lastRequest < RetryMs)
                    continue;

                // silence: ask again from the first hole
                if (lastData < lastRequest && ++retries > MaxRetries)
                    throw new TimeoutException("The vehicle stopped sending the log.");
                var (ofs, len) = NextRequest(have, size);
                Request(entry.Id, ofs, len);
                lastRequest = now;
            }

            await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            bytesReceived?.Invoke(size);
        }
        finally
        {
            Mav.OnPacketReceived -= handler;
            // stop the stream (also after a cancel) and let the vehicle carry on logging
            try
            {
                Mav.generatePacket(MAVLink.MAVLINK_MSG_ID.LOG_REQUEST_END,
                    new MAVLink.mavlink_log_request_end_t((byte)SysId, CompId), SysId, CompId);
            }
            catch (Exception)
            {
                // the link is gone
            }
        }
    }

    private void Request(ushort id, uint ofs, uint count) =>
        Mav.generatePacket(MAVLink.MAVLINK_MSG_ID.LOG_REQUEST_DATA,
            new MAVLink.mavlink_log_request_data_t(ofs, count, id, (byte)SysId, CompId), SysId, CompId);

    /// <summary>
    /// The range to ask for next: from the first missing block to the end of the last one when
    /// the holes are close together (the vehicle streams the range in one go), else the first hole.
    /// </summary>
    internal static (uint Ofs, uint Count) NextRequest(bool[] have, long size)
    {
        var first = Array.IndexOf(have, false);
        if (first < 0)
            return (0, 0);
        var last = Array.LastIndexOf(have, false);

        var firstEnd = first;
        while (firstEnd + 1 < have.Length && !have[firstEnd + 1])
            firstEnd++;

        var holes = have.Skip(first).Take(last - first + 1).Count(h => !h);
        var span = last - first + 1;
        var end = span <= Math.Max(4 * holes, 1024) ? last : firstEnd;

        var ofs = (long)first * BlockSize;
        var stop = Math.Min(size, (long)(end + 1) * BlockSize);
        return ((uint)ofs, (uint)(stop - ofs));
    }

    /// <summary>Asks the vehicle to erase all its logs (sent twice: there is no answer to wait for).</summary>
    public void EraseAll()
    {
        var req = new MAVLink.mavlink_log_erase_t((byte)SysId, CompId);
        Mav.generatePacket(MAVLink.MAVLINK_MSG_ID.LOG_ERASE, req, SysId, CompId);
        Mav.generatePacket(MAVLink.MAVLINK_MSG_ID.LOG_ERASE, req, SysId, CompId);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
