using System.ComponentModel;
using System.Globalization;
using System.Text;
using MissionPlanner.App.Core.Localization;

namespace MissionPlanner.App.Tests.Localization;

/// <summary>Tables kept in memory as JSON text, by culture.</summary>
internal sealed class MemorySource(Dictionary<string, string> tables) : ITranslationSource
{
    public IEnumerable<string> Cultures => tables.Keys;

    public Stream? Open(string culture) =>
        tables.TryGetValue(culture, out var json) ? new MemoryStream(Encoding.UTF8.GetBytes(json)) : null;
}

[TestClass]
public sealed class LocalizationTextTests
{
    [TestMethod]
    public void SplitRemovesMnemonicsAndKeepsDecoration()
    {
        var p = LocalizationText.Split("  &Load File...  ");
        Assert.AreEqual("  ", p.Leading);
        Assert.AreEqual("Load File", p.Core);
        Assert.AreEqual("...", p.Suffix);
        Assert.AreEqual("  ", p.Trailing);

        Assert.AreEqual("Port", LocalizationText.Split("Port:").Core);
        Assert.AreEqual(":", LocalizationText.Split("Port:").Suffix);
        Assert.AreEqual(" :", LocalizationText.Split("Valeur :").Suffix);
        Assert.AreEqual("…", LocalizationText.Split("Saving…").Suffix);
        Assert.AreEqual("：", LocalizationText.Split("端口：").Suffix);
    }

    [TestMethod]
    public void MnemonicsAndWhitespace()
    {
        Assert.AreEqual("文件", LocalizationText.StripMnemonics("文件(&F)"));
        Assert.AreEqual("Save & Exit", LocalizationText.StripMnemonics("Save && Exit"));
        Assert.AreEqual("File", LocalizationText.StripMnemonics("&File"));
        Assert.AreEqual("Arm & Disarm", LocalizationText.StripMnemonics("Arm & Disarm"));
        Assert.AreEqual("line one line two", LocalizationText.Split("line one\r\n  line two").Core);
        Assert.AreEqual("load file", LocalizationText.Key("&LOAD File:"));
    }

    [TestMethod]
    public void CapitalsAndFormatItems()
    {
        Assert.IsTrue(LocalizationText.IsAllCaps("CONNECT"));
        Assert.IsTrue(LocalizationText.IsAllCaps("ARM / DISARM"));
        Assert.IsFalse(LocalizationText.IsAllCaps("Connect"));
        Assert.IsFalse(LocalizationText.IsAllCaps("X"));

        Assert.IsTrue(LocalizationText.SameFormatItems("Got {0} of {1:F1}", "{1:F1} из {0}"));
        Assert.IsFalse(LocalizationText.SameFormatItems("Got {0}", "Получено"));
        Assert.IsFalse(LocalizationText.SameFormatItems("Got {0}", "Получено {0} {"));
        Assert.IsFalse(LocalizationText.SameFormatItems("Done", "Готово {0}"));
    }
}

[TestClass]
public sealed class ResxHarvestTests
{
    private const string Header = """
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
        """;

    private static string Resx(params (string Name, string Value)[] entries) =>
        Header + string.Concat(entries.Select(e =>
            $"<data name=\"{e.Name}\" xml:space=\"preserve\"><value>{System.Security.SecurityElement.Escape(e.Value)}</value></data>\n")) + "</root>";

    [TestMethod]
    public void ReadsOnlyText()
    {
        var xml = Header + """
              <data name="BUT_load.Text" xml:space="preserve"><value>&amp;Load File...</value></data>
              <data name="BUT_load.ToolTip" xml:space="preserve"><value>Load a mission</value></data>
              <data name="BUT_load.Size" type="System.Drawing.Size, System.Drawing"><value>100, 23</value></data>
              <data name="&gt;&gt;BUT_load.Name" xml:space="preserve"><value>BUT_load</value></data>
              <data name="BUT_load.AccessibleName" xml:space="preserve"><value>load</value></data>
              <data name="pic.Image" type="System.Drawing.Bitmap, System.Drawing" mimetype="application/x-microsoft.net.object.bytearray.base64"><value>AAAA</value></data>
              <data name="ERROR" xml:space="preserve"><value>Error</value></data>
              <data name="$this.Text" xml:space="preserve"><value>Flight Planner</value></data>
              <data name="list.Items2" xml:space="preserve"><value>Relative</value></data>
            </root>
            """;
        var strings = ResxReader.ReadStrings(xml);
        CollectionAssert.AreEquivalent(
            new[] { "BUT_load.Text", "BUT_load.ToolTip", "ERROR", "$this.Text", "list.Items2" },
            strings.Keys.ToArray());
        Assert.AreEqual("&Load File...", strings["BUT_load.Text"]);
    }

