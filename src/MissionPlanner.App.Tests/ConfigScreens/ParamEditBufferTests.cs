using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using static MissionPlanner.App.Tests.ConfigScreens.ParamTestSupport;

namespace MissionPlanner.App.Tests.ConfigScreens;

[TestClass]
public class ParamEditBufferTests
{
    [TestMethod]
    [DataRow("1.5", 1.5)]
    [DataRow(" 1,5 ", 1.5)]
    [DataRow("-3", -3.0)]
    [DataRow("1e3", 1000.0)]
    [DataRow("0x10", 16.0)]
    [DataRow("45*100", 4500.0)]
    [DataRow("(1+2)/4", 0.75)]
    [DataRow("-(2+3)*2", -10.0)]
    [DataRow("2.5e-1+1", 1.25)]
    public void ParsesNumbersAndSimpleArithmetic(string text, double expected)
    {
        Assert.IsTrue(ParamValues.TryParse(text, out var v), text);
        Assert.AreEqual(expected, v, 1e-12);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("abc")]
    [DataRow("1/0")]
    [DataRow("1+")]
    [DataRow("(1+2")]
    [DataRow("0xZZ")]
    [DataRow("NaN")]
    public void RejectsWhatIsNotANumber(string text)
    {
        Assert.IsFalse(ParamValues.TryParse(text, out _), text);
    }

    [TestMethod]
    public void FormatsWithoutFloatNoise()
    {
        Assert.AreEqual("0.15", ParamValues.Format((float)0.15));
        Assert.AreEqual("100", ParamValues.Format(100));
        Assert.AreEqual("-2.5", ParamValues.Format(-2.5));
        Assert.AreEqual("16777217", ParamValues.Format(16777217));
    }

    [TestMethod]
    public void ComparesAsTheVehicleStoresValues()
    {
        Assert.IsTrue(ParamValues.Same(0.1, (float)0.1), "a file's 0.1 equals the vehicle's float 0.1");
        Assert.IsFalse(ParamValues.Same(0.1, 0.2));
        Assert.IsFalse(ParamValues.Same(16777217, 16777216), "distinct whole numbers stay distinct");
        Assert.IsTrue(ParamValues.Same(5, 5));
    }

    [TestMethod]
    public void ValidateRejectsTextReadOnlyAndFlagsOutOfRange()
    {
        var meta = Meta("ATC_RAT_RLL_P", min: 0, max: 0.5);

        Assert.AreEqual(EditCheck.NotANumber, ParamEditBuffer.Validate("ATC_RAT_RLL_P", "x1", meta).Result);
        Assert.AreEqual(EditCheck.Ok, ParamEditBuffer.Validate("ATC_RAT_RLL_P", "0.2", meta).Result);
        Assert.AreEqual(EditCheck.Ok, ParamEditBuffer.Validate("ATC_RAT_RLL_P", "0.5", meta).Result, "the range is inclusive");

        var high = ParamEditBuffer.Validate("ATC_RAT_RLL_P", "0.6", meta);
        Assert.AreEqual(EditCheck.OutOfRange, high.Result);
        Assert.AreEqual(0.6, high.Value, 1e-12);
        Assert.IsTrue(high.Message.Contains("out of range"), high.Message);
        Assert.AreEqual(EditCheck.OutOfRange, ParamEditBuffer.Validate("ATC_RAT_RLL_P", "-1", meta).Result);

        var ro = Meta("STAT_RUNTIME", readOnly: true);
        Assert.AreEqual(EditCheck.ReadOnly, ParamEditBuffer.Validate("STAT_RUNTIME", "5", ro).Result);

        // without documentation any number goes
        Assert.AreEqual(EditCheck.Ok, ParamEditBuffer.Validate("X", "1e6", null).Result);
    }

    [TestMethod]
    public void EditEqualToTheVehicleValueIsDropped()
    {
        var buffer = new ParamEditBuffer();
        var changes = 0;
        buffer.Changed += (_, _) => changes++;

        Assert.IsTrue(buffer.Set("A", 2, current: 1));
        Assert.IsTrue(buffer.IsModified("A"));
        Assert.AreEqual(1, changes);

        Assert.IsTrue(buffer.Set("A", 2, current: 1));
        Assert.AreEqual(1, changes, "setting the same value again is not a change");

        Assert.IsFalse(buffer.Set("A", 1, current: 1), "back to the vehicle's value");
        Assert.IsFalse(buffer.IsModified("A"));
        Assert.AreEqual(0, buffer.Count);
        Assert.AreEqual(2, changes);

        Assert.IsFalse(buffer.Set("B", (float)0.1, current: 0.1), "float noise is not an edit");
    }

    [TestMethod]
    public void DescribeListsChangesEnableFirst()
    {
        var buffer = new ParamEditBuffer();
        buffer.Set("BATT_MONITOR", 4, 0);
        buffer.Set("AHRS_EKF_TYPE", 3, 2);
        buffer.Set("FENCE_ENABLE", 1, 0);

        var lines = buffer.Describe(n => n == "AHRS_EKF_TYPE" ? 2 : n == "FENCE_ENABLE" ? 0 : null);

        Assert.AreEqual(3, lines.Count);
        Assert.AreEqual("FENCE_ENABLE: 0 -> 1", lines[0]);
        Assert.AreEqual("AHRS_EKF_TYPE: 2 -> 3", lines[1]);
        Assert.AreEqual("BATT_MONITOR: ? -> 4", lines[2]);

        buffer.RemoveAll(["FENCE_ENABLE", "NOT_THERE"]);
        Assert.AreEqual(2, buffer.Count);
    }

    [TestMethod]
    public void ConfirmationListsUpToTwentyChanges()
    {
        var few = Enumerable.Range(0, 3).Select(i => $"P{i}: 0 -> 1").ToList();
        var text = ParamWriteFlow.ConfirmText(few);
        Assert.IsTrue(text.Contains("change 3 parameters"), text);
        Assert.IsTrue(text.Contains("P2: 0 -> 1"), text);

        var many = Enumerable.Range(0, 25).Select(i => $"P{i}: 0 -> 1").ToList();
        text = ParamWriteFlow.ConfirmText(many);
        Assert.IsTrue(text.Contains("change 25 parameters"), text);
        Assert.IsTrue(text.Contains("P19: 0 -> 1"), text);
        Assert.IsFalse(text.Contains("P20: 0 -> 1"), text);
        Assert.IsTrue(text.Contains("and 5 more"), text);
    }
}
