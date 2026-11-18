using MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

namespace MissionPlanner.App.Tests.SetupScreens.Comms;

[TestClass]
public sealed class TerminalBufferTests
{
    [TestMethod]
    public void CrLfMakesLinesAndControlsAreDropped()
    {
        var b = new TerminalBuffer();
        b.Append("hello\r\nworld\0\a\r\n");
        Assert.AreEqual("hello\nworld\n", b.Text);
        Assert.AreEqual("hello\nworld\n", b.FullLog);
    }

    [TestMethod]
    public void AnsiSequencesAreStrippedEvenWhenSplitAcrossReads()
    {
        var b = new TerminalBuffer();
        b.Append("\x1b[1;32mnsh\x1b");
        b.Append("[0m> \x1b[");
        b.Append("K");
        Assert.AreEqual("nsh> ", b.Text);
    }

    [TestMethod]
    public void TwoCharacterEscapesAreDropped()
    {
        var b = new TerminalBuffer();
        b.Append("a\x1b" + "7b\x1b" + "8c");
        Assert.AreEqual("abc", b.Text);
    }

    [TestMethod]
    public void BackspaceErasesAndLoneCrOverwritesTheLine()
    {
        var b = new TerminalBuffer();
        b.Append("lsx\b \b\r\n");
        b.Append("10%\r20%\r30%");
        Assert.AreEqual("ls\n30%", b.Text);
    }

    [TestMethod]
    public void ScrollbackKeepsTheLastLinesButTheLogKeepsAll()
    {
        var b = new TerminalBuffer(maxLines: 3);
        for (var i = 1; i <= 5; i++)
            b.Append($"line {i}\n");
        Assert.AreEqual("line 3\nline 4\nline 5\n", b.Text);
        Assert.AreEqual(2, b.TrimmedLines);
        StringAssert.StartsWith(b.FullLog, "line 1\nline 2\n");
    }

    [TestMethod]
    public void AppendLineEndsAPartialLineFirst()
    {
        var b = new TerminalBuffer();
        b.Append("nsh> ");
        b.AppendLine("*** Closed");
        Assert.AreEqual("nsh> \n*** Closed\n", b.Text);
        b.Clear();
        Assert.AreEqual("", b.Text);
    }

    [TestMethod]
    public void HistoryWalksBackAndForwardSkippingRepeats()
    {
        var h = new CommandHistory();
        h.Add("ver");
        h.Add("ver");
        h.Add("uptime");
        h.Add("");
        CollectionAssert.AreEqual(new[] { "ver", "uptime" }, h.Items.ToArray());

        Assert.AreEqual("uptime", h.Previous());
        Assert.AreEqual("ver", h.Previous());
        Assert.AreEqual("ver", h.Previous(), "stays at the oldest");
        Assert.AreEqual("uptime", h.Next());
        Assert.AreEqual("", h.Next(), "past the newest: an empty line");
        Assert.IsNull(h.Next());
    }
}
