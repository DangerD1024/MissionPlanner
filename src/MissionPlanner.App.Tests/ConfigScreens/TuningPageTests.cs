using MissionPlanner.App.Core.Params;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.ArduPilot;
using static MissionPlanner.App.Tests.ConfigScreens.ParamTestSupport;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>The Basic and Extended Tuning pages over a parameter list (no link): slider links, the roll/pitch lock and large changes.</summary>
[TestClass]
public class TuningPageTests
{
    private static (AppServices Services, FakeDialogs Dialogs) Copter(params (string Name, double Value)[] values)
    {
        var (s, dialogs, _) = CreateServices();
        s.Session.Cs.firmware = Firmwares.ArduCopter2;
        AddParams(s, values.Select(v => (v.Name, v.Value, (double?)null)).ToArray());
        return (s, dialogs);
    }

    private static readonly (string, double)[] CopterRates =
    [
        ("ATC_RAT_RLL_P", 0.135), ("ATC_RAT_RLL_I", 0.135), ("ATC_RAT_RLL_D", 0.0036), ("ATC_RAT_RLL_IMAX", 0.5),
        ("ATC_RAT_PIT_P", 0.135), ("ATC_RAT_PIT_I", 0.135), ("ATC_RAT_PIT_D", 0.0036), ("ATC_RAT_PIT_IMAX", 0.5),
        ("ATC_RAT_YAW_P", 0.18), ("ATC_ANG_RLL_P", 4.5), ("ATC_ANG_PIT_P", 4.5),
        ("PSC_ACCZ_P", 0.5), ("PSC_ACCZ_I", 1), ("MOT_THST_HOVER", 0.35), ("ATC_INPUT_TC", 0.15),
        ("INS_HNTCH_ENABLE", 0), ("INS_LOG_BAT_MASK", 1),
    ];

    private static BasicTuningViewModel Basic(AppServices s, Func<string, ParamMeta?>? meta = null)
    {
        var page = new BasicTuningViewModel(s) { XmlPath = Path.Combine(TestEnvironment.DataRoot, "none.xml") };
        page.MetaSource = meta ?? (_ => null);
        page.Rebuild();
        return page;
    }

    private static ExtendedTuningViewModel Extended(AppServices s, Func<string, ParamMeta?>? meta = null)
    {
        var page = new ExtendedTuningViewModel(s) { MetaSource = meta ?? (_ => null) };
        page.Rebuild();
        return page;
    }

    [TestMethod]
    public void RollPitchSensitivityQueuesItsLinkedValues()
    {
        var (s, _) = Copter(CopterRates);
        var page = Basic(s);
        CollectionAssert.AreEqual(new[] { "ATC_INPUT_TC", "ATC_RAT_RLL_P", "MOT_THST_HOVER", "PSC_ACCZ_P" },
            page.Sections.SelectMany(x => x.Items).Select(i => i.Name).ToArray());

        var rate = page.Item("ATC_RAT_RLL_P")!;
        CollectionAssert.AreEqual(new[] { "ATC_RAT_RLL_I", "ATC_RAT_PIT_P", "ATC_RAT_PIT_I" }, rate.Links.Select(l => l.Name).ToArray());

        rate.BoxValue = 0.18m;
        Assert.IsTrue(rate.IsModified);
        Assert.AreEqual(4, page.PendingCount);
        foreach (var name in new[] { "ATC_RAT_RLL_P", "ATC_RAT_RLL_I", "ATC_RAT_PIT_P", "ATC_RAT_PIT_I" })
        {
            Assert.IsTrue(page.Edits.TryGet(name, out var v), name);
            Assert.AreEqual(0.18, v, 1e-9, name);
        }

        Assert.IsTrue(rate.Links.All(l => l.IsModified));
        Assert.AreEqual("0.135 → 0.18", rate.Links[0].ValueText);

        // back to the vehicle's value: nothing is left to write
        rate.BoxValue = 0.135m;
        Assert.IsFalse(rate.IsModified);
        Assert.AreEqual(0, page.PendingCount);
        Assert.IsFalse(rate.Links.Any(l => l.IsModified));
        Assert.AreEqual("0.135", rate.Links[0].ValueText);
    }

