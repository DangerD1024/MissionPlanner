using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;
using MissionPlanner.App.Core.Links.Usb;

namespace MissionPlanner.Mobile.Android;

/// <summary>
/// The phone's USB host for the shared USB serial drivers: lists the attached devices, asks the
/// user for permission (the system dialog) and opens them.
/// </summary>
public sealed class AndroidUsbHost : IUsbHost
{
    private readonly Context _context;
    private readonly UsbManager _manager;
    private readonly string _permissionAction;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pending = new();

    public AndroidUsbHost(Context context)
    {
        _context = context;
        _manager = (UsbManager)context.GetSystemService(Context.UsbService)!;
        _permissionAction = context.PackageName + ".USB_PERMISSION";

        var filter = new IntentFilter();
        filter.AddAction(UsbManager.ActionUsbDeviceAttached);
        filter.AddAction(UsbManager.ActionUsbDeviceDetached);
        filter.AddAction(_permissionAction);
        var receiver = new Receiver(this);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else
            context.RegisterReceiver(receiver, filter);
    }

    public event EventHandler? DevicesChanged;

    /// <summary>The activity was opened for a device that was plugged in.</summary>
    public void NotifyChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<UsbDeviceInfo> Devices() =>
        (_manager.DeviceList?.Values ?? (ICollection<UsbDevice>)Array.Empty<UsbDevice>())
            .OrderBy(d => d.DeviceName, StringComparer.Ordinal)
            .Select(Describe)
            .ToList();

    public bool HasPermission(UsbDeviceInfo device) =>
        Find(device) is { } d && _manager.HasPermission(d);

    public Task<bool> RequestPermissionAsync(UsbDeviceInfo device)
    {
        if (Find(device) is not { } d)
            return Task.FromResult(false);
        if (_manager.HasPermission(d))
            return Task.FromResult(true);

        TaskCompletionSource<bool> tcs;
        lock (_gate)
        {
            if (_pending.TryGetValue(d.DeviceName, out var existing))
                return existing.Task;
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[d.DeviceName] = tcs;
        }

        // explicit (package set) and mutable, so the system can add the device and the answer
        var intent = new Intent(_permissionAction).SetPackage(_context.PackageName);
        var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S ? PendingIntentFlags.Mutable : 0;
        var pending = PendingIntent.GetBroadcast(_context, 0, intent, flags);
        _manager.RequestPermission(d, pending);
        return tcs.Task;
    }

    public IUsbConnection? Open(UsbDeviceInfo device)
    {
        if (Find(device) is not { } d || !_manager.HasPermission(d))
            return null;
        var connection = _manager.OpenDevice(d);
        return connection == null ? null : new Connection(d, connection);
    }

    private UsbDevice? Find(UsbDeviceInfo device) =>
        _manager.DeviceList?.Values.FirstOrDefault(d => d.DeviceName == device.Name);

    private static UsbDeviceInfo Describe(UsbDevice d)
    {
        var interfaces = new List<UsbInterfaceInfo>();
        for (var i = 0; i < d.InterfaceCount; i++)
        {
            var iface = d.GetInterface(i);
            if (iface.AlternateSetting != 0)
                continue;
            var endpoints = new List<UsbEndpointInfo>();
            for (var e = 0; e < iface.EndpointCount; e++)
            {
                var ep = iface.GetEndpoint(e)!;
                endpoints.Add(new UsbEndpointInfo(
                    (int)ep.Address,
                    ep.Direction == UsbAddressing.In,
                    ep.Type switch
                    {
                        UsbAddressing.XferBulk => UsbTransferType.Bulk,
                        UsbAddressing.XferInterrupt => UsbTransferType.Interrupt,
                        UsbAddressing.XferIsochronous => UsbTransferType.Isochronous,
                        _ => UsbTransferType.Control,
                    },
                    ep.MaxPacketSize));
            }

            interfaces.Add(new UsbInterfaceInfo(iface.Id, (int)iface.InterfaceClass, (int)iface.InterfaceSubclass, (int)iface.InterfaceProtocol, endpoints));
        }

        string? manufacturer = null, product = null;
        try
        {
            manufacturer = d.ManufacturerName;
            product = d.ProductName;
        }
        catch (Java.Lang.SecurityException)
        {
            // some phones hide the strings until the permission is given
        }

        return new UsbDeviceInfo(d.DeviceName, d.VendorId, d.ProductId, manufacturer, product, interfaces);
    }

    private void OnBroadcast(Intent intent)
    {
        if (intent.Action == _permissionAction)
        {
#pragma warning disable CA1422 // the typed overload is API 33+
            var device = (UsbDevice?)intent.GetParcelableExtra(UsbManager.ExtraDevice);
#pragma warning restore CA1422
            var granted = intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false);
            List<TaskCompletionSource<bool>> answered;
            lock (_gate)
            {
                var keys = device == null ? _pending.Keys.ToList() : _pending.Keys.Where(k => k == device.DeviceName).ToList();
                answered = keys.Select(k => _pending[k]).ToList();
                foreach (var k in keys)
                    _pending.Remove(k);
            }

            foreach (var tcs in answered)
                tcs.TrySetResult(granted || (device != null && _manager.HasPermission(device)));
            return;
        }

        NotifyChanged();
    }

    private sealed class Receiver(AndroidUsbHost host) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent != null)
                host.OnBroadcast(intent);
        }
    }

    /// <summary>An open device; interfaces claimed through it are released on dispose.</summary>
    private sealed class Connection(UsbDevice device, UsbDeviceConnection connection) : IUsbConnection
    {
        private readonly List<UsbInterface> _claimed = new();
        private readonly Dictionary<int, UsbEndpoint> _endpoints = new();
        private bool _disposed;

        public bool ClaimInterface(int number)
        {
            for (var i = 0; i < device.InterfaceCount; i++)
            {
                var iface = device.GetInterface(i);
                if (iface.Id != number || iface.AlternateSetting != 0)
                    continue;
                if (!connection.ClaimInterface(iface, true))
                    return false;
                _claimed.Add(iface);
                for (var e = 0; e < iface.EndpointCount; e++)
                {
                    var ep = iface.GetEndpoint(e)!;
                    _endpoints[(int)ep.Address] = ep;
                }

                return true;
            }

            return false;
        }

        public int ControlTransfer(int requestType, int request, int value, int index, byte[]? buffer, int length, int timeoutMs) =>
            connection.ControlTransfer((UsbAddressing)requestType, request, value, index, buffer, length, timeoutMs);

        public int BulkTransfer(UsbEndpointInfo endpoint, byte[] buffer, int offset, int length, int timeoutMs)
        {
            if (!_endpoints.TryGetValue(endpoint.Address, out var ep))
                return -1;
            if (offset == 0)
                return connection.BulkTransfer(ep, buffer, length, timeoutMs);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                return connection.BulkTransfer(ep, buffer, offset, length, timeoutMs);
            var copy = new byte[length];
            if (!endpoint.IsIn)
                Array.Copy(buffer, offset, copy, 0, length);
            var n = connection.BulkTransfer(ep, copy, length, timeoutMs);
            if (endpoint.IsIn && n > 0)
                Array.Copy(copy, 0, buffer, offset, n);
            return n;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var iface in _claimed)
            {
                try
                {
                    connection.ReleaseInterface(iface);
                }
                catch (Exception)
                {
                    // unplugged
                }
            }

            connection.Close();
        }
    }
}
