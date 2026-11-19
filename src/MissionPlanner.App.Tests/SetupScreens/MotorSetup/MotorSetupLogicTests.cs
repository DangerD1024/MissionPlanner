using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;

namespace MissionPlanner.App.Tests.SetupScreens.MotorSetup;

internal static class Layouts
{
    private static readonly Lazy<MotorLayoutCatalog> Shipped = new(() =>
        MotorLayoutCatalog.Load(SetupScreens.HardwareSetupMotorLayoutTests.RepoFile(MotorLayoutCatalog.FileName)));

    /// <summary>The APMotorLayout.json shipped with Mission Planner.</summary>
    public static MotorLayoutCatalog Catalog
    {
        get
        {
            var c = Shipped.Value;
            if (c.Layouts.Count == 0)
                Assert.Inconclusive("APMotorLayout.json could not be read");
            return c;
        }
    }

    public static MotorLayout Get(int frameClass, int frameType) =>
        Catalog.Find(frameClass, frameType) ?? throw new AssertFailedException($"no layout {frameClass}/{frameType}");

    /// <summary>Outputs 1..n carrying Motor1..n.</summary>
    public static IReadOnlyList<MotorOutput> Straight(int n) => Enumerable.Range(1, n).Select(i => new MotorOutput(i, i)).ToList();
}

[TestClass]
public sealed class MotorFunctionTests
{
    [TestMethod]
    public void MotorFunctionsFollowArduPilotNumbering()
    {
        Assert.AreEqual(33, MotorFunctions.FunctionOf(1));
        Assert.AreEqual(40, MotorFunctions.FunctionOf(8));
        Assert.AreEqual(82, MotorFunctions.FunctionOf(9));
        Assert.AreEqual(85, MotorFunctions.FunctionOf(12));
        for (var m = 1; m <= 12; m++)
            Assert.AreEqual(m, MotorFunctions.MotorOf(MotorFunctions.FunctionOf(m)));

        Assert.IsNull(MotorFunctions.MotorOf(0));
        Assert.IsNull(MotorFunctions.MotorOf(32));
        Assert.IsNull(MotorFunctions.MotorOf(41));
        Assert.IsNull(MotorFunctions.MotorOf(81));
        Assert.IsNull(MotorFunctions.MotorOf(86));
        Assert.AreEqual(3, MotorFunctions.MotorOf(34.9999), "float noise rounds to the function");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MotorFunctions.FunctionOf(13));
    }

    [TestMethod]
    public void ReadsMotorOutputsUpTo32WithServo32Enable()
    {
        var p = new Dictionary<string, double>
        {
            ["SERVO1_FUNCTION"] = 33, ["SERVO2_FUNCTION"] = 0, ["SERVO3_FUNCTION"] = 35, ["SERVO17_FUNCTION"] = 82,
        };
        CollectionAssert.AreEqual(new[] { new MotorOutput(1, 1), new MotorOutput(3, 3) }, MotorOutputs.Read(n => p.TryGetValue(n, out var v) ? v : null).ToArray());

        p["SERVO_32_ENABLE"] = 1;
        CollectionAssert.AreEqual(new[] { new MotorOutput(1, 1), new MotorOutput(3, 3), new MotorOutput(17, 9) },
            MotorOutputs.Read(n => p.TryGetValue(n, out var v) ? v : null).ToArray());
    }

    [TestMethod]
    public void CheckBlocksDuplicatesAndMissingMotorsAndIgnoresForeignOnes()
    {
        var quad = Layouts.Get(1, 1);
        Assert.IsTrue(MotorOutputs.Check(quad, Layouts.Straight(4)).CanRunWizard);

        var dup = MotorOutputs.Check(quad, [new(1, 1), new(2, 2), new(3, 3), new(4, 4), new(6, 2)]);
        Assert.IsFalse(dup.CanRunWizard);
        StringAssert.Contains(dup.Problems.Single(), "Motor 2 is on outputs 2 and 6");

        var missing = MotorOutputs.Check(quad, [new(1, 1), new(2, 2), new(3, 3)]);
        StringAssert.Contains(missing.Problems.Single(), "Motor 4");

        var foreign = MotorOutputs.Check(quad, [new(1, 1), new(2, 2), new(3, 3), new(4, 4), new(9, 5)]);
        Assert.IsTrue(foreign.CanRunWizard);
        Assert.AreEqual(new MotorOutput(9, 5), foreign.Ignored.Single());
        Assert.HasCount(4, foreign.Outputs);
    }
}

