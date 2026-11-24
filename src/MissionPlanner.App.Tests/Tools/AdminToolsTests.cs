using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using MissionPlanner.App.Core.Localization;
using MissionPlanner.App.Core.Params;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.Mavlink;
using MissionPlanner.Utilities;
using MissionPlanner.Warnings;
using static MAVLink;

namespace MissionPlanner.App.Tests.Tools;

/// <summary>TOOLS: Warning Manager, Param gen, Lang Edit, signing, Proximity and Manage Command List.</summary>
[TestClass]
public class AdminToolsTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    [TestCleanup]
    public void Cleanup()
    {
        AppWarnings.Use([]);
        Settings.Instance.Remove(PlannerExtraCommands.CommandsKey);
        Settings.Instance.Remove(PlannerExtraCommands.IdsKey);
    }

    // ---- Warning Manager ----

    [TestMethod]
    public void WarningConditionsCompareAsMissionPlanner()
    {
        Assert.IsTrue(WarningLogic.Holds(CustomWarning.Conditional.LT, 1, 2));
        Assert.IsFalse(WarningLogic.Holds(CustomWarning.Conditional.LT, 2, 2));
        Assert.IsTrue(WarningLogic.Holds(CustomWarning.Conditional.LTEQ, 2, 2));
        Assert.IsTrue(WarningLogic.Holds(CustomWarning.Conditional.EQ, 2, 2));
        Assert.IsTrue(WarningLogic.Holds(CustomWarning.Conditional.GT, 3, 2));
        Assert.IsTrue(WarningLogic.Holds(CustomWarning.Conditional.GTEQ, 2, 2));
        Assert.IsTrue(WarningLogic.Holds(CustomWarning.Conditional.NEQ, 3, 2));
        Assert.IsFalse(WarningLogic.Holds(CustomWarning.Conditional.NONE, 3, 2));
    }

    [TestMethod]
    public void AWarningRepeatsOnlyAfterItsRepeatTime_AndChainedConditionsMustAllHold()
    {
        var values = new Dictionary<string, double> { ["battery_voltage"] = 10.2, ["armed"] = 0 };
        double? Read(string n) => values.TryGetValue(n, out var v) ? v : null;
        var rule = new WarningRule
        {
            Field = "battery_voltage", Condition = CustomWarning.Conditional.LT, Value = 10.5, RepeatSeconds = 10,
            Text = "{name} low: {value} below {warning}",
            Child = new WarningRule { Field = "armed", Condition = CustomWarning.Conditional.EQ, Value = 1 },
        };

        Assert.IsEmpty(WarningLogic.Check([rule], Read, T0).Spoken, "not armed: the chained condition fails");
        values["armed"] = 1;
        var first = WarningLogic.Check([rule], Read, T0.AddSeconds(1)).Spoken;
        Assert.HasCount(1, first);
        Assert.AreEqual($"battery_voltage low: {10.2.ToString("0.##")} below {10.5.ToString("0.##")}", first[0]);
        Assert.IsEmpty(WarningLogic.Check([rule], Read, T0.AddSeconds(5)).Spoken, "within the repeat time");
        Assert.HasCount(1, WarningLogic.Check([rule], Read, T0.AddSeconds(11)).Spoken, "repeat time passed");
        values["battery_voltage"] = 12;
        Assert.IsEmpty(WarningLogic.Check([rule], Read, T0.AddSeconds(30)).Spoken, "condition no longer holds");
        Assert.IsEmpty(WarningLogic.Check([new WarningRule { Field = "nosuchfield", Condition = CustomWarning.Conditional.NEQ }], Read, T0).Spoken);
    }

    [TestMethod]
    public void ColouringRulesColourTheirFieldWhileTheyHold()
    {
        var rule = new WarningRule { Field = "satcount", Condition = CustomWarning.Conditional.LT, Value = 6, IsColoring = true };
        Assert.AreEqual("Red", rule.Color);
        Assert.AreEqual(0, rule.RepeatSeconds);
        var check = WarningLogic.Check([rule], _ => 4, T0);
        Assert.IsEmpty(check.Spoken);
        Assert.AreEqual("Red", check.Colors["satcount"]);
        Assert.AreEqual("NoColor", WarningLogic.Check([rule], _ => 9, T0).Colors["satcount"]);
        Assert.IsNull(WarningLogic.TileColor("NoColor"));
    }

    [TestMethod]
    public void AppWarningsSpeakFromTheVehicleState()
    {
        var cs = new CurrentState { satcount = 4 };
        var speech = new RecordingSpeech();
        AppWarnings.Use([
            new WarningRule { Field = "satcount", Condition = CustomWarning.Conditional.LT, Value = 6, Text = "{value} satellites" },
            new WarningRule { Field = "satcount", Condition = CustomWarning.Conditional.LT, Value = 6, IsColoring = true, Color = "Yellow" },
        ]);
        var said = AppWarnings.Step(cs, speech, T0);
        Assert.AreEqual("4 satellites", said);
        CollectionAssert.AreEqual(new[] { "4 satellites" }, speech.Said);
        Assert.AreEqual("Yellow", AppWarnings.ColorFor("satcount"));
        AppWarnings.Clear();
        Assert.IsNull(AppWarnings.ColorFor("satcount"));
    }

    [TestMethod]
    public void WarningsFileIsMissionPlannersWarningsXml()
    {
        // written by the WinForms app's own class and serializer
        var old = new List<CustomWarning>
        {
            new()
            {
                Name = "alt", Warning = 100, ConditionType = CustomWarning.Conditional.GT, RepeatTime = 15, Text = "Too high {value}",
                Child = new CustomWarning { Name = "armed", Warning = 1, ConditionType = CustomWarning.Conditional.EQ },
            },
            new() { Name = "satcount", Warning = 6, ConditionType = CustomWarning.Conditional.LT, type = CustomWarning.WarningType.Coloring, color = "Gold" },
        };
        var serializer = new XmlSerializer(typeof(List<CustomWarning>), [typeof(CustomWarning)]);
        var ms = new MemoryStream();
        serializer.Serialize(ms, old);
        ms.Position = 0;

        var rules = WarningStore.Read(ms);
        Assert.HasCount(2, rules);
        Assert.AreEqual("alt", rules[0].Field);
        Assert.AreEqual(CustomWarning.Conditional.GT, rules[0].Condition);
        Assert.AreEqual(100, rules[0].Value);
        Assert.AreEqual(15, rules[0].RepeatSeconds);
        Assert.AreEqual("Too high {value}", rules[0].Text);
        Assert.AreEqual("armed", rules[0].Child!.Field);
        Assert.IsTrue(rules[1].IsColoring);
        Assert.AreEqual("Gold", rules[1].Color);

        // and the WinForms app reads what this app writes
        var path = Path.Combine(TestEnvironment.DataRoot, "warnings-test.xml");
        WarningStore.Save(path, rules);
        using var file = File.OpenRead(path);
        var back = (List<CustomWarning>)serializer.Deserialize(file)!;
        Assert.HasCount(2, back);
        Assert.AreEqual("alt", back[0].Name);
        Assert.AreEqual(15, back[0].RepeatTime);
        Assert.AreEqual("armed", back[0].Child.Name);
        Assert.AreEqual(CustomWarning.WarningType.Coloring, back[1].type);
    }

    [TestMethod]
    public void WarningManagerChainsAndRemovesConditions()
    {
        var vm = new WarningManagerViewModel(FakeUi.Services(out _));
        vm.AddCommand.Execute(null);
        Assert.HasCount(1, vm.Rows);
        vm.AddConditionCommand.Execute(vm.Rows[0]);
        Assert.HasCount(2, vm.Rows);
        Assert.IsTrue(vm.Rows[1].IsChild);
        Assert.AreSame(vm.Rows[1].Rule, vm.Rows[0].Rule.Child);
        Assert.HasCount(1, AppWarnings.Rules, "edits are checked at once");
        vm.RemoveCommand.Execute(vm.Rows[1]);
        Assert.HasCount(1, vm.Rows);
        Assert.IsNull(vm.Rows[0].Rule.Child);
        vm.RemoveCommand.Execute(vm.Rows[0]);
        Assert.IsEmpty(vm.Rows);
        Assert.IsTrue(vm.IsEmpty);
    }

    // ---- signing ----

    [TestMethod]
    public void SigningKeyIsTheSha256OfThePassphrase_AsMissionPlannerMakesIt()
    {
        CollectionAssert.AreEqual(Convert.FromHexString("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"), SigningKeys.FromPassphrase("abc"));

        var saved = MAVAuthKeys.Keys;
        try
        {
            MAVAuthKeys.AddKey("test-key", "correct horse battery staple ü");
            CollectionAssert.AreEqual(MAVAuthKeys.Keys["test-key"].Key, SigningKeys.FromPassphrase("correct horse battery staple ü"));
            CollectionAssert.AreEqual(SHA256.HashData(Encoding.UTF8.GetBytes("correct horse battery staple ü")), MAVAuthKeys.Keys["test-key"].Key);
        }
        finally
        {
            MAVAuthKeys.Keys = saved;
        }
    }

    [TestMethod]
    public void KeyListIsSwappedNotChanged_AndTheKeyInUseIsFoundByValue()
    {
        var keys = new MAVAuthKeys.AuthKeys();
        var a = SigningKeys.With(keys, "a", SigningKeys.FromPassphrase("one"));
        Assert.IsEmpty(keys);
        Assert.HasCount(1, a);
        Assert.AreEqual("a", SigningKeys.NameOf(a, SigningKeys.FromPassphrase("one")));
        Assert.IsNull(SigningKeys.NameOf(a, SigningKeys.FromPassphrase("two")));
        Assert.IsNull(SigningKeys.NameOf(a, null));
        Assert.IsEmpty(SigningKeys.Without(a, "a"));
        Assert.HasCount(1, a);
    }

    [TestMethod]
    public void PassphraseStrengthIsTheAuthKeysScore()
    {
        Assert.AreEqual((24, "Weak"), SigningKeys.Strength("password"));
        Assert.AreEqual((126, "Strong"), SigningKeys.Strength("Tr0ub4dor&3xK!"));
        Assert.AreEqual("Good", SigningKeys.Strength("hellothere1").Verdict);
    }

    // ---- Proximity ----

    [TestMethod]
    public void ProximitySectorsSitOnTheSensorBearings()
    {
        Assert.AreEqual(0, ProximityGeometry.OrientationAngle(MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_NONE));
        Assert.AreEqual(135, ProximityGeometry.OrientationAngle(MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_135));
        Assert.AreEqual(315, ProximityGeometry.OrientationAngle(MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_315));
        Assert.IsNull(ProximityGeometry.OrientationAngle(MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_PITCH_270), "down");

        var state = new Proximity.directionState();
        state.Add(1, MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_90, 230, DateTime.Now, 10);
        state.Add(2, -10, 5, 400, DateTime.Now, 10);
        state.Add(3, MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_PITCH_270, 80, DateTime.Now, 10);
        var sectors = ProximityGeometry.Sectors(state.GetRaw());
        Assert.HasCount(2, sectors);
        var side = sectors.Single(s => !s.IsCustom);
        Assert.AreEqual(90, side.CenterDeg);
        Assert.AreEqual(67.5, side.StartDeg);
        Assert.AreEqual(112.5, side.EndDeg);
        var obstacle = sectors.Single(s => s.IsCustom);
        Assert.AreEqual(350, obstacle.CenterDeg);
        Assert.AreEqual(347.5, obstacle.StartDeg);
        Assert.AreEqual(230, ProximityGeometry.Closest(sectors));
    }

    [TestMethod]
    public void ProximityScaleFitsTheRangeAndPointsGoClockwiseFromTheNose()
    {
        // Mission Planner: ((screenradius + 50) * 2) / min(width, height)
        Assert.AreEqual(1100.0 / 300, ProximityGeometry.CmPerPixel(500, 400, 300), 1e-9);
        Assert.AreEqual(100, ProximityGeometry.RadiusPx(400, 500, 4), 1e-9);
        Assert.AreEqual(125, ProximityGeometry.RadiusPx(900, 500, 4), 1e-9, "held at the edge");

        var (x, y) = ProximityGeometry.Point(100, 100, 0, 10);
        Assert.AreEqual(100, x, 1e-9);
        Assert.AreEqual(90, y, 1e-9);
        (x, y) = ProximityGeometry.Point(100, 100, 90, 10);
        Assert.AreEqual(110, x, 1e-9);
        Assert.AreEqual(100, y, 1e-9);

        CollectionAssert.AreEqual(new double[] { 50, 100, 150, 200, 250, 300, 350, 400, 450, 500 }, ProximityGeometry.Rings(500).ToArray());
        Assert.HasCount(10, ProximityGeometry.Rings(5000));
        Assert.AreEqual(500, ProximityGeometry.Rings(5000)[0]);
        Assert.AreEqual(50, ProximityViewModel.RangeStep(500));
    }

    // ---- Lang Edit ----

    [TestMethod]
    public void TranslationEditorSavesUserOverridesThatWinOverTheBuiltInTable()
    {
        var root = Path.Combine(TestEnvironment.DataRoot, "loc-" + Guid.NewGuid().ToString("N"));
        var builtIn = Path.Combine(root, "builtin");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(builtIn);
        File.WriteAllText(Path.Combine(builtIn, "de-DE.json"),
            """{ "culture": "de-DE", "overrides": {}, "strings": { "Connect": "Verbinden", "Disconnect": "Trennen" } }""");
        var loc = new Localizer(new DirectoryTranslationSource(builtIn), new DirectoryTranslationSource(user));
        loc.SetLanguage("de-DE");
        Assert.AreEqual("Verbinden", loc.Tr("Connect"));

        var vm = new LanguageEditorViewModel(FakeUi.Services(out _), loc, user);
        Assert.AreEqual("de-DE", vm.SelectedLanguage!.Code);
        var connect = vm.AllRows.Single(r => r.English == "Connect");
        Assert.AreEqual("Verbinden", connect.BuiltIn);
        connect.Mine = "Anschließen";
        var disconnect = vm.AllRows.Single(r => r.English == "Disconnect");
        disconnect.Mine = "Trennen"; // the same as the built-in: not written
        vm.SaveCommand.Execute(null);

        var file = UserTranslations.PathFor("de-DE", user);
        var saved = UserTranslations.Read(file);
        Assert.HasCount(1, saved);
        Assert.AreEqual("Anschließen", saved["Connect"]);
        Assert.AreEqual("Anschließen", loc.Tr("Connect"), "applied on top at once");
        Assert.AreEqual("Trennen", loc.Tr("Disconnect"));

        // a new editor shows it as the user's own
        var again = new LanguageEditorViewModel(FakeUi.Services(out _), loc, user);
        Assert.AreEqual("Anschließen", again.AllRows.Single(r => r.English == "Connect").Mine);
        again.MineOnly = true;
        Assert.HasCount(1, again.Rows);

        // a translation with other placeholders is flagged (the table would ignore it)
        var row = new TranslationRow("Saved {0} files", "", "Gespeichert");
        Assert.IsTrue(row.HasProblem);
        row.Mine = "{0} Dateien gespeichert";
        Assert.IsFalse(row.HasProblem);
    }

    [TestMethod]
    public void StringListReadsUntranslatedTxtLines()
    {
        var list = StringList.Parse("# comment\nConnect\tall\r\nLine one\\nline two\tde-DE fr\n\nback\\\\slash\tall\n");
        CollectionAssert.AreEqual(new[] { "Connect", "Line one\nline two", "back\\slash" }, list);
    }

    // ---- Manage Command List ----

    [TestMethod]
    public void CommandListIsStoredAsMissionPlannersTwoJsonMaps()
    {
        var commands = new List<PlannerCommand>
        {
            new(16, "WAYPOINT", [null, null, null, null, "Lat", "Long", "Alt"]),
            new(50000, "MY_CMD", ["Speed", null, null, null, null, null, null]),
        };
        var (c, i) = PlannerExtraCommands.Serialize(commands);
        Assert.AreEqual("""{"WAYPOINT":[null,null,null,null,"Lat","Long","Alt"],"MY_CMD":["Speed",null,null,null,null,null,null]}""", c);
        Assert.AreEqual("""{"MY_CMD":50000}""", i);

        var back = PlannerExtraCommands.Parse(c, i);
        Assert.HasCount(2, back);
        Assert.AreEqual((ushort)16, back[0].Id);
        Assert.IsTrue(back[0].IsKnown);
        Assert.AreEqual((ushort)50000, back[1].Id);
        Assert.AreEqual("Speed", back[1].ParamNames[0]);
        Assert.IsEmpty(PlannerExtraCommands.Parse("""{"UNKNOWN":[]}""", "{}"), "an unknown name without an ID is dropped");
    }

    [TestMethod]
    public void SavedCommandsJoinThePlannerCatalog()
    {
        var vm = new CommandListViewModel(FakeUi.Services(out _));
        Assert.IsEmpty(vm.Rows);
        vm.NewId = "211";
        Assert.AreEqual("DO_GRIPPER", vm.NewIdName);
        vm.AddCommand.Execute(null);
        vm.NewId = "211";
        vm.AddCommand.Execute(null);
        Assert.HasCount(1, vm.Rows, "Name or ID exists");
        vm.NewId = "45000";
        Assert.IsTrue(vm.NeedsName);
        vm.NewName = "MY_SPRAYER";
        vm.AddCommand.Execute(null);
        Assert.HasCount(2, vm.Rows);
        vm.Rows[1].P1 = "Rate";

        var changed = 0;
        EventHandler h = (_, _) => changed++;
        PlannerExtraCommands.Changed += h;
        try
        {
            vm.SaveCommand.Execute(null);
        }
        finally
        {
            PlannerExtraCommands.Changed -= h;
        }

        Assert.AreEqual(1, changed);
        Assert.HasCount(2, PlannerExtraCommands.Load());

        var catalog = MavCmdCatalog.Fallback(MavCmdSection.Copter).WithExtras(PlannerExtraCommands.Load());
        CollectionAssert.Contains(catalog.MissionCommands.ToList(), MAV_CMD.DO_GRIPPER);
        CollectionAssert.Contains(catalog.MissionCommands.ToList(), (MAV_CMD)45000);
        var headers = catalog.HeadersFor((MAV_CMD)45000);
        Assert.AreEqual("Rate", headers[0]);
        Assert.AreEqual("Alt (m)", headers[6]);

        // a name the section already has keeps the section's labels
        var wp = catalog.HeadersFor(MAV_CMD.WAYPOINT);
        var withWp = MavCmdCatalog.Fallback(MavCmdSection.Copter).WithExtras([new PlannerCommand(16, "WAYPOINT", ["X", null, null, null, null, null, null])]);
        CollectionAssert.AreEqual(wp, withWp.HeadersFor(MAV_CMD.WAYPOINT));
    }

    // ---- Param gen ----

    [TestMethod]
    public void GeneratedParameterFileIsUsedBeforeTheShippedBackup()
    {
        var files = ParamMetadata.LegacyFiles().ToList();
        Assert.AreEqual(Settings.GetUserDataDirectory() + "ParameterMetaData.xml", files[0]);
        StringAssert.EndsWith(files[1], "ParameterMetaDataBackup.xml");
    }

    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task ParameterDocumentationDownloadsFromArdupilotOrg()
    {
        var dir = Path.Combine(TestEnvironment.DataRoot, "pdef-" + Guid.NewGuid().ToString("N"));
        var result = await ParamDocsDownload.DownloadAsync(dir, ["AP_Periph"]);
        if (result.Updated.Count == 0)
            Assert.Inconclusive("autotest.ardupilot.org could not be reached (offline?)");

        var xml = Path.Combine(dir, "AP_Periph.apm.pdef.xml");
        Assert.IsTrue(File.Exists(xml));
        Assert.IsTrue(File.Exists(xml + ".gz"));
        Assert.IsGreaterThan(10, ParamMetadata.ParsePdef(xml).Count);
        Assert.IsEmpty(Directory.GetFiles(dir, "*.download"), "no temporary file left");
    }
}
