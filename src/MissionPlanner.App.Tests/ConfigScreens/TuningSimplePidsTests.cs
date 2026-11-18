using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>Basic Tuning sliders: acsimplepids.xml, the names newer firmware uses and the linked multipliers.</summary>
[TestClass]
public class TuningSimplePidsTests
{
    private static string? FindShippedXml()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, TuningSimplePids.FileName);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static Func<string, bool> Has(params string[] names) => new HashSet<string>(names).Contains;

    [TestMethod]
    public void TheShippedXmlIsReadAndMatchesTheBuiltInCopy()
    {
        var path = FindShippedXml();
        Assert.IsNotNull(path, "acsimplepids.xml is in the repository root");

        var fromFile = TuningSimplePids.Load(path);
        Assert.HasCount(TuningSimplePids.CopterXmlDefaults.Count, fromFile);
        for (var i = 0; i < fromFile.Count; i++)
        {
            var a = TuningSimplePids.CopterXmlDefaults[i];
            var b = fromFile[i];
            Assert.AreEqual(a.Title, b.Title);
            Assert.AreEqual(a.Description, b.Description);
            Assert.AreEqual(a.Name, b.Name);
            Assert.AreEqual(a.Min, b.Min, 1e-9);
            Assert.AreEqual(a.Max, b.Max, 1e-9);
            CollectionAssert.AreEqual(a.Relations.ToArray(), b.Relations.ToArray(), a.Name);
        }

        var rate = fromFile.Single(d => d.Name == "ATC_RAT_RLL_P");
        CollectionAssert.AreEqual(
            new[] { new TuningRelation("ATC_RAT_RLL_I", 1), new TuningRelation("ATC_RAT_PIT_P", 1), new TuningRelation("ATC_RAT_PIT_I", 1) },
            rate.Relations.ToArray());
        Assert.AreEqual(new TuningRelation("ACCEL_Z_I", 2), fromFile.Single(d => d.Name == "ACCEL_Z_P").Relations.Single());
    }

    [TestMethod]
    public void CurrentCopterNamesFillTheXmlSlots()
    {
        var defs = TuningSimplePids.Resolve(TuningSimplePids.For(Firmwares.ArduCopter2, xmlPath: null),
            Has("ATC_INPUT_TC", "ATC_RAT_RLL_P", "ATC_RAT_RLL_I", "MOT_THST_HOVER", "PSC_ACCZ_P", "PSC_ACCZ_I"));

        CollectionAssert.AreEqual(new[] { "ATC_INPUT_TC", "ATC_RAT_RLL_P", "MOT_THST_HOVER", "PSC_ACCZ_P" }, defs.Select(d => d.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "RC Feel Roll/Pitch", "Roll/Pitch Sensitivity", "Throttle Hover", "Climb Sensitivity" },
            defs.Select(d => d.Title).ToArray());
        Assert.IsTrue(defs[0].Reversed, "a smaller input time constant is crisper");

        // 4.7 renamed the accel controller: its slider takes the new name
        var v47 = TuningSimplePids.Resolve(TuningSimplePids.Copter(null), Has("PSC_D_ACC_P", "PSC_D_ACC_I", "PSC_ACCZ_P"));
        Assert.AreEqual("PSC_D_ACC_P", v47.Single().Name);
        Assert.AreEqual(new TuningRelation("PSC_D_ACC_I", 2), v47.Single().Relations.Single());
    }

    [TestMethod]
    public void OlderCopterNamesUseTheXml()
    {
        var defs = TuningSimplePids.Resolve(TuningSimplePids.Copter(null),
            Has("RC_FEEL_RP", "RATE_RLL_P", "RATE_RLL_I", "RATE_PIT_P", "RATE_PIT_I", "THR_MID", "ACCEL_Z_P", "ACCEL_Z_I"));

        CollectionAssert.AreEqual(new[] { "RC_FEEL_RP", "RATE_RLL_P", "THR_MID", "ACCEL_Z_P" }, defs.Select(d => d.Name).ToArray());
        Assert.IsFalse(defs[0].Reversed);
    }

    [TestMethod]
    public void ASliderWritesItsRelatedValuesAtTheirMultipliers()
    {
        var copter = TuningSimplePids.Copter(null);
        var rate = copter.Single(d => d.Name == "ATC_RAT_RLL_P");
        var all = Has("ATC_RAT_RLL_P", "ATC_RAT_RLL_I", "ATC_RAT_PIT_P", "ATC_RAT_PIT_I");

        var values = TuningSimplePids.Values(rate, 0.2, all);
        CollectionAssert.AreEqual(new[]
        {
            new KeyValuePair<string, double>("ATC_RAT_RLL_P", 0.2),
            new KeyValuePair<string, double>("ATC_RAT_RLL_I", 0.2),
            new KeyValuePair<string, double>("ATC_RAT_PIT_P", 0.2),
            new KeyValuePair<string, double>("ATC_RAT_PIT_I", 0.2),
        }, values.ToArray());

        // climb: I at twice P, at the precision the vehicle stores
        var climb = copter.Single(d => d.Name == "PSC_ACCZ_P");
        var climbValues = TuningSimplePids.Values(climb, 0.15, Has("PSC_ACCZ_P", "PSC_ACCZ_I"));
        Assert.AreEqual(0.3, climbValues.Single(v => v.Key == "PSC_ACCZ_I").Value);
        Assert.AreEqual(0.3, TuningSimplePids.Linked(0.1, 3));

        // a related parameter the vehicle lacks is not written
        var partial = TuningSimplePids.Values(rate, 0.2, Has("ATC_RAT_RLL_P", "ATC_RAT_PIT_P"));
        CollectionAssert.AreEqual(new[] { "ATC_RAT_RLL_P", "ATC_RAT_PIT_P" }, partial.Select(v => v.Key).ToArray());
    }

    [TestMethod]
    public void AnEditedXmlReplacesTheDefaults()
    {
        var path = Path.Combine(TestEnvironment.DataRoot, "acsimplepids-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <simple>
              <ac>
                <version>3.0</version>
                <param>
                  <title>Yaw Sensitivity:</title>
                  <desc>Yaw.</desc>
                  <name>ATC_RAT_YAW_P</name>
                  <value>0.2</value>
                  <min>0.1</min>
                  <max>1.5</max>
                  <relation>
                    <param>ATC_RAT_YAW_I</param>
                    <multiplier>0.1</multiplier>
                  </relation>
                </param>
              </ac>
            </simple>
            """);

        var defs = TuningSimplePids.Copter(path);
        var yaw = defs.First();
        Assert.AreEqual("Yaw Sensitivity", yaw.Title);
        Assert.AreEqual(1.5, yaw.Max);
        Assert.AreEqual(new TuningRelation("ATC_RAT_YAW_I", 0.1), yaw.Relations.Single());
        Assert.IsFalse(defs.Any(d => d.Name == "RATE_RLL_P"), "the file replaces the built-in copy");
        Assert.IsTrue(defs.Any(d => d.Name == "MOT_THST_HOVER"), "current names are still offered");

        // a missing or broken file falls back to the built-in copy
        Assert.IsTrue(TuningSimplePids.Copter(path + ".missing").Any(d => d.Name == "RATE_RLL_P"));
        File.WriteAllText(path, "<simple><ac>");
        Assert.IsTrue(TuningSimplePids.Copter(path).Any(d => d.Name == "RATE_RLL_P"));
    }

    [TestMethod]
    public void CopterSitlGetsTheFourSlidersUnderTheirCurrentNames()
    {
        var p = TuningFixtures.Load(TuningFixtures.Copter);
        var defs = TuningSimplePids.Resolve(TuningSimplePids.For(Firmwares.ArduCopter2, xmlPath: null), p.ContainsKey);

        CollectionAssert.AreEqual(new[] { "ATC_INPUT_TC", "ATC_RAT_RLL_P", "MOT_THST_HOVER", "PSC_D_ACC_P" }, defs.Select(d => d.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "ATC_RAT_RLL_I", "ATC_RAT_PIT_P", "ATC_RAT_PIT_I" }, defs[1].Relations.Select(r => r.Name).ToArray());
        Assert.AreEqual(new TuningRelation("PSC_D_ACC_I", 2), defs[3].Relations.Single());
    }

    [TestMethod]
    public void OnlyCoptersHaveSliders()
    {
        // planes and rovers get Mission Planner's ConfigArduplane / ConfigArdurover cards as Basic Tuning
        Assert.IsEmpty(TuningSimplePids.For(Firmwares.ArduPlane, null));
        Assert.IsEmpty(TuningSimplePids.For(Firmwares.ArduRover, null));
        Assert.IsEmpty(TuningSimplePids.For(Firmwares.ArduSub, null));
    }
}
