using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;
using MissionPlanner.App.Tests.SetupScreens.Rtk;

namespace MissionPlanner.App.Tests.SetupScreens.Joystick;

[TestClass]
public class JoystickViewModelTests
{
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PageListsDevicesMapsChannelsSavesAndSends()
    {
        var ui = new SetupTestUi();
        var backend = new FakeJoystickBackend();
        var pad = backend.Add("Test Pad");
        backend.Add("Other Stick");
        var vehicle = new FakeJoystickVehicle();
        var store = new JoystickProfileStore(Path.Combine(TestEnvironment.DataRoot, "joyvm-" + Guid.NewGuid().ToString("N")));
        var vm = new JoystickViewModel(ui.CreateServices(), () => backend, vehicle, store);

        vm.OnShown();
        await vm.BackendReady;
        Assert.AreEqual("Fake joystick backend", vm.BackendText);
        Assert.IsFalse(vm.BackendMissing);
        Assert.HasCount(2, vm.Devices);
        vm.SelectedDevice = vm.Devices.Single(d => d.Name == "Test Pad");
        Assert.IsTrue(vm.HasDevice);
        StringAssert.Contains(vm.DeviceText, "6 axes, 12 buttons, 1 hat");
        Assert.HasCount(16, vm.Channels);
        Assert.AreEqual(8, vm.Channels.Count(c => c.IsVisible));
        Assert.AreEqual("RC3 Throttle", vm.Channels[2].Label);
        Assert.HasCount(12, vm.ButtonRows);
        Assert.AreEqual("—", vm.ButtonRows[0].SelectedButton!.Label);

        // enabling with nothing mapped is refused
        await vm.ToggleSendingCommand.ExecuteAsync(null);
        Assert.IsFalse(vm.IsSending);
        Assert.Contains("Map at least one", ui.Messages.Last());

        // map RC1 to axis 1 and see the live output
        vm.Channels[0].SelectedInput = vm.InputOptions.Single(o => o.Input == JoystickInput.Axis(0));
        vm.Channels[0].Expo = 0;
        Assert.IsTrue(vm.IsDirty);
        pad.AxisValues[0] = short.MaxValue;
        pad.ButtonValues[4] = true;
        Assert.IsTrue(await Wait.Until(() => vm.Channels[0].OutputText == "2000", TimeSpan.FromSeconds(5), vm.UpdateLive));
        Assert.AreEqual(32767, vm.Axes[0].Value);
        Assert.IsTrue(vm.ButtonStates[4].IsPressed);
        Assert.AreEqual("centre", vm.Hats[0].Text);
        Assert.AreEqual("—", vm.Channels[1].OutputText);

        // buttons: Arm on button 5
        vm.ButtonRows[0].SelectedButton = vm.ButtonOptions.Single(o => o.Button == 4);
        vm.ButtonRows[0].SelectedFunction = ButtonFunctionInfo.For(JoystickButtonFunction.Arm);
        vm.ButtonRows[1].SelectedFunction = ButtonFunctionInfo.For(JoystickButtonFunction.Do_Set_Servo);
        Assert.AreEqual("Servo", vm.ButtonRows[1].P1Label);
        Assert.AreEqual(1500m, vm.ButtonRows[1].P2, "the function's default parameters");
        vm.ButtonRows[2].SelectedFunction = ButtonFunctionInfo.For(JoystickButtonFunction.ChangeMode);
        Assert.IsTrue(vm.ButtonRows[2].UsesMode);
        Assert.IsNotEmpty(vm.Modes);
        vm.ButtonRows[2].Mode = "Loiter";

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.IsFalse(vm.IsDirty);
        Assert.IsTrue(File.Exists(store.PathFor("Test Pad")));

        // sending
        pad.ButtonValues[4] = false;
        vm.UpdateLive();
        await Task.Delay(100);
        await vm.ToggleSendingCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.IsSending);
        Assert.AreEqual("Disable", vm.EnableText);
        Assert.IsTrue(await Wait.Until(() => vehicle.Overrides.Count > 5, TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2000, vehicle.Overrides.Last().chan1_raw);
        pad.ButtonValues[4] = true;
        Assert.IsTrue(await Wait.Until(() => vehicle.Actions.Contains("arm"), TimeSpan.FromSeconds(5)));
        Assert.IsTrue(await Wait.Until(() => vm.LastActionText.Contains("Arm"), TimeSpan.FromSeconds(5), vm.UpdateLive));
        StringAssert.Contains(vm.SendText, "RC_CHANNELS_OVERRIDE at 25 Hz to system 1");

        // leaving the page keeps sending; disabling releases
        vm.OnHidden();
        var count = vehicle.Overrides.Count;
        await Task.Delay(200);
        Assert.IsGreaterThan(count, vehicle.Overrides.Count);
        vm.OnShown();
        await vm.ToggleSendingCommand.ExecuteAsync(null);
        Assert.IsFalse(vm.IsSending);
        Assert.AreEqual(0, vehicle.Overrides.Last().chan1_raw);

        // hidden and not sending: the device is let go; coming back reloads the saved profile
        vm.OnHidden();
        Assert.IsFalse(vm.HasDevice);
        var fresh = new JoystickViewModel(ui.CreateServices(), () => backend, vehicle, store);
        fresh.OnShown();
        await fresh.BackendReady;
        Assert.AreEqual("Test Pad", fresh.SelectedDevice?.Name, "the last device is remembered");
        Assert.AreEqual(JoystickInput.Axis(0), fresh.Channels[0].SelectedInput!.Input);
        Assert.AreEqual(4, fresh.ButtonRows[0].SelectedButton!.Button);
        Assert.AreEqual("Loiter", fresh.ButtonRows[2].Mode);
        StringAssert.StartsWith(fresh.ProfileText, "Profile loaded");
        fresh.OnHidden();
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task MissingSupportIsExplained()
    {
        var ui = new SetupTestUi();
        var vm = new JoystickViewModel(ui.CreateServices(), () => new NoJoystickBackend("No joystick support: SDL2 was not found."),
            new FakeJoystickVehicle(), new JoystickProfileStore(Path.Combine(TestEnvironment.DataRoot, "joynone")));
        vm.OnShown();
        await vm.BackendReady;
        Assert.IsTrue(vm.BackendMissing);
        StringAssert.Contains(vm.BackendText, "SDL2 was not found");
        Assert.IsEmpty(vm.Devices);
        Assert.IsFalse(vm.HasDevice);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DetectAssignsTheMovedAxisAndPressedButton()
    {
        var ui = new SetupTestUi();
        var backend = new FakeJoystickBackend();
        var pad = backend.Add("Detect Pad");
        var vm = new JoystickViewModel(ui.CreateServices(), () => backend, new FakeJoystickVehicle(),
            new JoystickProfileStore(Path.Combine(TestEnvironment.DataRoot, "joydet-" + Guid.NewGuid().ToString("N"))));
        vm.OnShown();
        await vm.BackendReady;
        await Task.Delay(100);

        var detect = vm.Channels[3].DetectCommand.ExecuteAsync(null);
        await Task.Delay(150);
        pad.AxisValues[4] = -30000;
        await detect;
        Assert.AreEqual(JoystickInput.Axis(4), vm.Channels[3].SelectedInput!.Input);
        StringAssert.Contains(vm.StatusText, "RC4 Yaw ← Axis 5");

        var press = vm.ButtonRows[1].DetectCommand.ExecuteAsync(null);
        await Task.Delay(150);
        pad.ButtonValues[7] = true;
        await press;
        Assert.AreEqual(7, vm.ButtonRows[1].SelectedButton!.Button);
        vm.OnHidden();
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task UnsavedMappingSurvivesLeavingPageAndAppExit()
    {
        var ui = new SetupTestUi();
        var backend = new FakeJoystickBackend();
        backend.Add("Test Pad");
        var store = new JoystickProfileStore(Path.Combine(TestEnvironment.DataRoot, "joyautosave-" + Guid.NewGuid().ToString("N")));
        var vm = new JoystickViewModel(ui.CreateServices(), () => backend, new FakeJoystickVehicle(), store);
        vm.OnShown();
        await vm.BackendReady;
        vm.SelectedDevice = vm.Devices.Single(d => d.Name == "Test Pad");
        vm.Channels[0].SelectedInput = vm.InputOptions.Single(o => o.Input == JoystickInput.Axis(0));
        vm.ManualControl = true;
        Assert.IsTrue(vm.IsDirty);

        // leaving the page without pressing Save must persist the mapping
        vm.OnHidden();
        Assert.IsFalse(vm.IsDirty);
        Assert.IsTrue(File.Exists(store.PathFor("Test Pad")));
        var saved = store.Load("Test Pad");
        Assert.AreEqual(JoystickInput.Axis(0), saved!.Channel(1).Input);
        Assert.IsTrue(saved.ManualControl);

        // and so must app exit while the page is still on screen
        vm.OnShown();
        await vm.BackendReady;
        vm.Channels[1].SelectedInput = vm.InputOptions.Single(o => o.Input == JoystickInput.Axis(1));
        Assert.IsTrue(vm.IsDirty);
        vm.OnShutdown();
        Assert.IsFalse(vm.IsDirty);
        Assert.AreEqual(JoystickInput.Axis(1), store.Load("Test Pad")!.Channel(2).Input);
    }
}