    [TestMethod]
    public void PairsByKeyAndSkipsCopiesStaleKeysAndBrokenFormats()
    {
        var english = Resx(("BUT_load.Text", "&Load File..."), ("MSG", "Got {0} points"), ("BAD", "Sent {0}"),
            ("SAME", "GPS Status"), ("label1.Text", "1"));
        var russian = Resx(("BUT_load.Text", "&Загрузить файл..."), ("MSG", "Получено {0} точек"), ("BAD", "Отправлено"),
            ("SAME", "GPS Status"), ("label1.Text", "1"), ("OLD.Text", "Устаревшее"));

        var harvest = new ResxHarvest();
        harvest.Add(english, russian, 1);
        var table = harvest.Build();

        Assert.AreEqual(2, table.Count);
        Assert.AreEqual("Загрузить файл", table["Load File"]);
        Assert.AreEqual("Получено {0} точек", table["Got {0} points"]);
    }

    [TestMethod]
    public void PrefersStringsResxThenTheMostFrequent()
    {
        var harvest = new ResxHarvest();
        harvest.Add(Resx(("a.Text", "Connect")), Resx(("a.Text", "Соединить")), rank: 1);
        harvest.Add(Resx(("b.Text", "Connect:")), Resx(("b.Text", "Соединить:")), rank: 1);
        harvest.Add(Resx(("c.Text", "Connect")), Resx(("c.Text", "Подключиться")), rank: 1);
        harvest.Add(Resx(("d.Text", "Disarm")), Resx(("d.Text", "Снять с охраны")), rank: 1);
        harvest.Add(Resx(("e.Text", "Disarm")), Resx(("e.Text", "Разоружить")), rank: 1);
        Assert.AreEqual("Соединить", harvest.Build()["Connect"], "most frequent");
        Assert.AreEqual("Разоружить", harvest.Build()["Disarm"], "ties go to the first in order");

        // ExtLibs/Strings/Strings.resx (rank 0) wins over any number of screen files
        harvest.Add(Resx(("CONNECT_STR", "Connect")), Resx(("CONNECT_STR", "Подключить")), rank: 0);
        Assert.AreEqual("Подключить", harvest.Build()["Connect"]);
    }

    [TestMethod]
    public void CapitalsKeepTheirOwnEntryAndColonFormIsLearned()
    {
        var harvest = new ResxHarvest();
        harvest.Add(Resx(("MenuHelp.Text", "HELP"), ("a.Text", "Port:"), ("b.Text", "Baud:")),
            Resx(("MenuHelp.Text", "مساعدة"), ("a.Text", "المنفذ :"), ("b.Text", "السرعة :")), rank: 1);
        harvest.Add(Resx(("x.Text", "Help")), Resx(("x.Text", "المحطة الطرفية")), rank: 1);
        var table = harvest.Build();
        Assert.AreEqual("مساعدة", table["HELP"]);
        Assert.AreEqual("المحطة الطرفية", table["Help"]);
        Assert.AreEqual("المنفذ", table["Port"]);
        Assert.AreEqual(" :", harvest.Colon);
    }
}

[TestClass]
public sealed class TranslationTableTests
{
    private static TranslationTable Russian()
    {
        var t = new TranslationTable("ru-RU");
        t.Set("Load File", "Загрузить файл");
        t.Set("Connect", "Подключить");
        t.Set("CONNECT", "СОЕДИНИТЬ");
        t.Set("Disarm", "Снять с охраны");
        t.Set("Got {0} points", "Получено {0} точек");
        return t;
    }

    [TestMethod]
    public void AdaptsTheTranslationToTheSourceForm()
    {
        var t = Russian();
        Assert.IsTrue(t.TryTranslate("Load File...", out var r));
        Assert.AreEqual("Загрузить файл...", r);
        Assert.IsTrue(t.TryTranslate(" load file: ", out r));
        Assert.AreEqual(" Загрузить файл: ", r);
        Assert.IsTrue(t.TryTranslate("&Load File…", out r));
        Assert.AreEqual("Загрузить файл…", r);

        // capitals: the capitals entry when there is one, else the translation in capitals
        Assert.IsTrue(t.TryTranslate("CONNECT", out r));
        Assert.AreEqual("СОЕДИНИТЬ", r);
        Assert.IsTrue(t.TryTranslate("Connect", out r));
        Assert.AreEqual("Подключить", r);
        Assert.IsTrue(t.TryTranslate("DISARM", out r));
        Assert.AreEqual("СНЯТЬ С ОХРАНЫ", r);

        Assert.IsFalse(t.TryTranslate("Unknown text", out r));
        Assert.AreEqual("Unknown text", r);
        Assert.IsFalse(t.TryTranslate("", out _));
    }