    [TestMethod]
    public void ClimbSensitivitySetsITwiceP()
    {
        var (s, _) = Copter(CopterRates);
        var page = Basic(s);
        var climb = page.Item("PSC_ACCZ_P")!;
        Assert.AreEqual("×2", climb.Links.Single().MultiplierText);

        climb.Position = climb.ToPosition(0.6);
        Assert.IsTrue(page.Edits.TryGet("PSC_ACCZ_P", out var p));
        Assert.IsTrue(page.Edits.TryGet("PSC_ACCZ_I", out var i));
        Assert.AreEqual(0.6, p, 1e-9);
        Assert.AreEqual(1.2, i, 1e-9);
        Assert.AreEqual(0.6m, climb.BoxValue);
    }

    [TestMethod]
    public void SlidersUseTheDocumentedRangeAndStep()
    {
        var (s, _) = Copter(CopterRates);
        var meta = new Dictionary<string, ParamMeta>
        {
            ["ATC_RAT_RLL_P"] = Meta("ATC_RAT_RLL_P", "Roll axis rate controller P gain", min: 0.01, max: 0.5, increment: 0.005),
            ["MOT_THST_HOVER"] = Meta("MOT_THST_HOVER", "Thrust Hover Value", min: 0.2, max: 0.8),
        };
        var page = Basic(s, meta.GetValueOrDefault);

        var rate = page.Item("ATC_RAT_RLL_P")!;
        Assert.AreEqual(0.01, rate.Minimum);
        Assert.AreEqual(0.5, rate.Maximum);
        Assert.AreEqual(0.005, rate.Increment);

        // a slider position snaps to the documented step
        rate.Position = rate.ToPosition(0.2013);
        Assert.IsTrue(page.Edits.TryGet("ATC_RAT_RLL_P", out var v));
        Assert.AreEqual(0.2, v, 1e-9);

        // the xml range applies without documentation; the current value is never cut off
        var (s2, _) = Copter(("ATC_RAT_RLL_P", 0.5));
        var wide = Basic(s2).Item("ATC_RAT_RLL_P")!;
        Assert.AreEqual(0.08, wide.Minimum);
        Assert.AreEqual(0.5, wide.Maximum);

        var hover = page.Item("MOT_THST_HOVER")!;
        Assert.AreEqual(0.2, hover.Minimum);
        Assert.AreEqual(0.001, hover.Increment);

        // the input time constant runs backwards: right is crisper (smaller)
        var tc = page.Item("ATC_INPUT_TC")!;
        Assert.IsTrue(tc.Reversed);
        Assert.AreEqual("1", tc.LeftText);
        Assert.AreEqual("0", tc.RightText);
    }

    [TestMethod]
    public void LockedRollEditsCopyToPitch()
    {
        var (s, _) = Copter(CopterRates);
        var page = Extended(s);
        Assert.IsTrue(page.CanLockRollPitch);
        Assert.IsTrue(page.LockRollPitch, "roll and pitch agree");

        page.Item("ATC_RAT_RLL_P")!.Editor.NumericValue = 0.15m;
        Assert.IsTrue(page.Edits.TryGet("ATC_RAT_PIT_P", out var pitch));
        Assert.AreEqual(0.15, pitch, 1e-9);
        Assert.IsTrue(page.Item("ATC_RAT_PIT_P")!.Editor.IsModified);
        Assert.AreEqual(0.15m, page.Item("ATC_RAT_PIT_P")!.Editor.NumericValue);

        // and back, from pitch to roll
        page.Item("ATC_ANG_PIT_P")!.Editor.NumericValue = 6m;
        Assert.IsTrue(page.Edits.TryGet("ATC_ANG_RLL_P", out var roll));
        Assert.AreEqual(6, roll);

        // yaw has no twin
        page.Item("ATC_RAT_YAW_P")!.Editor.NumericValue = 0.2m;
        Assert.AreEqual(5, page.PendingCount);

        // unlocked: only the edited value
        page.LockRollPitch = false;
        page.Item("ATC_RAT_RLL_I")!.Editor.NumericValue = 0.2m;
        Assert.IsTrue(page.Edits.IsModified("ATC_RAT_RLL_I"));
        Assert.IsFalse(page.Edits.IsModified("ATC_RAT_PIT_I"));
    }

