using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>An 8-bit grayscale picture from the sensor, and as BGRA for drawing.</summary>
public sealed class GrayImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    /// <summary>One byte per pixel, row by row.</summary>
    public byte[] Pixels { get; } = pixels;

    public byte[] ToBgra()
    {
        var bgra = new byte[Width * Height * 4];
        for (var i = 0; i < Width * Height; i++)
        {
            var v = Pixels[i];
            bgra[4 * i] = v;
            bgra[4 * i + 1] = v;
            bgra[4 * i + 2] = v;
            bgra[4 * i + 3] = 255;
        }

        return bgra;
    }
}

/// <summary>
/// Puts the PX4Flow's camera image back together (ExtLibs OpticalFlow, without System.Drawing): a
/// DATA_TRANSMISSION_HANDSHAKE gives the size and packet count, then ENCAPSULATED_DATA packets
/// carry <c>payload</c> bytes each at <c>seqnr × payload</c>; the last one completes the picture.
/// </summary>
public sealed class OpticalFlowImageAssembler
{
    private MAVLink.mavlink_data_transmission_handshake_t _handshake;
    private byte[] _buffer = [];
    private bool _started;

    /// <summary>A new picture starts.</summary>
    public void Handshake(MAVLink.mavlink_data_transmission_handshake_t handshake)
    {
        _handshake = handshake;
        _buffer = new byte[handshake.size];
        _started = true;
    }

    /// <summary>One piece; returns the picture when this was the last piece of it, else null.</summary>
    public GrayImage? Data(MAVLink.mavlink_encapsulated_data_t data)
    {
        if (!_started || _handshake.payload == 0 || data.data == null)
            return null;
        var start = _handshake.payload * data.seqnr;
        var left = _buffer.Length - start;
        if (left > 0)
            Array.Copy(data.data, 0, _buffer, start, Math.Min(Math.Min(data.data.Length, _handshake.payload), left));

        if (data.seqnr + 1 != _handshake.packets)
            return null;
        int w = _handshake.width, h = _handshake.height;
        if (w <= 0 || h <= 0)
            return null;
        // the sensor sends 8-bit grayscale; a short image leaves the rest black
        var pixels = new byte[w * h];
        Array.Copy(_buffer, pixels, Math.Min(pixels.Length, _buffer.Length));
        return new GrayImage(w, h, pixels);
    }
}

/// <summary>
/// Mission Planner's "opticalflow calib": VIDEO_ONLY=1 makes a PX4Flow send its camera image
/// instead of flow, shown here to focus the lens; stopping (or leaving the page) sets it back to 0.
/// </summary>
public sealed partial class OpticalFlowViewModel(AppServices services)
    : ToolPageViewModel("Optical flow image",
        "Shows the camera image of a PX4Flow optical flow sensor (VIDEO_ONLY mode), to focus its lens.",
        services)
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(OpticalFlowViewModel));

    private readonly OpticalFlowImageAssembler _assembler = new();
    private MAVLinkInterface? _mav;
    private uint _sysid;
    private byte _compid;
    private int _subHandshake = -1;
    private int _subData = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText))]
    private bool _isRunning;

    [ObservableProperty]
    private GrayImage? _image;

    [ObservableProperty]
    private int _images;

    public string StartStopText => IsRunning ? "Stop" : "Show the image";

    /// <summary>Raised on the UI thread when <see cref="Image"/> has a new picture.</summary>
    public event EventHandler? ImageChanged;

    [RelayCommand]
    private async Task StartStopAsync()
    {
        if (IsRunning)
        {
            await StopAsync();
            return;
        }

        if (!IsConnected)
        {
            Report("Connect to a vehicle first.", true);
            return;
        }

        var session = Services.Session;
        var v = session.Vehicle;
        _mav = session.Mav;
        _sysid = v.sysid;
        _compid = v.compid;
        Images = 0;
        _subHandshake = _mav.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.DATA_TRANSMISSION_HANDSHAKE, OnPacket, _sysid, _compid);
        _subData = _mav.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.ENCAPSULATED_DATA, OnPacket, _sysid, _compid);
        IsRunning = true;
        var ok = await RunAsync("Turning on VIDEO_ONLY", (mav, sys, comp) => mav.setParam(sys, comp, "VIDEO_ONLY", 1),
            "Calibration mode on: waiting for images from the sensor.",
            "Could not set VIDEO_ONLY (is a PX4Flow connected, and its parameters loaded?).");
        if (!ok)
            Unsubscribe();
    }

    private bool OnPacket(MAVLink.MAVLinkMessage msg)
    {
        try
        {
            GrayImage? done = null;
            lock (_assembler)
            {
                if (msg.msgid == (uint)MAVLink.MAVLINK_MSG_ID.DATA_TRANSMISSION_HANDSHAKE)
                    _assembler.Handshake(msg.ToStructure<MAVLink.mavlink_data_transmission_handshake_t>());
                else if (msg.msgid == (uint)MAVLink.MAVLINK_MSG_ID.ENCAPSULATED_DATA)
                    done = _assembler.Data(msg.ToStructure<MAVLink.mavlink_encapsulated_data_t>());
            }

            if (done != null)
            {
                Services.Dispatcher.Post(() =>
                {
                    Image = done;
                    Images++;
                    ImageChanged?.Invoke(this, EventArgs.Empty);
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("optical flow image", ex);
        }

        return true;
    }

    private void Unsubscribe()
    {
        if (_mav != null)
        {
            if (_subHandshake >= 0)
                _mav.UnSubscribeToPacketType(_subHandshake);
            if (_subData >= 0)
                _mav.UnSubscribeToPacketType(_subData);
        }

        _subHandshake = _subData = -1;
        IsRunning = false;
    }

    private async Task StopAsync()
    {
        var mav = _mav;
        var sys = _sysid;
        var comp = _compid;
        Unsubscribe();
        if (mav == null)
            return;
        try
        {
            await Task.Run(() => mav.setParam(sys, comp, "VIDEO_ONLY", 0));
            Report("Calibration mode off (VIDEO_ONLY = 0).");
        }
        catch (Exception ex)
        {
            Report("Could not set VIDEO_ONLY back to 0: " + ex.Message, true);
        }
    }

    public override void OnHidden()
    {
        base.OnHidden();
        if (IsRunning)
            _ = StopAsync();
    }

    public override void OnShutdown()
    {
        base.OnShutdown();
        if (IsRunning)
            _ = StopAsync();
    }
}
