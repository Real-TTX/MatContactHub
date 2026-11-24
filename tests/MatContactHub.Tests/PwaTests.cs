using System.Text.RegularExpressions;
using MatContactHub.Services;

namespace MatContactHub.Tests;

/// <summary>The app as a PWA: the status bar has the colour of the page in every theme and colour scheme.</summary>
public sealed partial class PwaTests
{
    [Theory]
    [InlineData("standard", ":root {", ":root[data-theme=\"dark\"] {")]
    [InlineData("paper", ":root[data-scheme=\"paper\"] {", ":root[data-scheme=\"paper\"][data-theme=\"dark\"] {")]
    [InlineData("contrast", ":root[data-scheme=\"contrast\"] {", ":root[data-scheme=\"contrast\"][data-theme=\"dark\"] {")]
    [InlineData("oled", ":root {", ":root[data-scheme=\"oled\"][data-theme=\"dark\"] {")]
    public void The_theme_colours_are_the_backgrounds_of_the_toolkit(string scheme, string light, string dark)
    {
        var css = File.ReadAllText(Path.Combine(FindRoot(), "src", "MatContactHub", "wwwroot", "matui", "matui.css"));
        Assert.Equal((BackgroundAfter(css, light), BackgroundAfter(css, dark)), Ui.Backgrounds(scheme));
    }

    [Fact]
    public void A_chosen_theme_has_one_colour_and_the_device_theme_two()
    {
        Assert.Equal("<meta name=\"theme-color\" content=\"#000000\" />", Ui.ThemeColor("dark", "oled").ToString());
        Assert.Equal("<meta name=\"theme-color\" content=\"#f3eee3\" />", Ui.ThemeColor("light", "paper").ToString());
        var both = Ui.ThemeColor(null, null).ToString()!;
        Assert.Contains("content=\"#f5f4f1\" media=\"(prefers-color-scheme: light)\"", both, StringComparison.Ordinal);
        Assert.Contains("content=\"#151514\" media=\"(prefers-color-scheme: dark)\"", both, StringComparison.Ordinal);
    }

    /// <summary>The first <c>--mat-bg</c> of the rule that starts with <paramref name="selector"/>.</summary>
    private static string BackgroundAfter(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, selector);
        return BackgroundRegex().Match(css, start).Groups["colour"].Value;
    }

    [GeneratedRegex("--mat-bg:\\s*(?<colour>#[0-9a-fA-F]{6})")]
    private static partial Regex BackgroundRegex();

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