[TestClass]
public sealed class MotorOrderWizardTests
{
    private static readonly MotorSafety Safe = new(Connected: true, PropellersRemoved: true, Armed: false);

    [TestMethod]
    public void TestOrderNotMotorNumberIsWhatSpinsAnOutput()
    {
        // Quad Plus: motor 3 (front) is test A, motor 1 (right) is test B
        var w = new MotorOrderWizard(Layouts.Get(1, 0), Layouts.Straight(4));
        Assert.IsNull(w.Begin(Safe));
        Assert.AreEqual(new MotorOutput(1, 1), w.Current);
        Assert.AreEqual(2, w.CurrentTestOrder);
        w.Place(1);
        w.Place(2);
        Assert.AreEqual(new MotorOutput(3, 3), w.Current);
        Assert.AreEqual(1, w.CurrentTestOrder);
    }

    [TestMethod]
    public void SafetyGateComesFirst()
    {
        var w = new MotorOrderWizard(Layouts.Get(1, 1), Layouts.Straight(4));
        Assert.AreEqual(MotorOrderStage.Safety, w.Stage);
        Assert.AreEqual(MotorAssignResult.NotIdentifying, w.Place(1));
        StringAssert.Contains(w.Begin(Safe with { Connected = false }), "Connect");
        StringAssert.Contains(w.Begin(Safe with { PropellersRemoved = false }), "propellers");
        StringAssert.Contains(w.Begin(Safe with { Armed = true }), "Disarm");
        Assert.AreEqual(MotorOrderStage.Safety, w.Stage);
        Assert.IsNull(w.Begin(Safe));
        Assert.AreEqual(MotorOrderStage.Identify, w.Stage);
    }

    [TestMethod]
    public void SwappedWiringGivesTheSwappedFunctions()
    {
        var w = new MotorOrderWizard(Layouts.Get(1, 1), Layouts.Straight(4));
        w.Begin(Safe);
        // output 1 turns out to be the motor at position 2, output 2 at position 1
        Assert.AreEqual(MotorAssignResult.Next, w.Place(2));
        Assert.AreEqual(MotorAssignResult.Next, w.Place(1));
        Assert.AreEqual(MotorAssignResult.Next, w.Place(3));
        Assert.AreEqual(MotorAssignResult.Finished, w.Place(4));
        Assert.AreEqual(MotorOrderStage.Review, w.Stage);

        var m = w.Mapping();
        Assert.IsTrue(m.IsPermutation, m.Problem);
        Assert.IsFalse(m.IsIdentity);
        CollectionAssert.AreEqual(
            new[] { new KeyValuePair<string, double>("SERVO1_FUNCTION", 34), new KeyValuePair<string, double>("SERVO2_FUNCTION", 33) },
            m.Writes().ToArray());
    }

    [TestMethod]
    public void UnchangedOrderNeedsNoWrites()
    {
        var w = new MotorOrderWizard(Layouts.Get(1, 1), Layouts.Straight(4));
        w.Begin(Safe);
        for (var i = 1; i <= 4; i++)
            w.Place(i);
        Assert.IsTrue(w.Mapping().IsIdentity);
        Assert.IsEmpty(w.Mapping().Writes());
    }

    [TestMethod]
    public void APositionCanOnlyBeChosenOnceAndBackUndoes()
    {
        var w = new MotorOrderWizard(Layouts.Get(1, 1), Layouts.Straight(4));
        w.Begin(Safe);
        w.Place(3);
        Assert.AreEqual(MotorAssignResult.PositionTaken, w.Place(3));
        Assert.AreEqual(MotorAssignResult.UnknownPosition, w.Place(7));
        Assert.AreEqual(1, w.OutputAt(3));
        Assert.AreEqual(2, w.Current!.Output);

        Assert.IsTrue(w.Back());
        Assert.AreEqual(1, w.Current!.Output);
        Assert.IsNull(w.OutputAt(3));
        Assert.IsFalse(w.Back(), "nothing before the first output");

        w.Place(1);
        w.Place(2);
        w.Place(3);
        w.Place(4);
        Assert.IsTrue(w.Back(), "from the review back to the last output");
        Assert.AreEqual(MotorOrderStage.Identify, w.Stage);
        Assert.AreEqual(4, w.Current!.Output);
        Assert.AreEqual(MotorAssignResult.Finished, w.Place(4));
    }

