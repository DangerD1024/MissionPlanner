using MissionPlanner.App.Core.Params;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Tests.SetupScreens;

/// <summary>A parameter store in memory: writes succeed unless the name is listed in <see cref="Reject"/>.</summary>
internal sealed class FakeParamSource : IParamSource
{
    public Dictionary<string, double> Values { get; } = new();

    public Dictionary<string, ParamMeta> Metas { get; } = new();

    public HashSet<string> Integers { get; } = new();

    public HashSet<string> Reject { get; } = new();

    public List<(string Name, double Value)> Writes { get; } = new();

    public bool Has(string name) => Values.ContainsKey(name);

    public double? Get(string name) => Values.TryGetValue(name, out var v) ? v : null;

    public ParamMeta? Meta(string name) => Metas.GetValueOrDefault(name);

    public bool IsInteger(string name) => Integers.Contains(name);

    public IReadOnlyList<string> Names() => Values.Keys.ToList();

    public Task<bool> SetAsync(string name, double value)
    {
        Writes.Add((name, value));
        if (Reject.Contains(name) || !Values.ContainsKey(name))
            return Task.FromResult(false);
        Values[name] = value;
        return Task.FromResult(true);
    }

    public static ParamMeta MetaWith(string name, double? min = null, double? max = null,
        IEnumerable<KeyValuePair<double, string>>? values = null, IEnumerable<KeyValuePair<int, string>>? bits = null,
        bool reboot = false) =>
        new(name, name + " label", "Docs for " + name, "V", min, max, null,
            values?.ToList() ?? [], bits?.ToList() ?? [], "Standard", reboot, false);
}

[TestClass]
public class HardwareSetupParamFieldTests
{
    private static readonly ParamFieldSpec NoPause = new() { Debounce = TimeSpan.Zero };

    [TestMethod]
    public void TakesLabelUnitsAndKindFromMetadata()
    {
        var src = new FakeParamSource();
        src.Values["BATT_LOW_VOLT"] = 10.5;
        src.Metas["BATT_LOW_VOLT"] = FakeParamSource.MetaWith("BATT_LOW_VOLT", 0, 120);

        var f = new ParamField(src, "BATT_LOW_VOLT");
        Assert.AreEqual("BATT_LOW_VOLT label", f.Label);
        Assert.AreEqual("V", f.Units);
        Assert.AreEqual(ParamEditorKind.Number, f.Kind);
        Assert.AreEqual(10.5m, f.NumberValue);
        Assert.AreEqual(0m, f.Minimum);
        Assert.AreEqual(120m, f.Maximum);
    }

    [TestMethod]
    public void RangeWidensToTheVehiclesValueSoTheEditorNeverClampsIt()
    {
        var src = new FakeParamSource();
        src.Values["MOT_PWM_MAX"] = 2500;
        var f = new ParamField(src, "MOT_PWM_MAX", new ParamFieldSpec { Min = 0, Max = 2200 });
        Assert.AreEqual(2500m, f.Maximum);
        Assert.AreEqual(2500m, f.NumberValue);
        Assert.IsEmpty(src.Writes);
    }

    [TestMethod]
    public void FloatNoiseIsHidden()
    {
        var src = new FakeParamSource();
        src.Values["MOT_SPIN_ARM"] = 0.1f;
        var f = new ParamField(src, "MOT_SPIN_ARM");
        Assert.AreEqual(0.1m, f.NumberValue);
    }

    [TestMethod]
    public async Task NumberEditWritesAfterThePause()
    {
        var src = new FakeParamSource();
        src.Values["BATT_CAPACITY"] = 3300;
        var f = new ParamField(src, "BATT_CAPACITY", new ParamFieldSpec { Debounce = TimeSpan.FromMilliseconds(50) });
        var written = new List<double>();
        f.Written += (_, e) => written.Add(e.NewValue);

        f.NumberValue = 4000;
        f.NumberValue = 5000;
        Assert.IsEmpty(src.Writes, "nothing is written while the user is still changing it");

        for (var i = 0; i < 100 && src.Writes.Count == 0; i++)
            await Task.Delay(20);
        await Task.Delay(100);

        Assert.HasCount(1, src.Writes);
        Assert.AreEqual(("BATT_CAPACITY", 5000.0), src.Writes[0]);
        CollectionAssert.AreEqual(new[] { 5000.0 }, written);
        Assert.AreEqual(5000.0, f.Value);
    }

    [TestMethod]
    public void RejectedWriteRevertsAndReports()
    {
        var src = new FakeParamSource();
        src.Values["FS_THR_VALUE"] = 975;
        src.Reject.Add("FS_THR_VALUE");
        var f = new ParamField(src, "FS_THR_VALUE", NoPause);
        string? failure = null;
        f.WriteFailed += (_, m) => failure = m;

        f.NumberValue = 900;

        Assert.IsNotNull(failure);
        Assert.IsTrue(f.HasError);
        Assert.AreEqual(975m, f.NumberValue, "the editor shows the vehicle's value again");
        Assert.AreEqual(975.0, f.Value);
    }

    [TestMethod]
    public void OptionsWriteImmediatelyAndShowUndocumentedValues()
    {
        var src = new FakeParamSource();
        src.Values["SERIAL1_PROTOCOL"] = 42;
        src.Metas["SERIAL1_PROTOCOL"] = FakeParamSource.MetaWith("SERIAL1_PROTOCOL",
            values: [new(1, "MAVLink1"), new(2, "MAVLink2"), new(5, "GPS")]);

        var f = new ParamField(src, "SERIAL1_PROTOCOL");
        Assert.AreEqual(ParamEditorKind.Options, f.Kind);
        Assert.HasCount(4, f.Options);
        Assert.AreEqual(42.0, f.SelectedOption?.Value);

        f.SelectedOption = f.Options.First(o => o.Value == 2);
        Assert.AreEqual(("SERIAL1_PROTOCOL", 2.0), src.Writes.Single());
        Assert.HasCount(3, f.Options, "the undocumented entry goes once the value is documented");
        Assert.AreEqual("MAVLink2", f.ValueText);
    }

