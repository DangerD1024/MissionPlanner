using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Tests.ConfigScreens;

namespace MissionPlanner.App.Tests.SetupScreens;

/// <summary>Flight Modes against a parameter store in memory.</summary>
[TestClass]
public sealed class FlightModesViewModelTests
{
    private static FakeParamSource Copter()
    {
        var src = new FakeParamSource();
        for (var i = 1; i <= 6; i++)
            src.Values["FLTMODE" + i] = 0;
        src.Values["FLTMODE_CH"] = 5;
        src.Metas["FLTMODE_CH"] = FakeParamSource.MetaWith("FLTMODE_CH",
            values: [new(0, "Disabled"), new(5, "Channel5"), new(6, "Channel6"), new(7, "Channel7"), new(8, "Channel8")]);
        return src;
    }

    [TestMethod]
    public async Task TheModeChannelIsChosenFromAListAndSavedWithSaveModes()
    {
        var (services, _, _) = ParamTestSupport.CreateServices();
        var src = Copter();
        var vm = new FlightModesViewModel(services, src);
        vm.OnShown();

        Assert.IsNotNull(vm.Channel);
        Assert.AreEqual("FLTMODE_CH", vm.Channel.Name);
        Assert.AreEqual(ParamEditorKind.Options, vm.Channel.Kind);
        CollectionAssert.AreEqual(new[] { "Disabled", "Channel5", "Channel6", "Channel7", "Channel8" }, vm.Channel.Options.Select(o => o.Label).ToArray());
        Assert.AreEqual(5.0, vm.Channel.SelectedOption?.Value);
        Assert.IsFalse(vm.HasChanges);

        // queued: nothing is written until Save Modes
        vm.Channel.SelectedOption = vm.Channel.Options.Single(o => o.Value == 7);
        Assert.IsTrue(vm.HasChanges);
        Assert.IsEmpty(src.Writes);

        await vm.SaveCommand.ExecuteAsync(null);
        CollectionAssert.Contains(src.Writes, ("FLTMODE_CH", 7.0));
        Assert.AreEqual(7.0, src.Values["FLTMODE_CH"]);
        Assert.IsFalse(vm.HasChanges);
    }

    [TestMethod]
    public void UndoPutsTheChannelBack()
    {
        var (services, _, _) = ParamTestSupport.CreateServices();
        var src = Copter();
        var vm = new FlightModesViewModel(services, src);
        vm.OnShown();

        vm.Channel!.SelectedOption = vm.Channel.Options.Single(o => o.Value == 8);
        Assert.IsTrue(vm.HasChanges);
        vm.RevertCommand.Execute(null);
        Assert.AreEqual(5.0, vm.Channel.SelectedOption?.Value);
        Assert.IsFalse(vm.HasChanges);
        Assert.IsEmpty(src.Writes);
    }

    [TestMethod]
    public void WithoutDocumentedValuesChannels1To16AreOffered()
    {
        var options = FlightModeLogic.ChannelOptions(null);
        Assert.HasCount(16, options);
        Assert.AreEqual(1.0, options[0].Value);
        Assert.AreEqual("Channel 16", options[15].Label);
    }

    [TestMethod]
    public void AVehicleWithoutAModeChannelHasNoDropdown()
    {
        var (services, _, _) = ParamTestSupport.CreateServices();
        var src = Copter();
        src.Values.Remove("FLTMODE_CH");
        var vm = new FlightModesViewModel(services, src);
        vm.OnShown();
        Assert.IsNull(vm.Channel);
    }
}