    [TestMethod]
    public void UsesTheLanguagesColon()
    {
        var t = new TranslationTable("zh-Hans") { Colon = "：" };
        t.Set("Port", "端口");
        Assert.IsTrue(t.TryTranslate("Port:", out var r));
        Assert.AreEqual("端口：", r);
        Assert.IsTrue(t.TryTranslate("Port", out r));
        Assert.AreEqual("端口", r);
    }

    [TestMethod]
    public void TurkishCapitalsUseTurkishRules()
    {
        var t = new TranslationTable("tr");
        t.Set("Disarm", "Silahsızlandır");
        Assert.IsTrue(t.TryTranslate("DISARM", out var r));
        Assert.AreEqual("SİLAHSIZLANDIR", r);
    }

    [TestMethod]
    public void LoadsStringsThenOverrides()
    {
        const string json = """
            {
              "culture": "ru-RU",
              "colon": ":",
              // comments and trailing commas are fine in hand-edited files
              "overrides": { "Connect": "Подключиться", },
              "strings": { "Connect": "Соединить", "Load File": "Загрузить файл", "Bad {0}": "Плохо" }
            }
            """;
        var t = TranslationTable.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.AreEqual("ru-RU", t.Culture);
        Assert.AreEqual(2, t.Count, "a translation that loses {0} is dropped");
        Assert.IsTrue(t.TryTranslate("Connect", out var r));
        Assert.AreEqual("Подключиться", r);
    }
}

[TestClass]
public sealed class LocalizerTests
{
    private const string RussianJson = """
        { "culture": "ru-RU", "strings": { "Connect": "Подключить", "Got {0} points": "Получено {0} точек", "Broken {0}": "Сломано {0}" } }
        """;

    private const string ArabicJson = """
        { "culture": "ar", "strings": { "Connect": "اتصال" } }
        """;

    private static Localizer Create() =>
        new(new MemorySource(new Dictionary<string, string> { ["ru-RU"] = RussianJson, ["ar"] = ArabicJson }));

    private CultureInfo _ui = CultureInfo.CurrentUICulture;
    private CultureInfo? _defaultUi;

    [TestInitialize]
    public void Save()
    {
        _ui = CultureInfo.CurrentUICulture;
        _defaultUi = CultureInfo.DefaultThreadCurrentUICulture;
    }

    [TestCleanup]
    public void Restore()
    {
        CultureInfo.CurrentUICulture = _ui;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUi;
    }

    [TestMethod]
    public void ListsEnglishAndTheLanguagesWithATableInMissionPlannersOrder()
    {
        var loc = Create();
        CollectionAssert.AreEqual(new[] { "en-US", "ru-RU", "ar" }, loc.Languages.Select(l => l.Code).ToArray());
        Assert.AreEqual("Русский", loc.Languages[1].NativeName);
        Assert.IsTrue(loc.Languages[2].IsRightToLeft);
    }

    [TestMethod]
    public void TranslatesAndFallsBackToEnglish()
    {
        var loc = Create();
        Assert.AreEqual("Connect", loc.Tr("Connect"));

        loc.SetLanguage("ru-RU");
        Assert.AreEqual("ru-RU", loc.Culture);
        Assert.AreEqual("Подключить", loc.Tr("Connect"));
        Assert.AreEqual("Not translated", loc.Tr("Not translated"));
        Assert.AreEqual("", loc.Tr(null));
        Assert.AreEqual("Получено 5 точек", loc.Format("Got {0} points", 5));

        loc.SetLanguage("xx-YY");
        Assert.AreEqual("en-US", loc.Culture);
        Assert.AreEqual("Connect", loc.Tr("Connect"));
    }

