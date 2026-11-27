using MissionPlanner.App.Core.Speech;

namespace MissionPlanner.App.Tests.Speech;

[TestClass]
public class SpeechTextTests
{
    [TestMethod]
    public void NumbersUnitsAndOrdinalsBecomeWords()
    {
        Assert.AreEqual("Battery fifty percent", SpeechText.Normalize("Battery 50%"));
        Assert.AreEqual("Battery one is low ten point two volts", SpeechText.Normalize("Battery 1 is low 10.2V"));
        Assert.AreEqual("used one thousand five hundred milliamp hours", SpeechText.Normalize("used 1,500 mAh"));
        Assert.AreEqual("altitude minus three point five", SpeechText.Normalize("altitude -3.5"));
        Assert.AreEqual("Reached the third waypoint", SpeechText.Normalize("Reached the 3rd waypoint"));
        Assert.AreEqual("Waypoint one hundred twelve", SpeechText.Normalize("Waypoint 112"));
        Assert.AreEqual("two million three hundred thousand", SpeechText.Normalize("2300000"));
        Assert.AreEqual("zero zero seven", SpeechText.Normalize("007"));
        Assert.AreEqual("GPS two A", SpeechText.Normalize("GPS 2 A"), "a one-letter unit only right after the number");
    }

    [TestMethod]
    public void GcsWordsAreRespelledAndRunsSplit()
    {
        Assert.AreEqual("Mode Position Hold", SpeechText.Normalize("Mode PosHold"));
        Assert.AreEqual("Mode Altitude Hold", SpeechText.Normalize("Mode AltHold"));
        Assert.AreEqual("Mode Zig Zag", SpeechText.Normalize("Mode ZigZag"));
        Assert.AreEqual("EKF three IMU zero is using GPS", SpeechText.Normalize("EKF3 IMU0 is using GPS"));
        Assert.AreEqual("GPS Fix", SpeechText.Normalize("GPSFix"));
        Assert.AreEqual("GPS one: detected U blocks at one hundred fifteen thousand two hundred bawd",
            SpeechText.Normalize("GPS 1: detected u-blox at 115200 baud"));
    }

    [TestMethod]
    public void SentencesSplitAtTheirEndsNotInsideNumbers()
    {
        CollectionAssert.AreEqual(new[] { "Mode changed to Loiter.", "Battery 10.5 percent." },
            SpeechText.Sentences("Mode changed to Loiter. Battery 10.5 percent.").ToArray());
        CollectionAssert.AreEqual(new[] { "Pre Arm:", "Compass not calibrated" }, SpeechText.Sentences("Pre Arm: Compass not calibrated").ToArray());
        Assert.IsEmpty(SpeechText.Sentences(" . "));
    }

    private static Phonemizer Lexicon()
    {
        var path = Path.Combine(SpeechTestSupport.NewFolder("lexicon"), "lexicon.txt");
        File.WriteAllLines(path,
        [
            "# Lexicon: word(s) || space-separated phones",
            "mode || m ˈ o ʊ d",
            "degree || d ᵻ ɡ ɹ ˈ iː",
            "box || b ˈ ɑː k s",
            "cat || k ˈ æ t",
            "want || w ˈ ɑ n t",
            "arm || ˈ ɑː ɹ m",
            "calibrate || k ˈ æ l ɪ b ɹ ˌ e ɪ t",
            "way || w ˈ e ɪ",
            "point || p ˈ ɔ ɪ n t",
            "e || ˈ iː",
            "k || k ˈ e ɪ",
            "f || ˈ ɛ f",
            "a dog || ɐ d ˈ ɑː ɡ",
        ]);
        return Phonemizer.Load(path);
    }

    [TestMethod]
    public void TheLexiconIsReadWithoutSeparatorsAndMultiWordEntriesAreLeftOut()
    {
        var p = Lexicon();
        Assert.AreEqual("mˈoʊd", p.Word("Mode"), "case does not matter");
        Assert.IsFalse(p.Knows("a dog"));
        Assert.AreEqual(12, p.Count);

        // Kokoro's own format: "word phones"
        var path = Path.Combine(SpeechTestSupport.NewFolder("lexicon"), "lexicon-us-en.txt");
        File.WriteAllLines(path, ["kokoro k ˈ O k ə ɹ O", "mode m ˈ O d"]);
        Assert.AreEqual("kˈOkəɹO", Phonemizer.Load(path).Word("kokoro"));
    }

    [TestMethod]
    public void MissingWordsAreBuiltFromTheLexiconNeverDropped()
    {
        var p = Lexicon();
        Assert.AreEqual("dᵻɡɹˈiːz", p.Word("degrees"), "plural from the stem");
        Assert.AreEqual("bˈɑːksᵻz", p.Word("boxes"), "-es after a sibilant");
        Assert.AreEqual("kˈæts", p.Word("cats"), "voiceless: s");
        Assert.AreEqual("wˈɑntᵻd", p.Word("wanted"), "-ed after t");
        Assert.AreEqual("ˈɑːɹmd", p.Word("armed"));
        Assert.AreEqual("ˈɑːɹmɪŋ", p.Word("arming"));
        Assert.AreEqual("kˈælɪbɹˌeɪtɪŋ", p.Word("calibrating"), "-ing on a stem that lost its e");
        Assert.AreEqual("wˈeɪ pˈɔɪnt", p.Word("waypoint"), "two lexicon words");
        Assert.AreEqual("ˈiː kˈeɪ ˈɛf", p.Word("EKF"), "capitals are spelled");
        Assert.AreEqual("ˈiː kˈeɪ kˈeɪ", p.Word("ekk"), "an unknown word is spelled, never silent");
    }

    [TestMethod]
    public void PhonemesHaveTheShapeOfSherpaOnnxsLexiconPath()
    {
        var p = Lexicon();
        Assert.AreEqual("mˈoʊd, wˈeɪ pˈɔɪnt.", p.Phonemes("Mode, waypoint."));
        Assert.AreEqual("mˈoʊd ˈiː kˈeɪ ˈɛf!", p.Phonemes("mode EKF !"));
        Assert.AreEqual("mˈoʊd, mˈoʊd", p.Phonemes("mode - mode"), "a dash is a short pause");
        Assert.AreEqual("", p.Phonemes(" . , "));
    }

    [TestMethod]
    public void OnlyLatinTextIsForTheEnglishVoices()
    {
        Assert.IsTrue(SpeechText.IsLatinScript("Mode changed to Loiter, 50 °, Ünter"));
        Assert.IsFalse(SpeechText.IsLatinScript("Режим изменён на Loiter"));
        Assert.IsFalse(SpeechText.IsLatinScript("模式"));
    }
}