    [TestMethod]
    public void OutputsAreTestedInOutputOrderAndMotorsAbove8UseTheirFunctions()
    {
        // DodecaHexa wired backwards on outputs 3..14
        var outputs = Enumerable.Range(1, 12).Select(m => new MotorOutput(15 - m, m)).ToList();
        var w = new MotorOrderWizard(Layouts.Get(12, 1), outputs);
        w.Begin(Safe);
        Assert.AreEqual(3, w.Current!.Output);
        for (var i = 0; i < 12; i++)
            w.Place(i + 1); // output 3+i sits at motor i+1
        var m = w.Mapping();
        Assert.IsTrue(m.IsPermutation, m.Problem);
        var writes = m.Writes().ToDictionary(k => k.Key, k => k.Value);
        Assert.AreEqual(33, writes["SERVO3_FUNCTION"]);
        Assert.AreEqual(82, writes["SERVO11_FUNCTION"]);
        Assert.AreEqual(85, writes["SERVO14_FUNCTION"]);
    }

    [TestMethod]
    public void MappingValidationFindsWhatIsNotAPermutation()
    {
        int[] quad = [1, 2, 3, 4];
        Assert.IsTrue(new MotorMapping([new(1, 1, 2), new(2, 2, 1), new(3, 3, 3), new(4, 4, 4)], quad).IsPermutation);
        StringAssert.Contains(new MotorMapping([new(1, 1, 2), new(2, 2, 2), new(3, 3, 3), new(4, 4, 4)], quad).Problem, "motor 2");
        StringAssert.Contains(new MotorMapping([new(1, 1, 1), new(2, 2, 2), new(3, 3, 3)], quad).Problem, "motor 4");
        StringAssert.Contains(new MotorMapping([new(1, 1, 1), new(2, 2, 2), new(3, 3, 3), new(4, 4, 5)], quad).Problem, "not part");
        StringAssert.Contains(new MotorMapping([new(1, 1, 1), new(1, 2, 2), new(3, 3, 3), new(4, 4, 4)], quad).Problem, "twice");
        Assert.IsFalse(new MotorMapping([], quad).IsPermutation);
    }

    [TestMethod]
    public void WizardRefusesOutputsItCannotSpinSeparately()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new MotorOrderWizard(Layouts.Get(1, 1), [new(1, 1), new(2, 1), new(3, 3), new(4, 4)]));
    }
}

[TestClass]
public sealed class MotorDirectionTests
{
    private static Func<string, double?> Params(params (string Name, double Value)[] values)
    {
        var d = values.ToDictionary(v => v.Name, v => v.Value);
        return n => d.TryGetValue(n, out var v) ? v : null;
    }

    [TestMethod]
    public void ReverseMaskBitsAreOutputs()
    {
        Assert.AreEqual(1u, MotorDirection.Bit(1));
        Assert.AreEqual(0x80000000u, MotorDirection.Bit(32));
        Assert.AreEqual(0b1010u, MotorDirection.WithReversed(0, [2, 4], true));
        Assert.AreEqual(0b1000u, MotorDirection.WithReversed(0b1010u, [2], false));
        Assert.IsTrue(MotorDirection.IsReversed(0b1000u, 4));
        Assert.IsFalse(MotorDirection.IsReversed(0b1000u, 3));
        Assert.IsTrue(MotorDirection.IsReversed(0b1100u, [3, 4]));
        Assert.IsFalse(MotorDirection.IsReversed(0b0100u, [3, 4]), "a motor on two outputs is reversed only if both are");
        CollectionAssert.AreEqual(new[] { 2, 4 }, MotorDirection.Outputs(0b1010u).ToArray());

        // AP_Int32: output 32 makes the value negative
        Assert.AreEqual(-2147483648.0, MotorDirection.ToParam(0x80000000u));
        Assert.AreEqual(0x80000001u, MotorDirection.FromParam(-2147483647));
        Assert.AreEqual(6u, MotorDirection.FromParam(6.0000001));
        Assert.AreEqual(0u, MotorDirection.FromParam(null));
    }

