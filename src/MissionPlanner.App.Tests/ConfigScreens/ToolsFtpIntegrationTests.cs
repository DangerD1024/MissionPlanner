using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>
/// The MAVFtp page's client through the unchanged library (MAVFtp over MAVLinkInterface and the
/// session's reader), against a fake vehicle's MAVFTP server on TCP loopback.
/// </summary>
[TestClass]
public class ToolsFtpIntegrationTests
{
    private static async Task<(VehicleSession Session, ToolsFakeVehicle Vehicle, TcpClient Socket)> ConnectAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var session = new VehicleSession();
        var accept = listener.AcceptTcpClientAsync();
        var connect = session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        var socket = await accept;
        listener.Stop();
        var vehicle = new ToolsFakeVehicle(socket.GetStream());
        await connect;
        return (session, vehicle, socket);
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task ClientListsTransfersAndEditsFilesOnAVehicle()
    {
        var (session, vehicle, socket) = await ConnectAsync();
        try
        {
            vehicle.AddDir("/APM");
            vehicle.AddDir("/APM/LOGS");
            vehicle.AddFile("/APM/LOGS/00000001.BIN", Enumerable.Range(0, 700).Select(i => (byte)i).ToArray());
            var local = Path.Combine(TestEnvironment.DataRoot, "upload-" + Guid.NewGuid().ToString("N") + ".lua");
            var content = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
            File.WriteAllBytes(local, content);

            var client = new MavFtpClient(session.Mav, session.Vehicle.sysid, session.Vehicle.compid);
            var cancel = new CancellationTokenSource();
            await Task.Run(() =>
            {
                // listing: dots are returned by the vehicle and dropped by the page's model
                var root = client.ListDirectory("/", cancel);
                var listing = new FtpListing();
                listing.Load("/", root);
                Assert.AreEqual("APM", string.Join(",", listing.View().Select(e => e.Name)));
                Assert.IsTrue(listing.All[0].IsDirectory);

                var logs = client.ListDirectory("/APM/LOGS", cancel);
                var log = logs.Single(e => e.Name == "00000001.BIN");
                Assert.AreEqual(700UL, log.Size);
                Assert.AreEqual(vehicle.FileTime, log.ModifiedUtc);
                Assert.AreEqual("/APM/LOGS/00000001.BIN", log.Path);

                // download (several read packets)
                var data = client.GetFile("/APM/LOGS/00000001.BIN", cancel);
                Assert.IsNotNull(data);
                CollectionAssert.AreEqual(vehicle.GetFile("/APM/LOGS/00000001.BIN"), data.ToArray());

                // new folder, upload with CRC check, rename
                Assert.IsTrue(client.CreateDirectory("/APM/scripts", cancel));
                Assert.IsTrue(vehicle.HasDir("/APM/scripts"));
                client.UploadFile("/APM/scripts/up.lua", local, cancel);
                CollectionAssert.AreEqual(content, vehicle.GetFile("/APM/scripts/up.lua"));
                uint crc = 0;
                Assert.IsTrue(client.CalcFileCrc32("/APM/scripts/up.lua", ref crc, cancel));
                Assert.AreEqual(client.LocalCrc32(content), crc);
                Assert.IsTrue(client.Rename("/APM/scripts/up.lua", "/APM/scripts/main.lua", cancel));
                Assert.IsNotNull(vehicle.GetFile("/APM/scripts/main.lua"));

                // a folder with contents goes recursively
                Assert.IsTrue(client.RemoveDirectoryRecursive("/APM/scripts", cancel));
                Assert.IsFalse(vehicle.HasDir("/APM/scripts"));
                Assert.IsNull(vehicle.GetFile("/APM/scripts/main.lua"));

                Assert.IsTrue(client.RemoveFile("/APM/LOGS/00000001.BIN", cancel));
                Assert.IsNull(vehicle.GetFile("/APM/LOGS/00000001.BIN"));

                // a missing folder is an exception the page shows
                Assert.ThrowsExactly<FileNotFoundException>(() => client.ListDirectory("/nope", cancel));
            });
        }
        finally
        {
            await session.DisposeAsync();
            socket.Dispose();
            await vehicle.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task PageBrowsesAVehicle()
    {
        var (session, vehicle, socket) = await ConnectAsync();
        using var ui = new ToolsUiThread();
        try
        {
            vehicle.AddDir("/APM");
            vehicle.AddFile("/APM/defaults.parm", "FRAME_CLASS 1\n"u8.ToArray());

            // the page's own client factory, bound to the session's vehicle
            var services = new AppServices(ui, new ToolsDialogs(), new ToolsWindows());
            var vm = await ui.Run(() => new MavFtpViewModel(services, () => new MavFtpClient(session.Mav, session.Vehicle.sysid, session.Vehicle.compid)));
            await ui.Run(() => vm.OnShown());
            await ui.WaitFor(() => !vm.IsBusy && vm.Entries.Count > 0, "the root listing", 30000);
            Assert.AreEqual("APM", await ui.Run(() => string.Join(",", vm.Entries.Select(e => e.Name))));

            await ui.Run(() => vm.OpenEntryAsync(vm.Entries[0]));
            await ui.WaitFor(() => !vm.IsBusy && vm.CurrentPath == "/APM" && vm.Entries.Count > 0, "the /APM listing", 30000);
            Assert.AreEqual("defaults.parm", await ui.Run(() => vm.Entries[0].Name));
            Assert.IsNull(await ui.Run(() => vm.Error));
        }
        finally
        {
            await session.DisposeAsync();
            socket.Dispose();
            await vehicle.DisposeAsync();
        }
    }
}
