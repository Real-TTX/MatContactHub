using System.Text.RegularExpressions;
using MatContactHub.Localization;

namespace MatContactHub.Tests;

/// <summary>The catalog of the interface texts: complete in English and German, one place per key, and every key the code asks for is there.</summary>
public sealed partial class TextCatalogTests
{
    [Fact]
    public void Every_key_has_english_and_german()
    {
        var missing = TextCatalog.Files()
            .Where(f => Languages.Codes.Any(code => !f.Texts.TryGetValue(code, out var text) || string.IsNullOrWhiteSpace(text)))
            .Select(f => $"{f.File}: {f.Key}")
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_key_lives_in_one_file()
    {
        var twice = TextCatalog.Files().GroupBy(f => f.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(twice);
    }

    [Fact]
    public void Placeholders_are_the_same_in_both_languages()
    {
        var wrong = TextCatalog.Files()
            .Where(f => Placeholders(f.Texts["en"]) != Placeholders(f.Texts["de"]))
            .Select(f => f.Key)
            .ToList();
        Assert.Empty(wrong);
    }

    [Fact]
    public void Every_key_of_the_code_is_in_the_catalog()
    {
        var source = Path.Combine(FindRoot(), "src", "MatContactHub");
        var files = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".cshtml", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains("matui", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in KeyRegex().Matches(text))
            {
                used.Add(m.Groups["key"].Value);
            }
        }

        var missing = used.Where(k => !k.EndsWith('.') && !TextCatalog.Entries.ContainsKey(k)).ToList();
        Assert.Empty(missing);
    }

    /// <summary>The scripts ask for their texts without the prefix: t('Grid.Selected') is the key Js.Grid.Selected.</summary>
    [Fact]
    public void Every_key_of_the_scripts_is_in_the_catalog()
    {
        var folder = Path.Combine(FindRoot(), "src", "MatContactHub", "wwwroot", "js");
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.js", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match m in EditorKeyRegex().Matches(File.ReadAllText(file)))
            {
                var key = m.Groups["key"].Value;
                used.Add(key.StartsWith("Js.", StringComparison.Ordinal) ? key : "Js." + key);
            }
        }

        Assert.NotEmpty(used);
        var missing = used.Where(k => !k.EndsWith('.') && !TextCatalog.Entries.ContainsKey(k)).ToList();
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("Errors.NotFound.Title")]
    [InlineData("System.Tab.general")]
    [InlineData("System.Tab.directory")]
    [InlineData("About.Channel.Nightly")]
    public void Keys_built_at_run_time_exist(string key) => Assert.True(TextCatalog.Entries.ContainsKey(key), key);

    /// <summary>The keys made of a name and a value of an enum or a list: every value has its text.</summary>
    [Fact]
    public void Keys_of_every_value_exist()
    {
        var keys = Services.ActivityLog.Actions.Select(a => "Activity.Action." + a)
            .Concat(Enum.GetNames<Data.DataSourceKind>().Select(k => "Sources.Kind." + k))
            .Concat(Enum.GetNames<Data.SyncState>().Select(k => "Sources.State." + k))
            .Concat(Enum.GetNames<Data.NumberFormat>().Select(k => "Ldap.NumberFormat." + k))
            .Concat(Enum.GetNames<Data.AddressBookRole>().Select(k => "AddressBooks.Role." + k))
            .Concat(Enum.GetNames<Services.Contacts.DuplicateReason>().Select(k => "Duplicates.Reason." + k));
        Assert.All(keys, k => Assert.True(TextCatalog.Entries.ContainsKey(k), k));
    }

    [Fact]
    public void The_language_changes_the_text()
    {
        Assert.Equal("Address books", Languages.In("en", () => Text.T("AddressBooks.Title")));
        Assert.Equal("Adressbücher", Languages.In("de", () => Text.T("AddressBooks.Title")));
        Assert.Equal("Mindestens 10 Zeichen.", Languages.In("de", () => Text.T("Account.Password.Hint", 10)));
    }

    private static string Placeholders(string text)
        => string.Join(',', PlaceholderRegex().Matches(text).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal));

    // L["Key"], L["Key", ...], Text.T("Key"), t('Js.Key'), and the keys written as plain strings in a ternary (L[x ? "A.B" : "C.D"]): every quoted Area.Thing of the catalog's areas
    [GeneratedRegex("""["'](?<key>(Common|Shell|Settings|Password|Admin|Account|Roles|About|Users|System|Lists|Landing|Errors|Connections|Activity|Data|Search|Contacts|AddressBooks|Duplicates|Sources|Ldap|Js)\.[A-Za-z0-9.]+)["']""")]
    private static partial Regex KeyRegex();

    // the quoted keys of the areas of the scripts: t('Drive.Close'), t(x ? 'Drive.Moved' : 'Drive.Copied')
    [GeneratedRegex("""'(?<key>(Js\.)?(Offline|Common)\.[A-Za-z0-9.-]+)'""")]
    private static partial Regex EditorKeyRegex();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderRegex();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatContactHub.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MatContactHub.slnx not found.");
    }
}
