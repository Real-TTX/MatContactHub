using System.Globalization;
using System.Net;
using System.Text;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Contacts;

/// <summary>Small helpers of the views of the contacts (module C): links for numbers and addresses, the avatar in the colour of the address book, a vCard download.</summary>
public static class ContactView
{
    /// <summary>The <c>tel:</c> address of a number: <c>+</c> and digits only (<c>+49 (30) 1234-5</c> → <c>tel:+493012345</c>); null for a number without digits.</summary>
    public static string? Tel(string? number)
    {
        var digits = PhoneNormalizer.Digits(number);
        if (digits.Length == 0)
        {
            return null;
        }

        return "tel:" + (number!.TrimStart().StartsWith('+') ? "+" : string.Empty) + digits;
    }

    /// <summary>A number as a <c>tel:</c> link (empty for none).</summary>
    public static IHtmlContent PhoneLink(string? number, string? css = null)
    {
        if (Tel(number) is not { } href)
        {
            return HtmlString.Empty;
        }

        var cls = css is null ? string.Empty : $" class=\"{css}\"";
        return new HtmlString($"<a{cls} href=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(Readable(number))}</a>");
    }

    /// <summary>
    /// A number to read: one stored in E.164 (what the transform Phone of a data source writes, <c>+49307654321</c>) is shown in groups (<c>+49 30 7654321</c>);
    /// any other is shown as typed.
    /// </summary>
    public static string? Readable(string? number)
    {
        var trimmed = number?.Trim();
        var e164 = trimmed is { Length: > 1 } && trimmed[0] == '+' && !trimmed.AsSpan(1).ContainsAnyExcept("0123456789");
        if (!e164)
        {
            return number;
        }

        try
        {
            var util = PhoneNumbers.PhoneNumberUtil.GetInstance();
            return util.Format(util.Parse(trimmed!, null), PhoneNumbers.PhoneNumberFormat.INTERNATIONAL);
        }
        catch (PhoneNumbers.NumberParseException)
        {
            return number;
        }
    }

    /// <summary>An e-mail address as a <c>mailto:</c> link (empty for none).</summary>
    public static IHtmlContent MailLink(string? mail, string? css = null)
    {
        if (string.IsNullOrWhiteSpace(mail))
        {
            return HtmlString.Empty;
        }

        var cls = css is null ? string.Empty : $" class=\"{css}\"";
        return new HtmlString($"<a{cls} href=\"mailto:{WebUtility.HtmlEncode(Uri.EscapeDataString(mail.Trim()).Replace("%40", "@", StringComparison.Ordinal))}\">{WebUtility.HtmlEncode(mail)}</a>");
    }

    /// <summary>A website as a link that opens a new tab (http or https only; an address without a scheme gets https).</summary>
    public static IHtmlContent WebLink(string? website)
    {
        if (string.IsNullOrWhiteSpace(website))
        {
            return HtmlString.Empty;
        }

        var url = website.Contains("://", StringComparison.Ordinal) ? website.Trim() : "https://" + website.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new HtmlString(WebUtility.HtmlEncode(website));
        }

        return new HtmlString($"<a href=\"{WebUtility.HtmlEncode(uri.AbsoluteUri)}\" target=\"_blank\" rel=\"noopener noreferrer\">{WebUtility.HtmlEncode(website)}</a>");
    }

    /// <summary>The round avatar with the initials, tinted with the colour of the address book (<c>data-colour</c>; the toolkit writes it into <c>--mat-swatch</c>).</summary>
    public static IHtmlContent Avatar(string name, string? colour, string size = "sm")
    {
        var data = Ui.IsColour(colour) ? $" data-colour=\"{colour}\"" : string.Empty;
        var cls = size == "lg" ? "mat-avatar mat-avatar--lg md-avatar" : "mat-avatar mat-avatar--sm md-avatar";
        return new HtmlString($"<span class=\"{cls}\"{data} aria-hidden=\"true\">{WebUtility.HtmlEncode(Ui.Initials(name))}</span>");
    }

    /// <summary>A vCard file to download: <c>Name.vcf</c> for one contact, <c>contacts-2026-11-24.vcf</c> for several.</summary>
    public static FileContentResult Download(VCardExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        var name = export.SingleName is { Length: > 0 } single ? FileName(single) : "contacts-" + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new FileContentResult(Encoding.UTF8.GetBytes(export.Text), "text/vcard; charset=utf-8") { FileDownloadName = name + ".vcf" };
    }

    private static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Select(c => invalid.Contains(c) || c is '"' or '\'' ? '_' : c)]).Trim();
        return clean.Length == 0 ? "contact" : clean.Length > 80 ? clean[..80] : clean;
    }
}
