using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Mail;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Admin;

/// <summary>
/// The settings of the installation in two tabs: General (registration, public address) and Directory (the default country of the telephone numbers, how long the
/// history of the sync runs is kept). Beside it what is in the installation. The same values as the platform API; an address <c>MAT_PUBLIC_URL</c> sets is shown
/// read-only. The LDAP server has a page of its own (<c>/Admin/Ldap</c>).
/// </summary>
public sealed class SystemModel(InstanceService instance, MailService mail, AppDbContext db) : AppPageModel
{
    public static readonly string[] Tabs = ["general", "directory"];

    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "general";

    [BindProperty] public bool RegistrationOpen { get; set; }

    [BindProperty] public string PublicUrl { get; set; } = string.Empty;

    [BindProperty] public string DefaultCountry { get; set; } = "DE";

    [BindProperty] public int SyncRunDays { get; set; } = 90;

    /// <summary><c>MAT_PUBLIC_URL</c> sets the public address.</summary>
    public bool PublicUrlFixed { get; private set; }

    public InstanceSnapshot Counts { get; private set; } = null!;

    public SystemSettings Settings { get; private set; } = null!;

    public string Database { get; private set; } = string.Empty;

    public string DataDirectory => instance.DataDirectory;

    public string Zone => Me.TimeZone;

    /// <summary>Where the mail goes (MatSMTP), or null when the installation sends none.</summary>
    public string? MailText => mail.Describe(Languages.Current);

    public async Task OnGetAsync()
    {
        Tab = Tabs.Contains(Tab) ? Tab : "general";
        var s = await instance.SettingsAsync();
        RegistrationOpen = s.RegistrationOpen;
        PublicUrl = s.PublicUrl ?? string.Empty;
        DefaultCountry = s.DefaultCountry;
        SyncRunDays = s.SyncRunDays;
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Tab = Tabs.Contains(Tab) ? Tab : "general";

        // each tab sends its own fields; the others stay as they are
        var change = Tab switch
        {
            "directory" => new SettingsChange(DefaultCountry: DefaultCountry, SyncRunDays: SyncRunDays),
            _ => new SettingsChange(RegistrationOpen: RegistrationOpen, PublicUrl: PublicUrl),
        };
        if (await instance.SaveSettingsAsync(change) is { } problem)
        {
            ModelState.AddModelError(problem.Field, problem.Message);
            await LoadAsync();
            return Page();
        }

        await ActivityLog.AddAsync(db, Actor.ForUser(Me), "setting", Text.T("System.Tab." + Tab), null, null, HttpContext.RequestAborted);
        Flash(Text.T("Common.Saved"));
        return LocalRedirect("/Admin/System?tab=" + Tab);
    }

    public async Task<IActionResult> OnPostCleanUpAsync()
    {
        var result = await instance.CleanUpAsync(HttpContext.RequestAborted);
        Flash(Text.T("System.CleanedUp", result.Sessions, result.SyncRuns));
        return LocalRedirect("/Admin/System");
    }

    private async Task LoadAsync()
    {
        Counts = await instance.SnapshotAsync();
        Settings = await instance.SettingsAsync();
        Database = instance.DatabaseName();
        PublicUrlFixed = Settings.PublicUrlFixed;
        if (PublicUrlFixed)
        {
            PublicUrl = Settings.PublicUrl ?? string.Empty;
        }
    }
}
