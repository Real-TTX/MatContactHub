using System.Text.RegularExpressions;

namespace MatContactHub.Tests;

/// <summary>
/// The rules of the views that a reader of the markup can check: every cell of a table names its column (the cards of a phone show it), and a file field or
/// a group of radios is the one control <c>&lt;md-field&gt;</c>, never a copy written by hand.
/// </summary>
public sealed partial class ViewConventionTests
{
    /// <summary>
    /// Radios that are not a field: the radio of a row that picks the row (the contact to keep of a group of duplicates, one radio per row, the column names
    /// the group), and the options of the menus of the toolbar (the one code base of the toolbar: filter and sort).
    /// </summary>
    private static readonly string[] NotFieldRadios = [Path.Combine("Pages", "Duplicates", "Index.cshtml"), Path.Combine("Pages", "Shared", "_Toolbar.cshtml")];

    [Fact]
    public void Every_cell_has_a_data_label()
    {
        var missing = Views()
            .SelectMany(v => CellRegex().Matches(v.Text).Where(m => !m.Value.Contains("data-label=", StringComparison.Ordinal)).Select(m => $"{v.Path}: {m.Value}"))
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void File_fields_are_md_fields()
    {
        var manual = Views().Where(v => v.Text.Contains("type=\"file\"", StringComparison.Ordinal) || v.Text.Contains("data-upload", StringComparison.Ordinal)).Select(v => v.Path).ToList();
        Assert.Empty(manual);
    }

    [Fact]
    public void Radios_are_md_fields()
    {
        var manual = Views()
            .Where(v => v.Text.Contains("type=\"radio\"", StringComparison.Ordinal) && !NotFieldRadios.Any(r => v.Path.EndsWith(r, StringComparison.Ordinal)))
            .Select(v => v.Path)
            .ToList();
        Assert.Empty(manual);
    }

    private static IEnumerable<(string Path, string Text)> Views()
    {
        var pages = Path.Combine(FindRoot(), "src", "MatContactHub", "Pages");
        return Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories).Select(f => (f, File.ReadAllText(f)));
    }

    [GeneratedRegex("<td(\\s[^>]*)?>")]
    private static partial Regex CellRegex();

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
