using System.IO.MemoryMappedFiles;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Core.Video;

/// <summary>
/// Publishes raw BGRA HUD frames into a /dev/shm file for a same-machine consumer
/// (VOT_C CAM=SHM): no encoder, no decoder, no network — one frame of latency.
/// Seqlock over three slots: the writer fills the next slot, then publishes slot and
/// seq; a reader copies a slot and accepts it only if seq did not change during the
/// copy, so it never sees a half-written frame and never queues stale ones.
/// Header (little endian, 64 bytes): magic u32, version u32, width u32, height u32,
/// stride u32, slots u32, seq u64, timestamp_us u64, slot u32, reserved u32.
/// </summary>
public sealed class ShmFrameWriter : IDisposable
{
    public const uint Magic = 0x4D504855;
    public const int HeaderBytes = 64;
    public const int Slots = 3;

    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private readonly string _path;
    private int _width;
    private int _height;
    private int _frameBytes;
    private long _seq;
    private int _slot = -1;

    public ShmFrameWriter(string? path = null)
    {
        _path = path ?? StreamSettings.ShmPath;
    }

    public void Write(byte[] bgra, int width, int height)
    {
        var frameBytes = width * height * 4;
        if (_view == null || _width != width || _height != height)
        {
            _view?.Dispose();
            _file?.Dispose();
            // shared, so a reader in another process can open the file while it is written (Windows locks it otherwise)
            var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            _file = MemoryMappedFile.CreateFromFile(stream, null, HeaderBytes + Slots * frameBytes,
                MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            _view = _file.CreateViewAccessor(0, HeaderBytes + Slots * frameBytes);
            _width = width;
            _height = height;
            _frameBytes = frameBytes;
            _seq = 0;
            _slot = -1;
            _view.Write(0, Magic);
            _view.Write(4, 1u);
            _view.Write(8, (uint)width);
            _view.Write(12, (uint)height);
            _view.Write(16, (uint)(width * 4));
            _view.Write(20, (uint)Slots);
        }

        if (bgra.Length < frameBytes)
            return;
        _slot = (_slot + 1) % Slots;
        _view.WriteArray(HeaderBytes + _slot * frameBytes, bgra, 0, frameBytes);
        Thread.MemoryBarrier();
        _view.Write(40, (uint)_slot);
        _view.Write(32, (ulong)(Environment.TickCount64 * 1000));
        Thread.MemoryBarrier();
        _view.Write(24, (ulong)(++_seq));
    }

    public void Dispose()
    {
        _view?.Dispose();
        _file?.Dispose();
        _view = null;
        _file = null;
    }
}