    [TestMethod]
    public void LockStartsOffWhenRollAndPitchDifferOrOnAHeli()
    {
        var differ = CopterRates.Select(p => p.Item1 == "ATC_RAT_PIT_D" ? (p.Item1, 0.005) : p).ToArray();
        var (s, _) = Copter(differ);
        var page = Extended(s);
        Assert.IsTrue(page.CanLockRollPitch);
        Assert.IsFalse(page.LockRollPitch);
        page.Item("ATC_RAT_RLL_D")!.Editor.NumericValue = 0.004m;
        Assert.IsFalse(page.Edits.IsModified("ATC_RAT_PIT_D"));

        var (heli, _) = Copter([.. CopterRates, ("H_SWASH_TYPE", 0)]);
        Assert.IsFalse(Extended(heli).LockRollPitch);
    }

    [TestMethod]
    public void ExtendedPageShowsCardsWithEditorsByKind()
    {
        var (s, _) = Copter(CopterRates);
        var meta = new Dictionary<string, ParamMeta>
        {
            ["INS_HNTCH_ENABLE"] = Meta("INS_HNTCH_ENABLE", "Harmonic Notch Filter enable", values: [new(0, "Disabled"), new(1, "Enabled")]),
            ["INS_LOG_BAT_MASK"] = Meta("INS_LOG_BAT_MASK", "Sensor Bitmask", bits: [new(0, "IMU1"), new(1, "IMU2")]),
            ["ATC_RAT_RLL_P"] = Meta("ATC_RAT_RLL_P", "Roll axis rate controller P gain", units: "", min: 0.01, max: 0.5, increment: 0.005),
            ["ATC_RAT_RLL_D"] = Meta("ATC_RAT_RLL_D", "Roll axis rate controller D gain", min: 0, max: 0.05),
        };
        var page = Extended(s, meta.GetValueOrDefault);

        Assert.AreEqual("ArduCopter", page.VehicleText);
        CollectionAssert.AreEqual(new[] { TuningLayouts.RateSection, TuningLayouts.AttitudeSection, TuningLayouts.PositionSection, TuningLayouts.FilterSection },
            page.Sections.Select(x => x.Title).ToArray());
        CollectionAssert.AreEqual(new[] { "P", "I", "D", "IMAX" },
            page.Sections[0].Groups[0].Items.Select(i => i.Label).ToArray());
        Assert.IsTrue(page.Item("INS_HNTCH_ENABLE")!.Editor.IsOptions);
        Assert.IsTrue(page.Item("INS_LOG_BAT_MASK")!.Editor.IsBitmask);
        Assert.AreEqual(0.005m, page.Item("ATC_RAT_RLL_P")!.Editor.Increment);
        Assert.AreEqual(0.001m, page.Item("ATC_RAT_RLL_D")!.Editor.Increment, "a step is guessed when none is documented");
        Assert.AreEqual(0.001m, page.Item("ATC_RAT_PIT_P")!.Editor.Increment, "undocumented gains still get a fine step");
        StringAssert.Contains(page.Item("ATC_RAT_RLL_P")!.Tip, "Range 0.01 to 0.5");
        Assert.AreEqual(page.AllItems.Count, page.ShownCount);
        Assert.IsFalse(page.IsEmpty);
    }

