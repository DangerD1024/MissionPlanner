using System.Runtime.InteropServices;
using log4net;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Core.Video;

/// <summary>A video capture device: what to show, and what goes into <see cref="VideoSourceConfig.Device"/>.</summary>
public sealed record CaptureDevice(string Name, string Id)
{
    public override string ToString() => Id == Name ? Name : $"{Name} ({Id})";
}

/// <summary>
/// The capture devices GStreamer can open here (EasyCap and other analog grabbers, UVC webcams, HDMI
/// capture), from its device monitor: Media Foundation on Windows, Video4Linux on Linux,
/// AVFoundation on macOS.
/// </summary>
public static class CaptureDevices
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(CaptureDevices));

    /// <summary>The devices now connected; empty without GStreamer or without devices. Blocks briefly: call off the UI thread.</summary>
    public static IReadOnlyList<CaptureDevice> List()
    {
        if (!GstNative.TryInitialize(out _))
            return [];
        var found = new List<CaptureDevice>();
        var monitor = GstNative.DeviceMonitorNew();
        if (monitor == IntPtr.Zero)
            return found;
        try
        {
            GstNative.DeviceMonitorAddFilter(monitor, "Video/Source", IntPtr.Zero);
            var list = GstNative.DeviceMonitorGetDevices(monitor);
            var platform = VideoPipelineBuilder.CurrentCapturePlatform;
            var index = 0;
            // GList { gpointer data; GList *next; GList *prev; }
            for (var node = list; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var device = Marshal.ReadIntPtr(node);
                try
                {
                    if (Describe(device, platform, index) is { } d && found.All(f => f.Id != d.Id))
                        found.Add(d);
                }
                finally
                {
                    GstNative.ObjectUnref(device);
                }

                index++;
            }

            GstNative.GListFree(list);
        }
        catch (Exception ex)
        {
            Log.Warn("capture devices: " + ex.Message);
        }
        finally
        {
            GstNative.ObjectUnref(monitor);
        }

        return found;
    }

    private static CaptureDevice? Describe(IntPtr device, VideoPipelineBuilder.CapturePlatform platform, int index)
    {
        var namePtr = GstNative.DeviceGetDisplayName(device);
        var name = Marshal.PtrToStringUTF8(namePtr) ?? "";
        GstNative.GFree(namePtr);
        if (name.Length == 0)
            return null;

        switch (platform)
        {
            case VideoPipelineBuilder.CapturePlatform.Linux:
                // v4l2src needs the node; devices without one (PipeWire's) are left out
                var path = Property(device, "api.v4l2.path") ?? Property(device, "device.path");
                return path != null && path.StartsWith("/dev/video", StringComparison.Ordinal) ? new CaptureDevice(name, path) : null;
            case VideoPipelineBuilder.CapturePlatform.MacOS:
                return new CaptureDevice(name, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            default:
                // mfvideosrc opens a device by its name; names with quotes cannot be put in a pipeline
                return name.Contains('"') ? null : new CaptureDevice(name, name);
        }
    }

    private static string? Property(IntPtr device, string field)
    {
        var props = GstNative.DeviceGetProperties(device);
        if (props == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(GstNative.StructureGetString(props, field));
        }
        finally
        {
            GstNative.StructureFree(props);
        }
    }
}
