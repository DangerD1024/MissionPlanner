using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

namespace MissionPlanner.App.Tests.SetupScreens.OptionalHardware;

[TestClass]
public sealed class OsdLayoutTests
{
    private static KeyValuePair<string, double> P(string name, double value) => new(name, value);

    private static IReadOnlyList<OsdScreen> Sample() => OsdLayout.Parse(
    [
        P("OSD_TYPE", 1), P("OSD_CHAN", 8),
        P("OSD1_ENABLE", 1), P("OSD1_CHAN_MIN", 900), P("OSD1_CHAN_MAX", 1100), P("OSD1_TXT_RES", 0), P("OSD1_FONT", 0),
        P("OSD1_ALTITUDE_EN", 1), P("OSD1_ALTITUDE_X", 23), P("OSD1_ALTITUDE_Y", 8),
        P("OSD1_BAT_VOLT_EN", 1), P("OSD1_BAT_VOLT_X", 24), P("OSD1_BAT_VOLT_Y", 1),
        P("OSD1_LINK_Q_EN", 0), P("OSD1_LINK_Q_X", 1), P("OSD1_LINK_Q_Y", 1),
        P("OSD1_BROKEN_EN", 1), P("OSD1_BROKEN_X", 3), // no _Y: not an element
        P("OSD2_ENABLE", 0),
        P("OSD2_ALTITUDE_EN", 0), P("OSD2_ALTITUDE_X", 0), P("OSD2_ALTITUDE_Y", 0),
        P("OSD5_ENABLE", 0), P("OSD5_CHAN_MIN", 900),
        P("OSD5_PARAM1_EN", 1), P("OSD5_PARAM1_X", 2), P("OSD5_PARAM1_Y", 3), P("OSD5_PARAM1_KEY", 44), P("OSD5_PARAM1_TYPE", 0),
        P("OSD5_SAVE_X", 23), P("OSD5_SAVE_Y", 11),
    ]);

    [TestMethod]
    public void Parse_FindsCompleteElementsAndScreenSettings()
    {
        var screens = Sample();
        CollectionAssert.AreEqual(new[] { 1, 2, 5 }, screens.Select(s => s.Number).ToArray());

        var s1 = screens[0];
        CollectionAssert.AreEqual(new[] { "ALTITUDE", "BAT_VOLT", "LINK_Q" }, s1.Items.Select(i => i.Name).ToArray());
        var alt = s1.Items[0];
        Assert.IsTrue(alt.Enabled);
        Assert.AreEqual(23, alt.X);
        Assert.AreEqual(8, alt.Y);
        Assert.AreEqual("OSD1_ALTITUDE_EN", alt.EnabledParam);
        Assert.IsFalse(s1.Items[2].Enabled);
        // settings in Mission Planner's order, the half element left with them
        CollectionAssert.AreEqual(
            new[] { "OSD1_ENABLE", "OSD1_CHAN_MIN", "OSD1_CHAN_MAX", "OSD1_TXT_RES", "OSD1_FONT", "OSD1_BROKEN_EN", "OSD1_BROKEN_X" },
            s1.Settings.ToArray());
        Assert.IsFalse(s1.IsParamScreen);
    }

    [TestMethod]
    public void Parse_ParamScreen_KeepsSlotExtrasOutOfTheSettings()
    {
        var s5 = Sample().Single(s => s.Number == 5);
        Assert.AreEqual("PARAM1", s5.Items.Single().Name);
        Assert.IsTrue(s5.IsParamScreen);
        CollectionAssert.AreEqual(new[] { "OSD5_ENABLE", "OSD5_CHAN_MIN", "OSD5_SAVE_X", "OSD5_SAVE_Y" }, s5.Settings.ToArray());
    }

    [TestMethod]
    public void Edits_AreListedAsChanges_UntilAcceptedOrDiscarded()
    {
        var s1 = Sample()[0];
        var alt = s1.Items[0];
        var link = s1.Items[2];
        Assert.IsFalse(s1.Changes().Any());

        alt.X = 2;
        alt.Y = 14;
        link.Enabled = true;
        Assert.IsTrue(alt.IsChanged);
        CollectionAssert.AreEquivalent(
            new[] { P("OSD1_ALTITUDE_X", 2), P("OSD1_ALTITUDE_Y", 14), P("OSD1_LINK_Q_EN", 1) },
            s1.Changes().ToArray());

        alt.Accept("OSD1_ALTITUDE_X");
        CollectionAssert.AreEquivalent(new[] { P("OSD1_ALTITUDE_Y", 14), P("OSD1_LINK_Q_EN", 1) }, s1.Changes().ToArray());

        alt.Discard();
        link.Discard();
        Assert.AreEqual(2, alt.X, "the written X stays");
        Assert.AreEqual(8, alt.Y);
        Assert.IsFalse(link.Enabled);
        Assert.IsFalse(s1.Changes().Any());
    }

    [TestMethod]
    public void XValueAndYValue_EditThePosition()
    {
        var alt = Sample()[0].Items[0];
        alt.XValue = 5.4m;
        alt.YValue = 3m;
        Assert.AreEqual(5, alt.X);
        Assert.AreEqual(3, alt.Y);
        alt.XValue = null;
        Assert.AreEqual(5, alt.X);
    }

    [TestMethod]
    public void CopyLayout_CopiesTheElementsBothScreensHave()
    {
        var screens = Sample();
        var s1 = screens[0];
        var s2 = screens[1];
        OsdLayout.CopyLayout(s1, s2);
        var alt2 = s2.Items.Single();
        Assert.IsTrue(alt2.Enabled);
        Assert.AreEqual(23, alt2.X);
        Assert.AreEqual(8, alt2.Y);
        CollectionAssert.AreEquivalent(
            new[] { P("OSD2_ALTITUDE_EN", 1), P("OSD2_ALTITUDE_X", 23), P("OSD2_ALTITUDE_Y", 8) },
            s2.Changes().ToArray());
    }

    [TestMethod]
    public void Grid_FromTextResolution_AndClamp()
    {
        Assert.AreEqual(OsdGrid.Sd, OsdGrid.FromTextResolution(null));
        Assert.AreEqual(OsdGrid.Sd, OsdGrid.FromTextResolution(0));
        Assert.AreEqual(OsdGrid.Hd50, OsdGrid.FromTextResolution(1));
        Assert.AreEqual(OsdGrid.Hd60, OsdGrid.FromTextResolution(2));
        Assert.AreEqual(13, OsdGrid.Sd.NtscRows);

        Assert.AreEqual((29, 15), OsdLayout.Clamp(40, 20, OsdGrid.Sd));
        Assert.AreEqual((0, 0), OsdLayout.Clamp(-3, -1, OsdGrid.Sd));
        Assert.AreEqual((40, 20), OsdLayout.Clamp(40, 20, OsdGrid.Hd60));
    }

    [TestMethod]
    public void Captions_SampleOrName()
    {
        Assert.AreEqual("ALTITUDE", OsdCaptions.For("ALTITUDE", names: true));
        Assert.AreNotEqual("ALTITUDE", OsdCaptions.For("ALTITUDE"));
        Assert.AreEqual("NEWTHING", OsdCaptions.For("NEWTHING"));
        StringAssert.StartsWith(OsdCaptions.For("PARAM3"), "PARAM3");
    }
}
