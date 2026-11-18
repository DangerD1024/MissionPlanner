using System.Text;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>
/// The ArduPilot/PX4 serial bootloader protocol (identify, CRC check, erase, program, verify,
/// reboot), ported from <c>ExtLibs/px4uploader/Uploader.cs</c> because that project is not
/// referenced by the new app. Same commands, timeouts and checks; works on any
/// <see cref="ICommsSerial"/> (the Comms serial port on every OS).
/// </summary>
public sealed class Px4Bootloader : IBootloader
{
    // response codes
    internal const byte INSYNC = 0x12;
    internal const byte OK = 0x10;
    internal const byte FAILED = 0x11;
    internal const byte INVALID = 0x13;

    // commands
    internal const byte EOC = 0x20;
    internal const byte GET_SYNC = 0x21;
    internal const byte GET_DEVICE = 0x22;
    internal const byte CHIP_ERASE = 0x23;
    internal const byte CHIP_VERIFY = 0x24;
    internal const byte PROG_MULTI = 0x27;
    internal const byte READ_MULTI = 0x28;
    internal const byte GET_CRC = 0x29;
    internal const byte GET_SN = 0x2b;
    internal const byte GET_CHIP = 0x2c;
    internal const byte GET_CHIP_DES = 0x2e;
    internal const byte REBOOT = 0x30;
    internal const byte EXTF_ERASE = 0x34;
    internal const byte EXTF_PROG_MULTI = 0x35;
    internal const byte EXTF_GET_CRC = 0x37;

    // GET_DEVICE parameters
    internal const byte INFO_BL_REV = 1;
    internal const byte INFO_BOARD_ID = 2;
    internal const byte INFO_BOARD_REV = 3;
    internal const byte INFO_FLASH_SIZE = 4;
    internal const byte INFO_EXTF_SIZE = 6;

    private const int BlRevMin = 2;
    private const int BlRevMax = 20;
    private const int ProgMultiMax = 252;

    private readonly ICommsSerial _port;
    private int _blRev;
    private int _flashSize;
    private int _extfSize;
    private int _boardId;