    [TestMethod]
    public void PwmEscsCannotBeReversedInSoftware()
    {
        var s = MotorDirection.Evaluate(Params(("MOT_PWM_TYPE", 0), ("SERVO_DSHOT_ESC", 1), ("SERVO_BLH_RVMASK", 0)), false, [1, 2, 3, 4]);
        Assert.AreEqual(DirectionSupport.NotDshot, s.Support);
        Assert.IsFalse(s.CanReverse);
        StringAssert.Contains(s.Message, "Swap any two");
        StringAssert.Contains(s.Message, "Normal PWM");
    }

    [TestMethod]
    public void DshotWithoutEscTypeOffersTheEscType()
    {
        var s = MotorDirection.Evaluate(Params(("MOT_PWM_TYPE", 6), ("SERVO_DSHOT_ESC", 0), ("SERVO_BLH_RVMASK", 0)), false, [1, 2, 3, 4]);
        Assert.AreEqual(DirectionSupport.EscTypeNone, s.Support);
        Assert.IsTrue(s.CanOfferEscType);
        StringAssert.Contains(s.Message, "no dshot commands will be executed");
    }

    [TestMethod]
    public void DshotWithEscTypeCanReverse()
    {
        var s = MotorDirection.Evaluate(Params(("MOT_PWM_TYPE", 5), ("SERVO_DSHOT_ESC", 2), ("SERVO_BLH_RVMASK", 6), ("SERVO_BLH_BDMASK", 15)), false, [1, 2, 3, 4]);
        Assert.AreEqual(DirectionSupport.Supported, s.Support);
        Assert.AreEqual(6u, s.ReverseMask);
        Assert.IsTrue(s.HasRpmTelemetry);
        Assert.IsNull(s.ThreeDWarning);
        StringAssert.Contains(s.Message, "reboot");
    }

    [TestMethod]
    public void QuadPlanesUseTheirOwnPwmType()
    {
        var p = Params(("Q_M_PWM_TYPE", 6), ("SERVO_DSHOT_ESC", 1), ("SERVO_BLH_RVMASK", 0));
        Assert.AreEqual("Q_M_PWM_TYPE", MotorDirection.Evaluate(p, true, [5, 6, 7, 8]).PwmTypeParam);
        Assert.AreEqual(DirectionSupport.Supported, MotorDirection.Evaluate(p, true, [5, 6, 7, 8]).Support);
    }

    [TestMethod]
    public void WithoutTheMaskParameterNothingCanBeReversed()
    {
        var s = MotorDirection.Evaluate(Params(("MOT_PWM_TYPE", 6), ("SERVO_DSHOT_ESC", 1)), false, [1, 2, 3, 4]);
        Assert.AreEqual(DirectionSupport.NoReverseParam, s.Support);
    }

    [TestMethod]
    public void ThreeDModeOnMotorOutputsIsWarnedAbout()
    {
        var s = MotorDirection.Evaluate(Params(("MOT_PWM_TYPE", 6), ("SERVO_DSHOT_ESC", 1), ("SERVO_BLH_RVMASK", 0), ("SERVO_BLH_3DMASK", 0b100100)), false, [1, 2, 3, 4]);
        StringAssert.Contains(s.ThreeDWarning, "output 3");
        StringAssert.Contains(s.ThreeDWarning, "AM32");
        var other = MotorDirection.Evaluate(Params(("MOT_PWM_TYPE", 6), ("SERVO_DSHOT_ESC", 1), ("SERVO_BLH_RVMASK", 0), ("SERVO_BLH_3DMASK", 0b100000)), false, [1, 2, 3, 4]);
        Assert.IsNull(other.ThreeDWarning, "a 3D rover output elsewhere is not a motor");
    }
}