    [TestMethod]
    public void RaisesChangesOnlyWhenTheLanguageChanges()
    {
        var loc = Create();
        var changed = 0;
        var properties = new List<string?>();
        loc.LanguageChanged += (_, _) => changed++;
        ((INotifyPropertyChanged)loc).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        loc.SetLanguage("ar");
        Assert.AreEqual(1, changed);
        Assert.IsTrue(loc.IsRightToLeft);
        CollectionAssert.Contains(properties, nameof(Localizer.Culture));

        loc.SetLanguage("ar");
        Assert.AreEqual(1, changed, "same language again");

        loc.SetLanguage("en-US");
        Assert.AreEqual(2, changed);
        Assert.IsFalse(loc.IsRightToLeft);
    }

    [TestMethod]
    public void LaterSourcesCorrectEarlierOnes()
    {
        var loc = Create();
        loc.AddSource(new MemorySource(new Dictionary<string, string>
        {
            ["ru-RU"] = """{ "overrides": { "Connect": "Подключиться", "Write": "Записать" } }""",
        }));
        loc.SetLanguage("ru-RU");
        Assert.AreEqual("Подключиться", loc.Tr("Connect"));
        Assert.AreEqual("Записать", loc.Tr("Write"));
        Assert.AreEqual("Получено 2 точек", loc.Format("Got {0} points", 2));
    }