    /// <summary>Opens the port with the short timeouts the probe relies on; throws if it cannot be opened.</summary>
    public Px4Bootloader(ICommsSerial port)
    {
        _port = port;
        _port.ReadTimeout = 50;
        _port.WriteTimeout = 50;
        try
        {
            if (!_port.IsOpen)
                _port.Open();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string PortName => _port.PortName;

    public BootloaderInfo Identify()
    {
        _port.DiscardInBuffer();
        Sync();

        _blRev = GetInfo(INFO_BL_REV);
        if (_blRev < BlRevMin || _blRev > BlRevMax)
            throw new IOException($"Bootloader protocol mismatch (revision {_blRev}).");

        _port.WriteTimeout = 500;

        _boardId = GetInfo(INFO_BOARD_ID);
        var boardRev = GetInfo(INFO_BOARD_REV);
        _flashSize = GetInfo(INFO_FLASH_SIZE);

        int chip = 0;
        string? chipDes = null;
        string? sn = null;
        _extfSize = 0;
        if (_blRev >= 5)
        {
            try
            {
                chip = GetChip();
                chipDes = GetChipDescription();
            }
            catch (Exception)
            {
                Sync();
            }

            try
            {
                sn = Convert.ToHexString(GetSerialNumber());
            }
            catch (Exception)
            {
                Sync();
            }

            try
            {
                _extfSize = GetInfo(INFO_EXTF_SIZE);
            }
            catch (Exception)
            {
                Sync();
            }
        }

        return new BootloaderInfo(PortName, _boardId, boardRev, _blRev, _flashSize, _extfSize, chip, chipDes, sn);
    }

    public bool HasSameFirmware(ApjFirmware fw)
    {
        // revision 2 bootloaders cannot report a CRC
        if (_blRev < 3)
            return false;

        Sync();
        _port.ReadTimeout = 1000;

        var same = true;
        if (_flashSize > 0)
        {
            Send([GET_CRC, EOC]);
            var reported = (uint)RecvInt();
            GetSync();
            same &= reported == fw.Crc(_flashSize);
        }

        if (_extfSize > 0)
        {
            var size = BitConverter.GetBytes(fw.ExtfImageSize);
            Send([EXTF_GET_CRC, size[0], size[1], size[2], size[3], EOC]);
            WaitForBytes(4, TimeSpan.FromSeconds(30));
            var reported = (uint)RecvInt();
            GetSync();
            same &= reported == fw.ExtfCrc(fw.ExtfImageSize);
        }

        return same;
    }

    public void Upload(ApjFirmware fw, Action<FlashStep, double> progress, Action<string> log, CancellationToken cancel)
    {
        _port.ReadTimeout = 1000;

        if (_boardId != fw.BoardId && !(_boardId == 33 && fw.BoardId == 9))
            throw new InvalidOperationException($"Firmware not suitable for this board (firmware is for board {fw.BoardId}, the board is {_boardId}).");
        if (_flashSize != 0 && _flashSize < fw.ImageSize)
            throw new InvalidOperationException("The firmware image is too large for this board.");
        if (_extfSize != 0 && _extfSize < fw.ExtfImageSize)
            throw new InvalidOperationException("The external flash image is too large for this board.");

        // erasing internal flash first means a board unplugged while external flash is written
        // stays in its bootloader instead of booting half a firmware
        if (fw.ImageSize > 0)
        {
            log("Erasing…");
            progress(FlashStep.Erase, -1);
            Erase();
            progress(FlashStep.Erase, 100);
        }

        cancel.ThrowIfCancellationRequested();

        if (fw.ExtfImageSize > 0)
        {
            log("Erasing external flash…");
            EraseExternal(fw, p => progress(FlashStep.EraseExternal, p));
            log("Programming external flash…");
            ProgramChunks(fw.ExtfImage, EXTF_PROG_MULTI, p => progress(FlashStep.ProgramExternal, p), cancel);
            log("Verifying external flash…");
            VerifyExternal(fw, p => progress(FlashStep.VerifyExternal, p));
        }

        if (fw.ImageSize > 0)
        {
            log("Programming…");
            ProgramChunks(fw.Image, PROG_MULTI, p => progress(FlashStep.Program, p), cancel);
            log("Verifying…");
            progress(FlashStep.Verify, -1);
            if (_blRev == 2)
                VerifyV2(fw, p => progress(FlashStep.Verify, p));
            else
                VerifyV3(fw, log);
            progress(FlashStep.Verify, 100);
        }

        log("Done, rebooting.");
        Reboot();
    }

    public void Reboot()
    {
        try
        {
            Send([REBOOT, EOC]);
            _port.DiscardInBuffer();
        }
        catch (Exception)
        {
            // the board may already be gone
        }
    }

    public void Dispose()
    {
        try
        {
            _port.Close();
        }
        catch (Exception)
        {
        }

        try
        {
            _port.Dispose();
        }
        catch (Exception)
        {
        }
    }

    // ---- protocol ------------------------------------------------------------------------------

    private void Erase()
    {
        Sync();
        // bootloader bug: it must see a sync and a get_device before erasing
        GetInfo(INFO_BL_REV);
        Send([CHIP_ERASE, EOC]);
        WaitForBytes(1, TimeSpan.FromSeconds(20));
        GetSync();
    }

    private void EraseExternal(ApjFirmware fw, Action<double> progress)
    {
        Sync();
        GetInfo(INFO_BL_REV);
        var size = BitConverter.GetBytes(fw.ExtfImageSize);
        Send([EXTF_ERASE, size[0], size[1], size[2], size[3], EOC]);
        GetSync();

        // the bootloader streams percent bytes, then a sync
        var last = 0;
        while (true)
        {
            if (last < 95)
            {
                int pct = Recv(1)[0];
                if (pct != last)
                {
                    last = pct;
                    progress(pct);
                }
            }
            else if (TrySync())
            {
                progress(100);
                break;
            }
        }
    }

    private void ProgramChunks(byte[] image, byte command, Action<double> progress, CancellationToken cancel)
    {
        var chunks = (image.Length + ProgMultiMax - 1) / ProgMultiMax;
        for (var i = 0; i < chunks; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var offset = i * ProgMultiMax;
            var len = Math.Min(ProgMultiMax, image.Length - offset);
            var packet = new byte[len + 3];
            packet[0] = command;
            packet[1] = (byte)len;
            Array.Copy(image, offset, packet, 2, len);
            packet[^1] = EOC;
            Send(packet);
            GetSync();
            progress((i + 1) * 100.0 / chunks);
        }
    }

    private void VerifyV2(ApjFirmware fw, Action<double> progress)
    {
        Send([CHIP_VERIFY, EOC]);
        GetSync();
        var image = fw.Image;
        var chunks = (image.Length + ProgMultiMax - 1) / ProgMultiMax;
        for (var i = 0; i < chunks; i++)
        {
            var offset = i * ProgMultiMax;
            var len = Math.Min(ProgMultiMax, image.Length - offset);
            Send([READ_MULTI, (byte)len, EOC]);
            var programmed = Recv(len);
            if (!programmed.AsSpan().SequenceEqual(image.AsSpan(offset, len)))
                throw new IOException("Verification failed.");
            GetSync();
            progress((i + 1) * 100.0 / chunks);
        }
    }

    private void VerifyV3(ApjFirmware fw, Action<string> log)
    {
        var expect = fw.Crc(_flashSize);
        Send([GET_CRC, EOC]);
        var reported = (uint)RecvInt();
        GetSync();
        log($"CRC expected 0x{expect:X8}, board 0x{reported:X8}");
        if (reported != expect)
            throw new IOException("Program CRC failed.");
    }

    private void VerifyExternal(ApjFirmware fw, Action<double> progress)
    {
        var expect = fw.ExtfCrc(fw.ExtfImageSize);
        var size = BitConverter.GetBytes(fw.ExtfImageSize);
        Send([EXTF_GET_CRC, size[0], size[1], size[2], size[3], EOC]);

        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < 10_000)
        {
            if (_port.BytesToRead >= 4)
                break;
            progress((Environment.TickCount64 - start) / 100.0);
            Thread.Sleep(20);
        }

        WaitForBytes(4, TimeSpan.FromSeconds(20));
        var reported = (uint)RecvInt();
        GetSync();
        progress(100);
        if (reported != expect)
            throw new IOException("External flash CRC failed.");
    }

    private int GetInfo(byte param)
    {
        Send([GET_DEVICE, param, EOC]);
        var value = RecvInt();
        GetSync();
        return value;
    }

    private int GetChip()
    {
        Send([GET_CHIP, EOC]);
        var value = RecvInt();
        GetSync();
        return value;
    }

    private string GetChipDescription()
    {
        Send([GET_CHIP_DES, EOC]);
        var len = RecvInt();
        if (len is <= 0 or > 1024)
        {
            GetSync();
            return "";
        }

        var bytes = Recv(len);
        GetSync();
        return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
    }

    private byte[] GetSerialNumber()
    {
        var sn = new byte[12];
        for (var a = 0; a < 12; a += 4)
        {
            var addr = BitConverter.GetBytes(a);
            Send([GET_SN, addr[0], addr[1], addr[2], addr[3], EOC]);
            var word = Recv(4);
            GetSync();
            Array.Copy(word, 0, sn, a, 4);
        }

        return sn;
    }

    private void Sync()
    {
        _port.BaseStream?.Flush();
        Send([GET_SYNC, EOC]);
        GetSync();
    }

    private bool TrySync()
    {
        if (Recv(1)[0] != INSYNC)
            return false;
        return Recv(1)[0] == OK;
    }

    private void GetSync()
    {
        _port.BaseStream?.Flush();
        var deadline = Environment.TickCount64 + Math.Max(_port.ReadTimeout, 1);
        while (_port.BytesToRead == 0)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Timeout waiting for the bootloader.");
            Thread.Sleep(1);
        }

        var c = Recv(1)[0];
        if (c != INSYNC)
            throw new IOException($"Unexpected 0x{c:X2} instead of INSYNC.");
        c = Recv(1)[0];
        if (c == INVALID)
            throw new IOException("Bootloader reports INVALID OPERATION.");
        if (c == FAILED)
            throw new IOException("Bootloader reports OPERATION FAILED.");
        if (c != OK)
            throw new IOException($"Unexpected 0x{c:X2} instead of OK.");
    }

    private void WaitForBytes(int count, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (_port.BytesToRead >= count)
                return;
            Thread.Sleep(5);
        }
    }

    private void Send(byte[] data) => _port.Write(data, 0, data.Length);

    private int RecvInt() => BitConverter.ToInt32(Recv(4), 0);

    /// <summary>Reads exactly <paramref name="count"/> bytes; the port's read timeout ends a silent wait.</summary>
    private byte[] Recv(int count)
    {
        var buffer = new byte[count];
        var pos = 0;
        while (pos < count)
        {
            var n = _port.Read(buffer, pos, count - pos);
            if (n <= 0)
                throw new TimeoutException("Timeout reading from the bootloader.");
            pos += n;
            if (count >= 4 && pos >= 2 && buffer[0] == INSYNC && buffer[1] == INVALID)
                throw new IOException("Bad request (INSYNC INVALID).");
        }

        return buffer;
    }
}
