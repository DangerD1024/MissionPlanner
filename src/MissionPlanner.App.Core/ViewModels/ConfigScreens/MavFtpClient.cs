using MissionPlanner.ArduPilot.Mavlink;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// The MAVFTP operations the file browser uses. Every call blocks until the vehicle answers or
/// times out; run them off the UI thread, one at a time. Failures are exceptions (the vehicle
/// NAKed with an errno, file not found) or false returns (timeouts, cancels).
/// </summary>
public interface IFtpClient
{
    /// <summary>Raised on the calling worker thread: (message, percent), percent -1 when unknown.</summary>
    event Action<string, int>? Progress;

    IReadOnlyList<FtpEntry> ListDirectory(string directory, CancellationTokenSource cancel);

    /// <summary>Null when the file could not be opened.</summary>
    MemoryStream? GetFile(string path, CancellationTokenSource cancel);

    void UploadFile(string remotePath, string localPath, CancellationTokenSource cancel);

    bool CalcFileCrc32(string path, ref uint crc, CancellationTokenSource cancel);

    bool CreateDirectory(string path, CancellationTokenSource cancel);

    bool RemoveFile(string path, CancellationTokenSource cancel);

    /// <summary>The vehicle only removes empty folders, so the contents go first.</summary>
    bool RemoveDirectoryRecursive(string path, CancellationTokenSource cancel);

    bool Rename(string from, string to, CancellationTokenSource cancel);

    /// <summary>Closes every open session on the vehicle (after a cancel).</summary>
    bool ResetSessions();

    /// <summary>CRC-32 as the vehicle computes it, to compare with <see cref="CalcFileCrc32"/>.</summary>
    uint LocalCrc32(byte[] data);
}

/// <summary><see cref="IFtpClient"/> over the library's <see cref="MAVFtp"/>.</summary>
public sealed class MavFtpClient : IFtpClient
{
    private readonly MAVFtp _ftp;

    public MavFtpClient(MAVLinkInterface mav, uint sysid, byte compid)
    {
        SysId = sysid;
        CompId = compid;
        _ftp = new MAVFtp(mav, sysid, compid);
        _ftp.Progress += (message, percent) => Progress?.Invoke(message, percent);
    }

    public uint SysId { get; }

    public byte CompId { get; }

    public event Action<string, int>? Progress;

    public IReadOnlyList<FtpEntry> ListDirectory(string directory, CancellationTokenSource cancel) =>
        (_ftp.kCmdListDirectory(directory, cancel) ?? [])
        .Select(f => new FtpEntry(directory, f.Name, f.isDirectory, f.Size, f.ModifiedUtc))
        .ToList();

    public MemoryStream? GetFile(string path, CancellationTokenSource cancel) => _ftp.GetFile(path, cancel, burst: false);

    public void UploadFile(string remotePath, string localPath, CancellationTokenSource cancel) =>
        _ftp.UploadFile(remotePath, localPath, cancel);

    public bool CalcFileCrc32(string path, ref uint crc, CancellationTokenSource cancel) =>
        _ftp.kCmdCalcFileCRC32(path, ref crc, cancel);

    public bool CreateDirectory(string path, CancellationTokenSource cancel) => _ftp.kCmdCreateDirectory(path, cancel);

    public bool RemoveFile(string path, CancellationTokenSource cancel) => _ftp.kCmdRemoveFile(path, cancel);

    public bool RemoveDirectoryRecursive(string path, CancellationTokenSource cancel) =>
        _ftp.RemoveDirectoryRecursive(path, cancel);

    public bool Rename(string from, string to, CancellationTokenSource cancel) => _ftp.kCmdRename(from, to, cancel);

    public bool ResetSessions() => _ftp.kCmdResetSessions();

    public uint LocalCrc32(byte[] data) => MAVFtp.crc_crc32(0, data);
}