    [TestMethod]
    public void UserFolderTables()
    {
        var dir = Path.Combine(TestEnvironment.DataRoot, "loc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "uk.json"), """{ "strings": { "Connect": "Підключити" } }""");
        var loc = new Localizer(new DirectoryTranslationSource(dir), new DirectoryTranslationSource(Path.Combine(dir, "missing")));
        CollectionAssert.Contains(loc.Languages.Select(l => l.Code).ToList(), "uk");
        loc.SetLanguage("uk");
        Assert.AreEqual("Підключити", loc.Tr("Connect"));
    }

    [TestMethod]
    [DataRow("ru-RU", "ru-RU")]
    [DataRow("ru-UA", "ru-RU")]
    [DataRow("zh-CN", "zh-Hans")]
    [DataRow("zh-HK", "zh-TW")]
    [DataRow("de-AT", "de-DE")]
    [DataRow("fr-CA", "fr")]
    [DataRow("pt-BR", "pt")]
    [DataRow("ar-EG", "ar")]
    [DataRow("en-GB", "en-US")]
    [DataRow("nl-NL", "en-US")]
    public void MatchesTheSystemCulture(string system, string expected)
    {
        var all = LanguageCatalog.Languages.Select(l => l.Code);
        Assert.AreEqual(expected, Localizer.MatchCulture(all, CultureInfo.GetCultureInfo(system)));
    }

    [TestMethod]
    public void InvariantSystemCultureIsEnglish() =>
        Assert.AreEqual("en-US", Localizer.MatchCulture(LanguageCatalog.Languages.Select(l => l.Code), CultureInfo.InvariantCulture));
}

[TestClass]
public sealed class UiStringScannerTests
{
    [TestMethod]
    public void FindsTrInXaml()
    {
        const string xaml = """
            <TextBlock Text="{l:Tr 'Load File...'}" ToolTip.Tip="{l:Tr 'Don\'t touch'}" />
            <Button Content="{l:Tr Connect}" />
            <TextBlock Text="{l:Tr 'Save &amp; Exit'}" />
            <TextBlock Text="{l:TrBind {Binding StatusText}}" />
            """;
        CollectionAssert.AreEqual(new[] { "Load File...", "Don't touch", "Connect", "Save & Exit" }, UiStringScanner.ScanXaml(xaml).ToArray());
        CollectionAssert.AreEqual(new[] { "StatusText" }, UiStringScanner.ScanXamlTranslatedBindings(xaml).ToArray());
    }

    [TestMethod]
    public void FindsUserStringsInCSharp()
    {
        const string code = """
            public X() : base("Compass") { }
            Add(Page("Install Firmware", () => new F(s), () => Ready() && HasParam("FRAME")));
            await Services.Dialogs.ConfirmAsync("Arm", $"Arm {name}?", "Arm motors");
            await Services.Dialogs.SaveFileAsync("Save parameters", "params.param", new FileFilter("Parameter files", "param"));
            var t = L.Tr("Ready") + L.Format("Got {0}", n);
            Log.Info("not shown");
            """;
        CollectionAssert.AreEquivalent(
            new[] { "Compass", "Install Firmware", "Arm", "Arm motors", "Save parameters", "Parameter files", "Ready", "Got {0}" },
            UiStringScanner.ScanCSharp(code).ToArray());
    }

    [TestMethod]
    public void FindsLiteralsAssignedToTranslatedProperties()
    {
        const string code = """
            StatusText = ok ? "Done" : "Failed";
            StatusText = $"{failed.Count} value{(failed.Count == 1 ? "" : "s")} not set.";
            StatusText = "Error: " + ex.Message;
            _statusText = "Idle";
            Other = "not this";
            public string Title => "Battery";
            """;
        CollectionAssert.AreEquivalent(new[] { "Done", "Failed", "Idle" },
            UiStringScanner.ScanAssignments(code, new HashSet<string> { "StatusText" }).ToArray());
        CollectionAssert.AreEquivalent(new[] { "Battery" },
            UiStringScanner.ScanAssignments(code, new HashSet<string> { "Title" }).ToArray());
    }
}

/// <summary>Checks of the committed data files against the app's sources.</summary>
[TestClass]
public sealed class LocalizationDataTests
{
    private static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MissionPlanner.Next.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException("repository not found");
    }

    private static string DataDir => Path.Combine(Repo(), "src", "MissionPlanner.App", "Localization");

    [TestMethod]
    public void EveryLanguageHasAValidTable()
    {
        foreach (var language in LanguageCatalog.Languages.Skip(1))
        {
            var path = Path.Combine(DataDir, language.Code + ".json");
            Assert.IsTrue(File.Exists(path), path);
            using var stream = File.OpenRead(path);
            var table = TranslationTable.Load(stream);
            Assert.AreEqual(language.Code, table.Culture, path);
            Assert.IsTrue(table.Count > 100, $"{language.Code}: {table.Count} entries");
            foreach (var (english, translation) in table.Entries)
                Assert.IsTrue(LocalizationText.SameFormatItems(english, translation), $"{language.Code}: {english}");
        }
    }

    /// <summary>
    /// Every {l:Tr} text in the XAML is in the string list: translated in some language or listed in
    /// untranslated.txt. Fails when strings were added without re-running src/tools/LocalizationHarvest.
    /// </summary>
    [TestMethod]
    public void EveryXamlTextIsInTheStringList()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(Path.Combine(DataDir, "untranslated.txt")))
        {
            if (line.StartsWith('#') || line.Length == 0)
                continue;
            var text = line.Split('\t')[0].Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\r", "\r").Replace("\\\\", "\\");
            known.Add(LocalizationText.Key(text));
        }

        var tables = LanguageCatalog.Languages.Skip(1)
            .Select(l => { using var s = File.OpenRead(Path.Combine(DataDir, l.Code + ".json")); return TranslationTable.Load(s); })
            .ToList();

        var missing = new List<string>();
        var app = Path.Combine(Repo(), "src", "MissionPlanner.App");
        foreach (var file in Directory.GetFiles(app, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            foreach (var text in UiStringScanner.ScanXaml(File.ReadAllText(file)))
            {
                var parts = LocalizationText.Split(text);
                if (!LocalizationText.HasLetters(parts.Core))
                    continue;
                if (!known.Contains(parts.Core.ToLowerInvariant()) && tables.Any(t => !t.Contains(text)))
                    missing.Add($"{Path.GetFileName(file)}: {text}");
            }
        }

        Assert.AreEqual(0, missing.Count, "re-run src/tools/LocalizationHarvest; not in the string list:\n" + string.Join("\n", missing.Take(30)));
    }

    /// <summary>
    /// l:Tr / l:TrBind are one-way: on a value the user edits (TextBox.Text and the like) the typed
    /// value never reaches the view-model, so the field snaps back (the parameter editors once did).
    /// </summary>
    [TestMethod]
    public void EditableValuesAreNotTranslatedBindings()
    {
        var editable = new System.Text.RegularExpressions.Regex(
            "<(TextBox|AutoCompleteBox|NumericUpDown|ComboBox|CheckBox|ToggleSwitch|Slider)\\b[^>]*?\\s(Text|Value|SelectedItem|SelectedValue|IsChecked)=\"\\{l:Tr",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var bad = new List<string>();
        var app = Path.Combine(Repo(), "src", "MissionPlanner.App");
        foreach (var file in Directory.GetFiles(app, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            foreach (System.Text.RegularExpressions.Match m in editable.Matches(File.ReadAllText(file)))
                bad.Add($"{Path.GetFileName(file)}: {m.Value[..Math.Min(m.Value.Length, 120)]}");
        }

        Assert.AreEqual(0, bad.Count, "editable value bound through l:Tr/l:TrBind (one-way):\n" + string.Join("\n", bad));
    }
}
