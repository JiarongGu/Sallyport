using System.Text.RegularExpressions;

namespace Sallyport.Client.Tests;

/// <summary>Every word the client shows has its Chinese (L, Chinese.cs; the page's i18n.ts): read from the sources, so a
/// sentence added in English without its translation fails here rather than showing English to a Chinese reader.</summary>
/// <remarks>The language is the process's (L.Current): a test that changes it runs alone, or a test beside it reading
/// English words would read Chinese ones.</remarks>
[Collection(nameof(OneLanguage))]
public sealed partial class LanguageTests
{
    private static string Repository()
    {
        var folder = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(folder, "Sallyport.slnx"))) folder = Path.GetDirectoryName(folder) ?? throw new DirectoryNotFoundException("no repository above the tests");
        return folder;
    }

    private static string Client => Path.Combine(Repository(), "apps", "Sallyport.Client");

    [Fact]
    public void Every_host_sentence_has_its_Chinese()
    {
        var keys = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Client, "*.cs"))
        {
            var source = File.ReadAllText(file);
            keys.AddRange(HostT().Matches(source).Select(m => m.Groups[1].Value));
            foreach (Match m in HostCount().Matches(source)) keys.AddRange([m.Groups[1].Value, m.Groups[2].Value]);
        }

        // A key is the C# literal as written: the table holds the same text once compiled, escapes undone.
        var missing = keys.Select(k => Regex.Unescape(k)).Distinct().Where(k => !Chinese.Words.ContainsKey(k)).ToList();

        Assert.True(keys.Count > 300, $"only {keys.Count} keys found: the scan is not reading the sources");
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_page_word_has_its_Chinese()
    {
        var web = Path.Combine(Client, "web", "src");
        var keys = new List<string>();
        foreach (var file in Directory.EnumerateFiles(web, "*.ts*", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            keys.AddRange(PageT().Matches(source).Select(m => m.Groups[1].Value));
            // t(cond ? 'one' : 'other'): both are said, and neither follows t( itself.
            keys.AddRange(PageTChoice().Matches(source).SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }));
            keys.AddRange(PageHelper().Matches(source).SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }).Where(v => v.Length > 0));
            keys.AddRange(PageTabs().Matches(source).Select(m => m.Groups[1].Value));
        }
        var table = PageTable().Matches(File.ReadAllText(Path.Combine(web, "i18n.ts"))).Select(m => m.Groups[1].Value).ToHashSet();

        var missing = keys.Distinct().Where(k => !table.Contains(k)).ToList();

        Assert.True(keys.Count > 80, $"only {keys.Count} keys found: the scan is not reading the sources");
        Assert.Empty(missing);
    }

    [Fact]
    public void No_page_word_is_written_outside_t()
    {
        // Text between tags is shown as written: an editor's unit once said "seconds" to a Chinese reader that way.
        var web = Path.Combine(Client, "web", "src");
        var bare = Directory.EnumerateFiles(web, "*.tsx", SearchOption.AllDirectories)
            .SelectMany(f => PageBareText().Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: {m.Groups[1].Value}"))
            // The name is the same in every language.
            .Where(text => !text.EndsWith(": Sallyport", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(bare);
    }

    [Fact]
    public void Every_setup_sentence_has_its_Chinese()
    {
        var setup = Path.Combine(Repository(), "apps", "Sallyport.Setup");
        // Setup's table is its own assembly's: read from its source, as its keys are.
        var table = SetupTable().Matches(File.ReadAllText(Path.Combine(setup, "Chinese.cs"))).Select(m => m.Groups[1].Value).ToHashSet();
        // The uninstaller speaks with setup's words (D32): its own sources are read with setup's.
        var uninstaller = Path.Combine(Repository(), "apps", "Sallyport.Uninstall");
        var keys = Directory.EnumerateFiles(setup, "*.cs").Concat(Directory.EnumerateFiles(uninstaller, "*.cs"))
            .Where(f => !f.EndsWith("Chinese.cs", StringComparison.Ordinal))
            .SelectMany(f => HostT().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            // The verbs, said through L.T(verb).
            .Concat(["Install", "Update", "Reinstall"])
            .Distinct().ToList();

        Assert.True(keys.Count > 80, $"only {keys.Count} keys found: the scan is not reading the sources");
        var missing = keys.Where(k => !table.Contains(k)).ToList();
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData(null, "en-US", "en")]
    [InlineData(null, "zh-CN", "zh-Hans")]
    [InlineData("en", "zh-CN", "en")]
    [InlineData("zh-Hans", "en-US", "zh-Hans")]
    public void The_setting_names_the_language_and_Windows_decides_the_rest(string? setting, string windows, string code)
    {
        // Windows' language as the test says, whatever this machine's is (the owner's is Chinese): checked either way.
        var before = (L.Current, System.Globalization.CultureInfo.CurrentUICulture);
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(windows);
            L.Current = L.Resolve(setting);
            Assert.Equal(code, L.Code);
        }
        finally { (L.Current, System.Globalization.CultureInfo.CurrentUICulture) = before; }
    }

    [Fact]
    public void Chinese_puts_values_where_its_grammar_wants_them()
    {
        var before = L.Current;
        try
        {
            L.Current = Language.Chinese;
            Assert.Equal("已连接", L.T("Connected"));
            Assert.Equal("已添加 Zenless。“使用 VPN”已打开：只要 jp 已连接，它就会经由 jp。",
                L.T("Added {0}. Use VPN is on: it goes through {1} whenever {1} is connected.", "Zenless", "jp"));
            Assert.Equal("Zenless、Edge", L.List(["Zenless", "Edge"]));
            Assert.Equal("已尝试 3 次。", L.Count(3, "1 attempt so far.", "{0} attempts so far."));
            Assert.Equal("连接中断。上次尝试：timeout", L.Join("连接中断。", L.T("Last attempt: {0}", "timeout")));
            Assert.Equal("2026年10月20日", L.Date(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)));
            // What has no translation yet shows in English rather than not at all.
            Assert.Equal("Something new", L.T("Something new"));
        }
        finally { L.Current = before; }
    }

    [Fact]
    public void Every_reason_the_router_service_gives_is_listed_with_its_Chinese()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(Repository(), "apps", "Sallyport.Service"), "*.cs")
            .Append(Path.Combine(Repository(), "src", "Sallyport.Windows", "Service", "RouterClient.cs"));
        var given = sources.SelectMany(f => ServiceReason().Matches(File.ReadAllText(f)))
            .Select(m => RouterWords.Sentence(Interpolated().Replace(Regex.Unescape(m.Groups["r"].Value), "{0}")))
            .Distinct().ToList();

        var unlisted = given.Where(r => !RouterWords.Reasons.Contains(r)).ToList();
        var untranslated = RouterWords.Reasons.Where(r => !Chinese.Words.ContainsKey(r)).ToList();

        Assert.True(given.Count > 20, $"only {given.Count} reasons found: the scan is not reading the sources");
        Assert.Empty(unlisted);
        Assert.Empty(untranslated);
    }

    [Fact]
    public void A_reason_from_the_router_service_is_shown_in_the_windows_language()
    {
        var before = L.Current;
        try
        {
            L.Current = Language.Chinese;
            Assert.Equal("应答的程序不是以系统身份运行的。", RouterWords.Say("what answered on the router's pipe is not running as the system"));
            Assert.Equal("分流器已停止（退出代码 -1）。", RouterWords.Say("the router stopped (exit -1)"));
            Assert.Equal("Something unforeseen.", RouterWords.Say("something unforeseen"));
        }
        finally { L.Current = before; }
    }

    [Fact]
    public void The_tray_menu_is_in_chinese_too()
    {
        var before = L.Current;
        try
        {
            L.Current = Language.Chinese;
            var menu = TrayMenu.Build([], "");
            Assert.Equal("打开 Sallyport", menu[0].Text);
            Assert.All(menu.Where(e => e.Kind is TrayEntryKind.Command or TrayEntryKind.Note), e => Assert.Matches(@"\p{IsCJKUnifiedIdeographs}", e.Text));
        }
        finally { L.Current = before; }
    }

    [GeneratedRegex("""L\.T\(\s*"((?:[^"\\]|\\.)*)" """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex HostT();

    [GeneratedRegex("""L\.Count\([^,()]*(?:\([^()]*\))?[^,()]*,\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)" """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex HostCount();

    [GeneratedRegex("""\bt\(\s*'((?:[^'\\]|\\.)*)'""")]
    private static partial Regex PageT();

    [GeneratedRegex("""\bt\(\s*(?:[^'()?]|'[^']*')*\?\s*'((?:[^'\\]|\\.)*)'\s*:\s*'((?:[^'\\]|\\.)*)'\s*\)""")]
    private static partial Regex PageTChoice();

    // The editors' helpers: text('field', 'Label', 'unit') and check('field', 'Label'), whose words are said through t().
    [GeneratedRegex("""\b(?:text|check)\('[a-zA-Z]+', '([^']*)'(?:, '([^']*)')?\)""")]
    private static partial Regex PageHelper();

    // The menu's pages: ['apps', 'Apps', 'apps'], said through t(label).
    [GeneratedRegex("""\['(?:apps|network|profiles|settings)', '([^']*)'""")]
    private static partial Regex PageTabs();

    // A tag's text that holds a letter: >seconds<, not >{t('seconds')}< nor the space between two tags. Code between a
    // generic's brackets (useState<Page>(null); … useRef<) has a bracket, an equals sign or a line break; text has none.
    // An arrow's > (=> rates.length < 2) closes no tag.
    [GeneratedRegex("""(?<![=\-])>[ \t]*([^<>{}();=&|\s][^<>{}();=&|\r\n]*?[A-Za-z][^<>{}();=&|\r\n]*?)[ \t]*<""")]
    private static partial Regex PageBareText();

    [GeneratedRegex("""^\s*'((?:[^'\\]|\\.)*)':""", RegexOptions.Multiline)]
    private static partial Regex PageTable();

    [GeneratedRegex("""^\s*\["((?:[^"\\]|\\.)*)"\] =""", RegexOptions.Multiline)]
    private static partial Regex SetupTable();

    // The ways the service and the router client give a reason: Rejected("…"), End("…"), RouterRefused("…"), a worker's
    // "error …" line, a driver's (false, "…"), each maybe interpolated; and a line of the ternary of who may not ask
    // (Refusal), which ends at its sentence.
    [GeneratedRegex("""(?:\b(?:Rejected|End|RouterRefused)\(\$?"|Say\(\$?"error |\(false, \$?")(?<r>(?:[^"\\]|\\.)*[a-z](?:[^"\\]|\\.)*)"(?=[);])|(?<!\?)[?:][ \t]+"(?<r>[^"\\{}]*[a-z][^"\\{}]*)"[ \t]*\r?$""", RegexOptions.Multiline)]
    private static partial Regex ServiceReason();

    [GeneratedRegex("""\{[^{}]*(?:\([^()]*\))?[^{}]*\}""")]
    private static partial Regex Interpolated();
}

[CollectionDefinition(nameof(OneLanguage), DisableParallelization = true)]
public sealed class OneLanguage;
