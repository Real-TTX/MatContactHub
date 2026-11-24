using MatContactHub.Controls;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Ldap;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Admin.Ldap;

/// <summary>
/// The LDAP server (administrators; module A owns it; FOUNDATION PLACEHOLDER: the settings of the tab General; the tabs Certificate / Accounts / Log and the connection
/// help for STARFACE and Yealink follow).
/// </summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    [BindProperty] public bool Enabled { get; set; }

    [BindProperty] public bool StartTlsEnabled { get; set; }

    [BindProperty] public bool LdapsEnabled { get; set; }

    [BindProperty] public string BaseDn { get; set; } = LdapSettings.DefaultBaseDn;

    [BindProperty] public bool AllowAnonymous { get; set; }

    [BindProperty] public NumberFormat NumberFormat { get; set; }

    [BindProperty] public string DefaultCountry { get; set; } = "DE";

    [BindProperty] public int SizeLimit { get; set; } = LdapSettings.DefaultSizeLimit;

    public List<Choice> NumberFormats { get; } =
        [.. Enum.GetValues<NumberFormat>().Select(f => new Choice(f.ToString(), Text.T("Ldap.NumberFormat." + f)))];

    public async Task OnGetAsync()
    {
        var s = await LdapSettingsService.LoadAsync(db, HttpContext.RequestAborted);
        (Enabled, StartTlsEnabled, LdapsEnabled, BaseDn, AllowAnonymous, NumberFormat, DefaultCountry, SizeLimit) =
            (s.Enabled, s.StartTlsEnabled, s.LdapsEnabled, s.BaseDn, s.AllowAnonymous, s.NumberFormat, s.DefaultCountry, s.SizeLimit);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var current = await LdapSettingsService.LoadAsync(db, HttpContext.RequestAborted);
        var settings = new LdapSettings(Enabled, StartTlsEnabled, LdapsEnabled, BaseDn, AllowAnonymous, NumberFormat, DefaultCountry, SizeLimit, current.CertificateSource);
        var saved = await LdapSettingsService.SaveAsync(db, Actor.ForUser(Me), settings, HttpContext.RequestAborted);
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            ModelState.AddModelError(string.Empty, saved.Error!);
            return Page();
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect("/Admin/Ldap");
    }
}
