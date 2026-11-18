using MissionPlanner.App.Core.Params;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.ArduPilot;
using static MissionPlanner.App.Tests.ConfigScreens.ParamTestSupport;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>
/// The Basic and Extended Tuning pages over a parameter list (no link): which page each vehicle
/// gets, the copter sliders, the cards and their editors, the roll/pitch lock, large changes and
/// the 4.7 warning.
/// </summary>
[TestClass]
public class TuningPageTests
{
    private static (AppServices Services, FakeDialogs Dialogs) Vehicle(string fixture, Firmwares firmware)
    {
        var (s, dialogs, _) = CreateServices();
        TuningFixtures.Apply(s, fixture, firmware);
        return (s, dialogs);
    }

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
        ("ATC_RAT_YAW_P", 0.18), ("ATC_ANG_RLL_P", 4.5), ("ATC_ANG_PIT_P", 4.5), ("ATC_ACC_R_MAX", 1100), ("ATC_ACC_P_MAX", 1100),
        ("PSC_ACCZ_P", 0.5), ("PSC_ACCZ_I", 1), ("MOT_THST_HOVER", 0.35), ("ATC_INPUT_TC", 0.15),
        ("INS_HNTCH_ENABLE", 0), ("INS_LOG_BAT_MASK", 1),
    ];

    private static BasicTuningViewModel Basic(AppServices s, Func<string, ParamMeta?>? meta = null)
    {
        var page = new BasicTuningViewModel(s)
        {
            XmlPath = Path.Combine(TestEnvironment.DataRoot, "none.xml"),
            MetaSource = meta ?? (_ => null),
            WriteDelay = Timeout.InfiniteTimeSpan,
        };
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
    public void EachVehicleGetsMissionPlannersPages()
    {
        // copter: ConfigSimplePids sliders, then ConfigArducopter
        var (copter, _) = Vehicle(TuningFixtures.Copter, Firmwares.ArduCopter2);
        var basic = Basic(copter);
        Assert.IsTrue(basic.HasSliders);
        Assert.IsFalse(basic.HasCards);
        Assert.AreEqual("Basic Tuning", basic.Heading);
        CollectionAssert.AreEqual(new[] { "RC Feel Roll/Pitch", "Roll/Pitch Sensitivity", "Throttle Hover", "Climb Sensitivity" },
            basic.Sliders.Select(x => x.Title).ToArray());
        Assert.AreEqual(TuningSimplePids.Note, basic.InfoText);
        var ext = Extended(copter);
        Assert.AreEqual("Extended Tuning", ext.Heading);
        Assert.AreEqual("ArduCopter", ext.VehicleText);
        Assert.HasCount(17, ext.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Groups).ToList());

        // plane: ConfigArduplane as Basic Tuning, no Extended Tuning without QuadPlane
        var (plane, _) = Vehicle(TuningFixtures.Plane, Firmwares.ArduPlane);
        var planeBasic = Basic(plane);
        Assert.IsTrue(planeBasic.HasCards);
        Assert.IsFalse(planeBasic.HasSliders);
        Assert.AreEqual("Basic Tuning", planeBasic.Heading);
        CollectionAssert.AreEqual(new[] { "Servo Roll Pid", "Servo Pitch Pid", "Servo Yaw" },
            planeBasic.Rows[0].Cells.SelectMany(c => c.Groups).Select(g => g.Title).ToArray());
        Assert.IsFalse(planeBasic.CanLockRollPitch);
        var planeExt = Extended(plane);
        Assert.IsTrue(planeExt.IsEmpty);
        StringAssert.Contains(planeExt.EmptyText, "Q_ENABLE");

        // QuadPlane: "QP Extended Tuning" with the Q_ parameters
        var (qp, _) = Vehicle(TuningFixtures.QuadPlane, Firmwares.ArduPlane);
        var qpExt = Extended(qp);
        Assert.AreEqual("QP Extended Tuning", qpExt.Heading);
        Assert.AreEqual("ArduPlane QuadPlane", qpExt.VehicleText);
        Assert.IsNotNull(qpExt.Item("Q_A_RAT_RLL_P"));
        Assert.IsTrue(qpExt.LockRollPitch);
        Assert.IsTrue(Basic(qp).HasCards);

        // nothing to tune without parameters
        var (empty, _, _) = CreateServices();
        empty.Session.Cs.firmware = Firmwares.ArduCopter2;
        var none = Extended(empty);
        Assert.IsTrue(none.IsEmpty);
        StringAssert.Contains(none.EmptyText, "Connect to a vehicle");
        Assert.IsTrue(Basic(empty).IsEmpty);
    }

    [TestMethod]
    public void CardsKeepMissionPlannersArrangement()
    {
        var (s, _) = Vehicle(TuningFixtures.Copter, Firmwares.ArduCopter2);
        var page = Extended(s);

        CollectionAssert.AreEqual(new[] { 4, 4, 4, 1, 1 }, page.Rows.Select(r => r.Cells.Count).ToArray());
        CollectionAssert.AreEqual(new[] { false, true, false, false, false }, page.Rows.Select(r => r.LockAbove).ToArray());
        Assert.AreEqual(TuningLayouts.Copter.CardWidth, page.Rows[0].Cells[0].Width);
        Assert.AreEqual(2 * TuningLayouts.Copter.CardWidth + TuningPageViewModel.Gap, page.Rows[3].Cells[0].Width, "Filter Logs spans two columns");

        // Velocity XY with Basic Filters under it, as in the WinForms designer
        CollectionAssert.AreEqual(new[] { "Velocity XY (Vel to Accel)", "Basic Filters" },
            page.Rows[1].Cells[3].Groups.Select(g => g.Title).ToArray());
        var logs = page.Rows[3].Cells[0].Groups.Single();
        CollectionAssert.AreEqual(new[] { 1, 1 }, logs.Columns.Select(c => c.Items.Count).ToArray(), "Mask | Options side by side");
        Assert.AreEqual(TuningLayouts.Copter.LabelWidth, page.Item("ATC_RAT_RLL_P")!.LabelWidth);
    }

    [TestMethod]
    public void TwoColumnCardsFillTopToBottom()
    {
        var items = Enumerable.Range(0, 8)
            .Select(i => new TuningParamItem("v" + i, new FriendlyParamItem("P" + i, Meta("P" + i), 0, (_, _) => { }), "", 80))
            .ToList();
        var columns = TuningGroupItem.Split(items, 2);
        CollectionAssert.AreEqual(new[] { "v0", "v1", "v2", "v3" }, columns[0].Items.Select(i => i.Label).ToArray());
        CollectionAssert.AreEqual(new[] { "v4", "v5", "v6", "v7" }, columns[1].Items.Select(i => i.Label).ToArray());
        // a card with fewer values keeps its two columns (the second may be empty) so its boxes stay narrow
        CollectionAssert.AreEqual(new[] { 1, 0 }, TuningGroupItem.Split(items.Take(1).ToList(), 2).Select(c => c.Items.Count).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 1 }, TuningGroupItem.Split(items.Take(3).ToList(), 2).Select(c => c.Items.Count).ToArray());
        Assert.HasCount(8, TuningGroupItem.Split(items, 1).Single().Items);
    }

    [TestMethod]
    public void SlidersWaitToRestThenWriteWithTheirRelatedValues()
    {
        var (s, _) = Copter(CopterRates);
        var page = Basic(s);
        CollectionAssert.AreEqual(new[] { "ATC_INPUT_TC", "ATC_RAT_RLL_P", "MOT_THST_HOVER", "PSC_ACCZ_P" },
            page.Sliders.Select(i => i.Name).ToArray());

        var rate = page.Slider("ATC_RAT_RLL_P")!;
        CollectionAssert.AreEqual(new[] { "ATC_RAT_RLL_I", "ATC_RAT_PIT_P", "ATC_RAT_PIT_I" }, rate.Links.Select(l => l.Name).ToArray());
        StringAssert.Contains(rate.Tip, "Also sets ATC_RAT_RLL_I, ATC_RAT_PIT_P, ATC_RAT_PIT_I");
        Assert.AreEqual("PSC_ACCZ_I (×2)", page.Slider("PSC_ACCZ_P")!.Tip.Split("Also sets ")[1]);

        // a move is held until the slider rests; the tuning pages' Write Params queue is not used
        rate.BoxValue = 0.18m;
        Assert.IsTrue(rate.IsModified);
        Assert.IsTrue(page.IsWriting);
        Assert.AreEqual(0, page.PendingCount);

        // back where it started: nothing to send
        rate.BoxValue = 0.135m;
        Assert.IsFalse(rate.IsModified);
        Assert.IsFalse(page.IsWriting);
        page.Slider("MOT_THST_HOVER")!.BoxValue = 0.35m;
        Assert.IsFalse(page.Slider("MOT_THST_HOVER")!.IsModified);
    }

    [TestMethod]
    public async Task SlidersWriteNothingWhenNotConnected()
    {
        var (s, _) = Copter(CopterRates);
        var page = Basic(s);
        var climb = page.Slider("PSC_ACCZ_P")!;
        climb.Position = climb.ToPosition(0.6);
        Assert.AreEqual(0.6, climb.PendingValue!.Value, 1e-9);

        await page.FlushAsync();
        Assert.AreEqual("Not connected: nothing was written.", page.InfoText);
        Assert.IsFalse(climb.IsModified);
        Assert.IsFalse(page.IsWriting);
        Assert.AreEqual(0.5m, climb.BoxValue);
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

        var rate = page.Slider("ATC_RAT_RLL_P")!;
        Assert.AreEqual(0.01, rate.Minimum);
        Assert.AreEqual(0.5, rate.Maximum);
        Assert.AreEqual(0.005, rate.Increment);

        // a slider position snaps to the documented step
        rate.Position = rate.ToPosition(0.2013);
        Assert.AreEqual(0.2, rate.PendingValue!.Value, 1e-9);

        // the xml range applies without documentation; the current value is never cut off
        var (s2, _) = Copter(("ATC_RAT_RLL_P", 0.5));
        var wide = Basic(s2).Slider("ATC_RAT_RLL_P")!;
        Assert.AreEqual(0.08, wide.Minimum);
        Assert.AreEqual(0.5, wide.Maximum);

        var hover = page.Slider("MOT_THST_HOVER")!;
        Assert.AreEqual(0.2, hover.Minimum);
        Assert.AreEqual(0.001, hover.Increment);

        // the input time constant runs backwards: right is crisper (smaller)
        var tc = page.Slider("ATC_INPUT_TC")!;
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
        Assert.IsTrue(page.Rows.Single(r => r.LockAbove).Cells.SelectMany(c => c.Groups).Any(g => g.Title == "Rate Roll"));

        page.Item("ATC_RAT_RLL_P")!.Editor.NumericValue = 0.15m;
        Assert.IsTrue(page.Edits.TryGet("ATC_RAT_PIT_P", out var pitch));
        Assert.AreEqual(0.15, pitch, 1e-9);
        Assert.IsTrue(page.Item("ATC_RAT_PIT_P")!.Editor.IsModified);
        Assert.AreEqual(0.15m, page.Item("ATC_RAT_PIT_P")!.Editor.NumericValue);

        // and back, from pitch to roll
        page.Item("ATC_ANG_PIT_P")!.Editor.NumericValue = 6m;
        Assert.IsTrue(page.Edits.TryGet("ATC_ANG_RLL_P", out var roll));
        Assert.AreEqual(6, roll);

        // yaw has no twin; nor has the stabilize accel limit (Mission Planner pairs the P only)
        page.Item("ATC_RAT_YAW_P")!.Editor.NumericValue = 0.2m;
        page.Item("ATC_ACC_R_MAX")!.Editor.NumericValue = 1000m;
        Assert.AreEqual(6, page.PendingCount);
        Assert.AreEqual("6 unsaved changes", page.PendingText);

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
    public void CardsUseEditorsByKind()
    {
        var (s, _) = Copter(CopterRates);
        var meta = new Dictionary<string, ParamMeta>
        {
            ["INS_HNTCH_ENABLE"] = Meta("INS_HNTCH_ENABLE", "Harmonic Notch Filter enable", values: [new(0, "Disabled"), new(1, "Enabled")]),
            ["INS_LOG_BAT_MASK"] = Meta("INS_LOG_BAT_MASK", "Sensor Bitmask", bits: [new(0, "IMU1"), new(1, "IMU2")]),
            ["ATC_RAT_RLL_P"] = Meta("ATC_RAT_RLL_P", "Roll axis rate controller P gain", units: "", min: 0.01, max: 0.5, increment: 0.005),
            ["ATC_RAT_RLL_D"] = Meta("ATC_RAT_RLL_D", "Roll axis rate controller D gain", min: 0, max: 0.05),
            ["ATC_ACC_R_MAX"] = Meta("ATC_ACC_R_MAX", "Acceleration Max for Roll", units: "deg/s/s", min: 0, max: 1800),
        };
        var page = Extended(s, meta.GetValueOrDefault);

        Assert.IsTrue(page.Item("INS_HNTCH_ENABLE")!.Editor.IsOptions);
        Assert.IsTrue(page.Item("INS_LOG_BAT_MASK")!.Editor.IsBitmask);
        Assert.AreEqual(0.005m, page.Item("ATC_RAT_RLL_P")!.Editor.Increment);
        Assert.AreEqual(0.0001m, page.Item("ATC_RAT_RLL_D")!.Editor.Increment, "Mission Planner's step when none is documented");
        Assert.AreEqual(0.001m, page.Item("ATC_RAT_PIT_P")!.Editor.Increment);
        StringAssert.Contains(page.Item("ATC_RAT_RLL_P")!.Tip, "Range 0.01 to 0.5");
        Assert.AreEqual("deg/s/s", page.Item("ATC_ACC_R_MAX")!.UnitsText);
        Assert.AreEqual("", page.Item("ATC_RAT_RLL_P")!.UnitsText);
        Assert.AreEqual(page.AllItems.Count, page.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Groups).Sum(g => g.Items.Count()));
        Assert.IsFalse(page.IsEmpty);
    }

    [TestMethod]
    public void LabelsWithUnitsHideTheUnitsColumn()
    {
        var (s, _) = Vehicle(TuningFixtures.Plane, Firmwares.ArduPlane);
        var meta = new Dictionary<string, ParamMeta>
        {
            ["TECS_CLMB_MAX"] = Meta("TECS_CLMB_MAX", "Maximum Climb Rate (metres/sec)", units: "m/s", min: 0.1, max: 20),
            ["NAVL1_PERIOD"] = Meta("NAVL1_PERIOD", "L1 control period", units: "s", min: 1, max: 60),
        };
        var page = Basic(s, meta.GetValueOrDefault);
        Assert.AreEqual("Climb Max (m/s)", page.Item("TECS_CLMB_MAX")!.Label);
        Assert.AreEqual("", page.Item("TECS_CLMB_MAX")!.UnitsText);
        Assert.AreEqual("s", page.Item("NAVL1_PERIOD")!.UnitsText);
        Assert.AreEqual(TuningLayouts.Plane.LabelWidth, page.Item("NAVL1_PERIOD")!.LabelWidth);
    }

    [TestMethod]
    public void TheBoxBeingEditedShowsItsCopter47Change()
    {
        var (s, _) = Vehicle(TuningFixtures.Copter, Firmwares.ArduCopter2);
        var page = Extended(s);
        Assert.IsFalse(page.HasUnitWarning);

        page.FocusedItem = page.Item("PSC_D_ACC_P");
        Assert.AreEqual("Change in 4.7: PSC_ACCZ_P → PSC_D_ACC_P: value is now 10x smaller", page.UnitWarning);
        Assert.IsTrue(page.UnitWarningIsScale);
        StringAssert.Contains(page.Item("PSC_D_ACC_P")!.Tip, "10x smaller");

        page.FocusedItem = page.Item("PSC_NE_VEL_P");
        StringAssert.Contains(page.UnitWarning, "name only");
        Assert.IsFalse(page.UnitWarningIsScale);

        page.FocusedItem = page.Item("ATC_RAT_RLL_P");
        Assert.IsFalse(page.HasUnitWarning);
        Assert.AreEqual("", TuningEditors.ChangeNote("ATC_RAT_RLL_P"));
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

        var (s, _) = Copter([.. CopterRates, ("INS_HNTCH_MODE", 1)]);
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
    }

    [TestMethod]
    public void RealDocumentationGivesListsForOptionsAndBitmasks()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "ParameterMetaDataBackup.xml");
        var docs = ParamMetadata.ParseLegacy(file, Firmwares.ArduCopter2);
        var (s, _) = Vehicle(TuningFixtures.Copter, Firmwares.ArduCopter2);
        var page = Extended(s, docs.GetValueOrDefault);

        var rc6 = page.Item("RC6_OPTION")!.Editor;
        Assert.IsTrue(rc6.IsOptions);
        Assert.IsTrue(rc6.Options.Count > 20, "RC option functions");
        Assert.IsTrue(page.Item("INS_LOG_BAT_MASK")!.Editor.IsBitmask);
        Assert.IsTrue(page.Item("INS_HNTCH_ENABLE")!.Editor.IsOptions);
        Assert.IsTrue(page.Item("TUNE")!.Editor.IsOptions);
        Assert.IsTrue(page.Item("ATC_RAT_RLL_P")!.Editor.IsNumber);
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
        Assert.AreEqual(1, page.PendingCount);
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
}
