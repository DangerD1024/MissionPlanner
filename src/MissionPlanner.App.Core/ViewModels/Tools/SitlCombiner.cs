using System.Net;
using System.Net.Sockets;
using log4net;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// Mission Planner's "sitl streamcombiner": connects to every SITL on this computer (TCP 5760, 5770,
/// 5780...), gives each vehicle its own system id (MAV_SYSID / SYSID_THISMAV = 1, 2, 3...) and serves
/// them all on one TCP port, so a single link shows every vehicle (vehicle list under the port).
/// The WinForms app opened one link per vehicle instead; this app has one link with several vehicles.
/// </summary>
public sealed class SitlCombiner : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SitlCombiner));

    public const int Port = 5750;

    public const int FirstSitlPort = 5760;

    public const int PortStep = 10;

    public const int MaxVehicles = 100;

    public static SitlCombiner Instance { get; } = new();

    private readonly List<TcpClient> _vehicles = new();
    private readonly object _gate = new();
    private TcpListener? _listener;
    private TcpClient? _gcs;
    private CancellationTokenSource? _stop;
    private uint _nextSysId;

    public bool IsRunning => _stop != null;

    public int VehicleCount
    {
        get
        {
            lock (_gate)
                return _vehicles.Count;
        }
    }

    /// <summary>Finds the SITLs, renumbers them and starts serving; returns how many vehicles were combined.</summary>
    public async Task<int> StartAsync(Action<string>? status = null, int firstPort = FirstSitlPort, int listenPort = Port, int maxVehicles = MaxVehicles)
    {
        Stop();
        _nextSysId = 1;
        var stop = new CancellationTokenSource();

        for (var i = 0; i < maxVehicles; i++)
        {
            var port = firstPort + PortStep * i;
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            }
            catch (Exception)
            {
                client.Dispose();
                // SITL instances are numbered without gaps (-I0, -I1...): the first closed port ends the search
                break;
            }

            status?.Invoke($"SITL on port {port}: assigning a system id…");
            var sysid = await Task.Run(() => AssignSysId(client));
            if (sysid == 0)
            {
                client.Dispose();
                continue;
            }

            lock (_gate)
                _vehicles.Add(client);
            Log.Info($"SITL {port} is now sysid {sysid}");
        }

        if (VehicleCount == 0)
            return 0;

        _listener = new TcpListener(IPAddress.Loopback, listenPort);
        _listener.Start();
        _stop = stop;
        _ = Task.Run(() => AcceptLoop(_listener, stop.Token));
        _ = Task.Factory.StartNew(() => PumpLoop(stop.Token), TaskCreationOptions.LongRunning);
        return VehicleCount;
    }

    /// <summary>Sets the vehicle's system id to the next free number and checks it took (as Mission Planner).</summary>
    private uint AssignSysId(TcpClient client)
    {
        var mav = new MAVLinkInterface { BaseStream = new TcpSerial { client = client } };
        try
        {
            var hb = mav.getHeartBeat();
            if (hb == MAVLink.MAVLinkMessage.Invalid)
                return 0;
            mav.sysidcurrent = hb.sysid;
            mav.compidcurrent = hb.compid;
            var parameter = "MAV_SYSID";
            try
            {
                mav.GetParam(mav.sysidcurrent, (byte)mav.compidcurrent, parameter);
            }
            catch (TimeoutException)
            {
                parameter = "SYSID_THISMAV";
                mav.GetParam(mav.sysidcurrent, (byte)mav.compidcurrent, parameter);
            }

            var sysid = _nextSysId++;
            mav.sendPacket(new MAVLink.mavlink_param_set_t
            {
                param_id = System.Text.Encoding.ASCII.GetBytes(parameter.PadRight(16, '\0')),
                param_value = sysid,
                param_type = (byte)mav.MAV.param_types[parameter],
                target_system = (byte)mav.sysidcurrent,
                target_component = (byte)mav.compidcurrent,
            }, mav.sysidcurrent, mav.compidcurrent);
            // ArduPilot switches its id at once: read it back at the new address
            mav.sysidcurrent = sysid;
            return mav.GetParam(parameter) == sysid ? sysid : 0;
        }
        catch (Exception ex)
        {
            Log.Warn("cannot assign a SITL system id", ex);
            return 0;
        }
        // the MAVLinkInterface is dropped without Dispose: that would close the client's socket
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                client.NoDelay = true;
                lock (_gate)
                {
                    _gcs?.Dispose();
                    _gcs = client;
                }
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("combiner accept", ex);
            }
        }
    }

    /// <summary>Whole MAVLink packets from every vehicle to the ground station; its bytes to every vehicle.</summary>
    private void PumpLoop(CancellationToken stop)
    {
        var parse = new MAVLink.MavlinkParse();
        var buffer = new byte[4096];
        while (!stop.IsCancellationRequested)
        {
            TcpClient? gcs;
            TcpClient[] vehicles;
            lock (_gate)
            {
                gcs = _gcs;
                vehicles = _vehicles.ToArray();
            }

            try
            {
                while (gcs is { Connected: true, Available: > 0 })
                {
                    var read = gcs.GetStream().Read(buffer, 0, buffer.Length);
                    foreach (var v in vehicles)
                        if (v.Connected)
                            v.GetStream().Write(buffer, 0, read);
                }
            }
            catch (Exception)
            {
                // the ground station went away; the next one is accepted
            }

            foreach (var v in vehicles)
            {
                try
                {
                    while (v.Connected && v.Available > 0)
                    {
                        var packet = parse.ReadPacket(v.GetStream());
                        if (packet != null && gcs is { Connected: true })
                            gcs.GetStream().Write(packet.buffer, 0, packet.Length);
                    }
                }
                catch (Exception)
                {
                    v.Close();
                }
            }

            Thread.Sleep(1);
        }
    }

    public void Stop()
    {
        var stop = Interlocked.Exchange(ref _stop, null);
        stop?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (Exception)
        {
        }

        _listener = null;
        lock (_gate)
        {
            _gcs?.Dispose();
            _gcs = null;
            foreach (var v in _vehicles)
                v.Dispose();
            _vehicles.Clear();
        }
    }

    public void Dispose() => Stop();
}