[TestClass]
public sealed class FrameReversalTests
{
    [TestMethod]
    public void QuadMirrorsArePlusRevHAndBetaflightReversed()
    {
        var c = Layouts.Catalog;
        CollectionAssert.AreEqual(new[] { (0, 6), (1, 3), (12, 18) }, FrameReversal.Pairs(c, 1).ToArray());
        Assert.AreEqual(6, FrameReversal.Partner(c, 1, 0));
        Assert.AreEqual(0, FrameReversal.Partner(c, 1, 6));
        Assert.AreEqual(3, FrameReversal.Partner(c, 1, 1));
        Assert.AreEqual(1, FrameReversal.Partner(c, 1, 3), "H goes back to X, not V");
        Assert.AreEqual(18, FrameReversal.Partner(c, 1, 12));
        Assert.AreEqual(12, FrameReversal.Partner(c, 1, 18));
        Assert.IsNull(FrameReversal.Partner(c, 1, 2), "V draws like X: its partner would be ambiguous");
        Assert.IsNull(FrameReversal.Partner(c, 1, 13), "DJI X");
        Assert.IsNull(FrameReversal.Partner(c, 1, 14), "Clockwise X");
        Assert.IsNull(FrameReversal.Partner(c, 1, 4), "V-Tail has motors without a rotation");

        Assert.IsTrue(FrameReversal.IsReversedMember(c, 1, 3));
        Assert.IsTrue(FrameReversal.IsReversedMember(c, 1, 6));
        Assert.IsTrue(FrameReversal.IsReversedMember(c, 1, 18));
        Assert.IsFalse(FrameReversal.IsReversedMember(c, 1, 1));
    }

    [TestMethod]
    public void OctaQuadMirrorsAreHAndBetaflightReversed()
    {
        CollectionAssert.AreEqual(new[] { (1, 3), (12, 18) }, FrameReversal.Pairs(Layouts.Catalog, 4).ToArray());
    }

    [TestMethod]
    public void OtherFramesHaveNoReversedType()
    {
        foreach (var cls in new[] { 2, 3, 5, 7, 12, 14 })
        {
            Assert.IsEmpty(FrameReversal.Pairs(Layouts.Catalog, cls), "class " + cls);
            foreach (var l in Layouts.Catalog.ForClass(cls))
                Assert.IsNull(FrameReversal.Partner(Layouts.Catalog, cls, l.Type), $"class {cls} type {l.Type}");
        }
    }

    [TestMethod]
    public void MirrorKeepsNumbersAndPositionsAndFlipsEveryRotation()
    {
        var x = Layouts.Get(1, 1);
        var h = Layouts.Get(1, 3);
        Assert.IsTrue(FrameReversal.IsMirror(x, h));
        foreach (var m in x.Motors)
        {
            var o = h.Motors.Single(n => n.Number == m.Number);
            Assert.AreEqual(m.Roll, o.Roll, 1e-6);
            Assert.AreEqual(m.Pitch, o.Pitch, 1e-6);
            Assert.AreNotEqual(m.IsClockwise, o.IsClockwise);
        }

        Assert.IsFalse(FrameReversal.IsMirror(x, Layouts.Get(1, 2)), "V is the same as X, not its mirror");
    }
}

[TestClass]
public sealed class MotorDiagramGeometryTests
{
    [TestMethod]
    public void ClicksHitTheMotorDrawnThere()
    {
        var layout = Layouts.Get(1, 1);
        var g = MotorDiagramGeometry.Compute(layout, 400, 400, 30);
        Assert.HasCount(4, g.Spots);
        foreach (var s in g.Spots)
            Assert.AreEqual(s.Motor, g.HitTest(s.X + s.Radius * 0.5, s.Y));
        Assert.IsNull(g.HitTest(200, 200), "the body is not a motor");

        // Quad X motor 1 is front right: right of centre and above it (front up)
        var m1 = g.SpotOf(1)!.Value;
        Assert.IsGreaterThan(200, m1.X);
        Assert.IsLessThan(200, m1.Y);
        Assert.IsTrue(g.Spots.All(s => s.X - s.Radius >= 30 && s.Y - s.Radius >= 30 && s.X + s.Radius <= 370), "inside the margin");
    }

    [TestMethod]
    public void CoaxialMotorsAreSeparateTargets()
    {
        var y6 = Layouts.Get(5, 10);
        var g = MotorDiagramGeometry.Compute(y6, 400, 400, 30);
        Assert.HasCount(6, g.Spots);
        foreach (var s in g.Spots)
            Assert.AreEqual(s.Motor, g.HitTest(s.X, s.Y));
        var a = g.SpotOf(1)!.Value;
        var b = g.SpotOf(2)!.Value;
        Assert.AreEqual(a.ArmX, b.ArmX, 1e-9, "same arm");
        Assert.IsGreaterThan(a.Radius * 2, Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)), "not overlapping");
    }
}