    [TestMethod]
    public void SuggestedValuesStayNumbersAndEnumerationsBecomeLists()
    {
        // ATC_INPUT_TC: 0.5 Very Soft ... 0.05 Very Crisp
        var inputTc = Meta("ATC_INPUT_TC", "Attitude control input time constant", min: 0, max: 1,
            values: [new(0.5, "Very Soft"), new(0.15, "Medium"), new(0.05, "Very Crisp")]);
        // ATC_ACC_R_MAX: a range of 0-1800 with five named points
        var accel = Meta("ATC_ACC_R_MAX", "Acceleration Max for Roll", min: 0, max: 1800, increment: 10,
            values: [new(0, "Disabled"), new(300, "VerySlow"), new(720, "Slow"), new(1080, "Medium"), new(1620, "Fast")]);
        // INS_HNTCH_MODE: range 0-5 and a name for each
        var mode = Meta("INS_HNTCH_MODE", "Harmonic Notch Filter mode", min: 0, max: 5,
            values: Enumerable.Range(0, 6).Select(i => new KeyValuePair<double, string>(i, "m" + i)).ToList());
        var enable = Meta("INS_HNTCH_ENABLE", "Enable", values: [new(0, "Disabled"), new(1, "Enabled")]);

        Assert.IsTrue(TuningEditors.IsSuggestionList(inputTc));
        Assert.IsTrue(TuningEditors.IsSuggestionList(accel));
        Assert.IsFalse(TuningEditors.IsSuggestionList(mode));
        Assert.IsFalse(TuningEditors.IsSuggestionList(enable));

        var (s, _) = Copter([.. CopterRates, ("ATC_ACC_R_MAX", 1100), ("INS_HNTCH_MODE", 1)]);
        var meta = new Dictionary<string, ParamMeta>
        {
            ["ATC_INPUT_TC"] = inputTc, ["ATC_ACC_R_MAX"] = accel, ["INS_HNTCH_MODE"] = mode, ["INS_HNTCH_ENABLE"] = enable,
        };
        var page = Extended(s, meta.GetValueOrDefault);
        Assert.IsTrue(page.Item("ATC_INPUT_TC")!.Editor.IsNumber);
        Assert.IsTrue(page.Item("ATC_ACC_R_MAX")!.Editor.IsNumber, "1100 is not one of the named points but is valid");
        Assert.AreEqual(10m, page.Item("ATC_ACC_R_MAX")!.Editor.Increment);
        Assert.IsTrue(page.Item("INS_HNTCH_MODE")!.Editor.IsOptions);
        Assert.IsTrue(page.Item("INS_HNTCH_ENABLE")!.Editor.IsOptions);
        StringAssert.Contains(page.Item("ATC_INPUT_TC")!.Tip, "Suggested: 0.5 Very Soft, 0.15 Medium, 0.05 Very Crisp");

        // the large-change question skips lists, not gains with suggested values
        Assert.IsFalse(page.IsEnumerated("ATC_ACC_R_MAX"));
        Assert.IsFalse(page.IsEnumerated("ATC_INPUT_TC"));
        Assert.IsTrue(page.IsEnumerated("INS_HNTCH_MODE"));
        Assert.IsFalse(page.IsEnumerated("ATC_RAT_RLL_P"), "undocumented");

        // the 4.7 badge is for parameters whose units or scale changed, not plain renames
        StringAssert.Contains(TuningEditors.ChangeNote("PSC_D_ACC_P"), "10x smaller");
        Assert.AreEqual("", TuningEditors.ChangeNote("PSC_NE_VEL_P"));
        Assert.AreEqual("", TuningEditors.ChangeNote("ATC_RAT_RLL_P"));
        Assert.IsTrue(page.Item("ATC_ACC_R_MAX")!.HasChangeNote);
    }

    [TestMethod]
    public void RebuildKeepsEditsAndDropsTheOnesNowWritten()
    {
        var (s, _) = Copter(CopterRates);
        var page = Extended(s);
        page.LockRollPitch = false;
        page.Item("ATC_RAT_YAW_P")!.Editor.NumericValue = 0.25m;
        page.Item("ATC_RAT_RLL_P")!.Editor.NumericValue = 0.14m;

        // the vehicle now has the yaw value (written elsewhere)
        s.Session.Vehicle.param["ATC_RAT_YAW_P"] = new MAVLink.MAVLinkParam("ATC_RAT_YAW_P", 0.25, MAVLink.MAV_PARAM_TYPE.REAL32);
        page.Rebuild();
        Assert.IsFalse(page.Edits.IsModified("ATC_RAT_YAW_P"));
        Assert.IsTrue(page.Item("ATC_RAT_RLL_P")!.Editor.IsModified);
        Assert.AreEqual(0.14m, page.Item("ATC_RAT_RLL_P")!.Editor.NumericValue);

        page.DiscardEditsCommand.Execute(null);
        Assert.AreEqual(0, page.PendingCount);
        Assert.IsFalse(page.Item("ATC_RAT_RLL_P")!.Editor.IsModified);
        Assert.AreEqual(0.135m, page.Item("ATC_RAT_RLL_P")!.Editor.NumericValue);
    }

    [TestMethod]
    public void LargeChangesFollowMissionPlannersRule()
    {
        var current = new Dictionary<string, double>
        {
            ["A"] = 0.1, ["B"] = 0.1, ["C"] = 0, ["D"] = -1, ["E"] = 0, ["F"] = 0.135,
        };
        var pending = new Dictionary<string, double>
        {
            ["A"] = 0.3, // tripled
            ["B"] = 0.2, // exactly doubled: not "more than"
            ["C"] = 5, // from zero
            ["D"] = -3, // more negative: not a large gain
            ["E"] = 1, // an enumeration (skipped)
            ["F"] = 0.27000001, // double at float precision
        };

        var large = TuningWriteFlow.LargeChanges(pending, n => current.TryGetValue(n, out var v) ? v : null, n => n == "E");
        CollectionAssert.AreEqual(new[] { "A", "C" }, large.Select(l => l.Name).ToArray());
        Assert.AreEqual(new TuningLargeChange("A", 0.1, 0.3), large[0]);

        StringAssert.Contains(TuningWriteFlow.LargeChangeText([large[0]]), "A has more than doubled the last input (0.1 -> 0.3)");
        StringAssert.Contains(TuningWriteFlow.LargeChangeText(large), "C: 0 -> 5");
    }

