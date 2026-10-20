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
        var keys = Directory.EnumerateFiles(setup, "*.cs").Where(f => !f.EndsWith("Chinese.cs", StringComparison.Ordinal))
            .SelectMany(f => HostT().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            // The verbs, said through L.T(verb).
            .Concat(["Install", "Update", "Reinstall"])
            .Distinct().ToList();

        Assert.True(keys.Count > 80, $"only {keys.Count} keys found: the scan is not reading the sources");
        var missing = keys.Where(k => !table.Contains(k)).ToList();
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("en", "en")]
    [InlineData("zh-Hans", "zh-Hans")]
    public void The_setting_names_the_language_and_Windows_decides_the_rest(string? setting, string code)
    {
        var before = L.Current;
        try
        {
            L.Current = L.Resolve(setting);
            // This machine's own display language is not Chinese, so "follow Windows" is English here.
            if (setting is null && System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return;
            Assert.Equal(code, L.Code);
        }
        finally { L.Current = before; }
    }

    [Fact]
    public void Chinese_puts_values_where_its_grammar_wants_them()
    {
        var before = L.Current;
        try
        {
            L.Current = Language.Chinese;
            Assert.Equal("已连接", L.T("Connected"));
            Assert.Equal("正在经由 jp 分流 Zenless。如果它已在运行，可能需要重新连接。",
                L.T("Routing {0} through {1}. If it is running already, it may need to reconnect.", "Zenless", "jp"));
            Assert.Equal("已尝试 3 次。", L.Count(3, "1 attempt so far.", "{0} attempts so far."));
            Assert.Equal("连接中断。上次尝试：timeout", L.Join("连接中断。", L.T("Last attempt: {0}", "timeout")));
            Assert.Equal("2026年10月20日", L.Date(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)));
            // What has no translation yet shows in English rather than not at all.
            Assert.Equal("Something new", L.T("Something new"));
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
            var menu = TrayMenu.Build([], [], "");
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

    // The editors' helpers: text('field', 'Label', 'unit') and check('field', 'Label'), whose words are said through t().
    [GeneratedRegex("""\b(?:text|check)\('[a-zA-Z]+', '([^']*)'(?:, '([^']*)')?\)""")]
    private static partial Regex PageHelper();

    // The tabs: ['apps', 'Apps'], said through t(label).
    [GeneratedRegex("""\['(?:apps|network|profiles|settings)', '([^']*)'\]""")]
    private static partial Regex PageTabs();

    // A tag's text that holds a letter: >seconds<, not >{t('seconds')}< nor the space between two tags. Code between a
    // generic's brackets (useState<Page>(null); … useRef<) has a bracket, an equals sign or a line break; text has none.
    [GeneratedRegex(""">[ \t]*([^<>{}();=&|\s][^<>{}();=&|\r\n]*?[A-Za-z][^<>{}();=&|\r\n]*?)[ \t]*<""")]
    private static partial Regex PageBareText();

    [GeneratedRegex("""^\s*'((?:[^'\\]|\\.)*)':""", RegexOptions.Multiline)]
    private static partial Regex PageTable();

    [GeneratedRegex("""^\s*\["((?:[^"\\]|\\.)*)"\] =""", RegexOptions.Multiline)]
    private static partial Regex SetupTable();
}

[CollectionDefinition(nameof(OneLanguage), DisableParallelization = true)]
public sealed class OneLanguage;
