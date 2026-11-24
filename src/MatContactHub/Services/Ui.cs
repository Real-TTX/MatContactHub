using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace MatContactHub.Services;

/// <summary>Small helpers for the markup of the toolkit: icons, the logo, initials, sizes.</summary>
public static partial class Ui
{
    public const string AppId = "matcontacthub";
    public const string AppName = "MatContactHub";

    /// <summary>The accent of MatContactHub: a fuchsia (white text on it 5.7:1); equal to wwwroot/css/app.css and scripts/logo.js.</summary>
    public const string Accent = "#b81d97";

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColourRegex();

    /// <summary>An icon from the page's inline sprite (the toolkit's and the app's own).</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null, string? colour = null)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        var data = IsColour(colour) ? $" data-colour=\"{colour}\"" : string.Empty;
        return new HtmlString($"<svg class=\"{cls}\"{data} aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{name}\"></use></svg>");
    }

    public static bool IsColour(string? value) => value is not null && ColourRegex().IsMatch(value);

    /// <summary>The page background (<c>--mat-bg</c> of matui.css) of a colour scheme in light and in dark: the colour of the status bar and the window.</summary>
    public static (string Light, string Dark) Backgrounds(string? scheme) => scheme switch
    {
        "paper" => ("#f3eee3", "#1b1814"),
        "contrast" => ("#ffffff", "#000000"),
        "oled" => ("#f5f4f1", "#000000"),
        _ => ("#f5f4f1", "#151514"),
    };

    /// <summary>
    /// The <c>theme-color</c> of a page (status bar colour = app background): one colour when the person chose light or dark, else one per colour scheme of the
    /// device. js/pwa.js keeps it equal to the background when the theme or the scheme changes on the page.
    /// </summary>
    public static IHtmlContent ThemeColor(string? theme, string? scheme)
    {
        var (light, dark) = Backgrounds(scheme);
        return new HtmlString(theme switch
        {
            "light" => $"<meta name=\"theme-color\" content=\"{light}\" />",
            "dark" => $"<meta name=\"theme-color\" content=\"{dark}\" />",
            _ => $"<meta name=\"theme-color\" content=\"{light}\" media=\"(prefers-color-scheme: light)\" /><meta name=\"theme-color\" content=\"{dark}\" media=\"(prefers-color-scheme: dark)\" />",
        });
    }

    /// <summary>The logo of the menu and the sign-in: the mark of MatContactHub.</summary>
    public static IHtmlContent Logo(string size = "md")
        => new HtmlString($"<span class=\"mat-logo mat-logo--{size} md-logo\" role=\"img\" aria-label=\"{AppName}\"><svg class=\"mat-icon\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-matcontacthub\"></use></svg></span>");

    public static string Initials(string name)
    {
        var letters = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return letters.Length == 0 ? "?" : letters;
    }

    /// <summary>A size for people: 812 B, 14.2 KB, 3.1 MB.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var culture = CultureInfo.CurrentUICulture;
        return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.0" : "0", culture) + " " + units[unit];
    }
}