    [TestMethod]
    public async Task DecliningALargeChangePutsItBackAndWritesNothing()
    {
        var (s, dialogs) = Copter(CopterRates);
        var page = Extended(s);
        page.LockRollPitch = false;
        page.Item("ATC_RAT_RLL_P")!.Editor.NumericValue = 0.3m; // 0.135 -> 0.3
        page.Item("ATC_RAT_YAW_P")!.Editor.NumericValue = 0.2m;

        dialogs.ConfirmAnswer = false;
        var written = await TuningWriteFlow.WriteAsync(s, page.Edits);
        page.SyncValues();

        Assert.IsEmpty(written);
        Assert.AreEqual("Large Value", dialogs.Confirms.Single().Title);
        StringAssert.Contains(dialogs.Confirms.Single().Message, "ATC_RAT_RLL_P has more than doubled the last input (0.135 -> 0.3)");
        Assert.IsFalse(page.Edits.IsModified("ATC_RAT_RLL_P"));
        Assert.IsFalse(page.Item("ATC_RAT_RLL_P")!.Editor.IsModified);
        Assert.AreEqual(0.135m, page.Item("ATC_RAT_RLL_P")!.Editor.NumericValue);
        Assert.IsTrue(page.Edits.IsModified("ATC_RAT_YAW_P"), "other edits stay queued");

        // accepted: the write goes on to the usual flow (here: not connected)
        page.Item("ATC_RAT_RLL_P")!.Editor.NumericValue = 0.3m;
        dialogs.ConfirmAnswer = true;
        await TuningWriteFlow.WriteAsync(s, page.Edits);
        Assert.AreEqual("Not connected", dialogs.Messages.Last().Title);
        Assert.IsTrue(page.Edits.IsModified("ATC_RAT_RLL_P"));
    }

    [TestMethod]
    public void PlaneAndRoverPagesUseTheirOwnLayouts()
    {
        var (s, _, _) = CreateServices();
        s.Session.Cs.firmware = Firmwares.ArduPlane;
        AddParams(s, ("RLL_RATE_P", 0.08, null), ("RLL_RATE_FF", 0.35, null), ("Q_ENABLE", 1, null), ("Q_A_RAT_RLL_P", 0.25, null),
            ("Q_A_RAT_PIT_P", 0.25, null), ("Q_A_RAT_RLL_I", 0.25, null), ("Q_A_RAT_PIT_I", 0.25, null));
        var plane = Extended(s);
        Assert.AreEqual("ArduPlane with QuadPlane", plane.VehicleText);
        Assert.IsTrue(plane.LockRollPitch, "QuadPlane rate controllers agree");
        plane.Item("Q_A_RAT_RLL_P")!.Editor.NumericValue = 0.3m;
        Assert.IsTrue(plane.Edits.IsModified("Q_A_RAT_PIT_P"));

        var basic = Basic(s);
        CollectionAssert.AreEqual(new[] { "RLL_RATE_FF", "RLL_RATE_P", "Q_A_RAT_RLL_P" }, basic.AllItems.Select(i => i.Name).ToArray());

        var (r, _, _) = CreateServices();
        r.Session.Cs.firmware = Firmwares.ArduRover;
        AddParams(r, ("ATC_STR_RAT_P", 0.2, null), ("CRUISE_SPEED", 5, null));
        var rover = Extended(r);
        Assert.IsFalse(rover.CanLockRollPitch);
        Assert.AreEqual(2, rover.ShownCount);
        Assert.AreEqual(2, Basic(r).ShownCount);

        var (empty, _, _) = CreateServices();
        empty.Session.Cs.firmware = Firmwares.ArduCopter2;
        var none = Extended(empty);
        Assert.IsTrue(none.IsEmpty);
        StringAssert.Contains(none.EmptyText, "Connect to a vehicle");
    }
}