    [TestMethod]
    public void ToggleWritesOnAndOffValues()
    {
        var src = new FakeParamSource();
        src.Values["SERVO3_REVERSED"] = 0;
        var f = new ParamField(src, "SERVO3_REVERSED", new ParamFieldSpec { Kind = ParamEditorKind.Toggle });
        Assert.IsFalse(f.IsOn);
        f.IsOn = true;
        Assert.AreEqual(1.0, src.Values["SERVO3_REVERSED"]);
        f.IsOn = false;
        Assert.AreEqual(0.0, src.Values["SERVO3_REVERSED"]);
    }

    [TestMethod]
    public void BitmaskTogglesEncodeTheValue()
    {
        var src = new FakeParamSource();
        src.Values["SERIAL2_OPTIONS"] = 1 + 1024; // bit 10 is not documented here
        src.Metas["SERIAL2_OPTIONS"] = FakeParamSource.MetaWith("SERIAL2_OPTIONS",
            bits: [new(0, "InvertRX"), new(1, "InvertTX"), new(2, "HalfDuplex")]);

        var f = new ParamField(src, "SERIAL2_OPTIONS", NoPause);
        Assert.AreEqual(ParamEditorKind.Bitmask, f.Kind);
        Assert.IsTrue(f.Bits[0].IsChecked);
        Assert.IsFalse(f.Bits[2].IsChecked);
        Assert.AreEqual("InvertRX", f.BitSummary);

        f.Bits[2].IsChecked = true;
        Assert.AreEqual(1 + 4 + 1024.0, src.Values["SERIAL2_OPTIONS"]);
        f.Bits[0].IsChecked = false;
        Assert.AreEqual(4 + 1024.0, src.Values["SERIAL2_OPTIONS"]);
        Assert.AreEqual("HalfDuplex", f.BitSummary);
    }

    [TestMethod]
    public async Task QueuedEditsWaitForCommit()
    {
        var src = new FakeParamSource();
        src.Values["FLTMODE1"] = 0;
        var modes = new List<ParamOption> { new(0, "Stabilize"), new(5, "Loiter"), new(6, "RTL") };
        var f = new ParamField(src, "FLTMODE1", new ParamFieldSpec { Kind = ParamEditorKind.Options, Options = modes, Mode = ParamWriteMode.Queued });

        f.SelectedOption = modes[1];
        Assert.IsTrue(f.IsDirty);
        Assert.AreEqual(5.0, f.PendingValue);
        Assert.IsEmpty(src.Writes);

        f.Refresh();
        Assert.AreEqual(5.0, f.SelectedOption?.Value, "a refresh does not undo a pending edit");

        Assert.IsTrue(await f.CommitAsync());
        Assert.AreEqual(("FLTMODE1", 5.0), src.Writes.Single());
        Assert.IsFalse(f.IsDirty);

        f.SelectedOption = modes[2];
        f.Revert();
        Assert.AreEqual(5.0, f.SelectedOption?.Value);
        Assert.IsFalse(f.IsDirty);
    }

    [TestMethod]
    public void LibraryRoundingNoiseIsRemoved()
    {
        // MAVLinkParam.Value turns 57 into 56.99999999999999 and 0.8f into 0.800000011920929
        Assert.AreEqual(57.0, ParamValues.Clean(56.99999999999999, isInteger: true));
        Assert.AreEqual(0.8, ParamValues.Clean(0.800000011920929, isInteger: false));
        Assert.AreEqual(12.02, ParamValues.Clean(12.019999504089355, isInteger: false));
        Assert.AreEqual(-1.0, ParamValues.Clean(-1.0000000000000002, isInteger: false));

        var src = new FakeParamSource();
        src.Values["SERIAL1_BAUD"] = 56.99999999999999;
        src.Integers.Add("SERIAL1_BAUD");
        src.Metas["SERIAL1_BAUD"] = FakeParamSource.MetaWith("SERIAL1_BAUD", values: [new(57, "57600"), new(115, "115200")]);
        var f = new ParamField(src, "SERIAL1_BAUD");
        Assert.AreEqual("57600", f.SelectedOption?.Label);
        Assert.HasCount(2, f.Options);
    }

    [TestMethod]
    public void RefreshPicksUpChangesFromElsewhere()
    {
        var src = new FakeParamSource();
        src.Values["SERVO1_TRIM"] = 1500;
        var f = new ParamField(src, "SERVO1_TRIM");
        src.Values["SERVO1_TRIM"] = 1520;
        f.Refresh();
        Assert.AreEqual(1520m, f.NumberValue);
        Assert.IsEmpty(src.Writes);
    }

    [TestMethod]
    public void ReadOnlyParametersAreNotWritten()
    {
        var src = new FakeParamSource();
        src.Values["STAT_BOOTCNT"] = 12;
        src.Metas["STAT_BOOTCNT"] = FakeParamSource.MetaWith("STAT_BOOTCNT") with { ReadOnly = true };
        var f = new ParamField(src, "STAT_BOOTCNT", NoPause);
        Assert.IsFalse(f.IsEditable);
        f.Edit(13);
        Assert.IsEmpty(src.Writes);
        Assert.IsTrue(f.HasError);
    }
}
