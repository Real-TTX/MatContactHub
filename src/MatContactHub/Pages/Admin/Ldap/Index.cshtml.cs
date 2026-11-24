using MatContactHub.Controls;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Ldap;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Admin.Ldap;

/// <summary>
/// The LDAP server (administrators) in five tabs: General (the settings and the state of the server), Certificate (the certificate in force, download, a new
/// self-signed one, an uploaded PFX), Accounts (the list of the LDAP accounts; the edit page is <c>/Admin/Ldap/Accounts/Edit</c>), Log (the last 500 binds and
/// searches) and Connection help (STARFACE, Yealink, any LDAPv3 client). Everything goes through the business layer of the module, which checks the actor again.
/// </summary>
public sealed class IndexModel(AppDbContext db, LdapServerHost server, LdapQueryLog log, IDataProtectionProvider protection, IConfiguration configuration) : AppPageModel
{
    public static readonly string[] Tabs = ["general", "certificate", "accounts", "log", "help"];

    private const int PageSize = 25;

    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "general";

    // ---- General

    [BindProperty] public bool Enabled { get; set; }

    [BindProperty] public bool StartTlsEnabled { get; set; }

    [BindProperty] public bool LdapsEnabled { get; set; }

    [BindProperty] public string BaseDn { get; set; } = LdapSettings.DefaultBaseDn;

    [BindProperty] public bool AllowAnonymous { get; set; }

    [BindProperty] public NumberFormat NumberFormat { get; set; }

    [BindProperty] public string DefaultCountry { get; set; } = "DE";

    [BindProperty] public int SizeLimit { get; set; } = LdapSettings.DefaultSizeLimit;

    public List<Choice> NumberFormats { get; } =
        [.. Enum.GetValues<NumberFormat>().Select(f => new Choice(f.ToString(), Text.T("Ldap.NumberFormat." + f), Text.T("Ldap.NumberFormat." + f + ".Example")))];

    public LdapServerStatus Status { get; private set; } = null!;

    // ---- Certificate

    [BindProperty] public IFormFile? Pfx { get; set; }

    [BindProperty] public string? PfxPassword { get; set; }

    public LdapCertificateInfo? Certificate { get; private set; }

    // ---- Accounts and Log

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<LdapAccountRow> Accounts { get; private set; } = [];

    public IReadOnlyList<LdapLogEntry> LogEntries { get; private set; } = [];

    public string Zone => Me.TimeZone;

    /// <summary>The account the examples of the help use (the first enabled one).</summary>
    public string HelpAccount { get; private set; } = "starface";

    /// <summary>The name the browser used for this server (the help: the address a phone uses is usually the same).</summary>
    public string ServerName => Request.Host.Host;

    private Actor Who => Actor.ForUser(Me);

    private string Folder => LdapCertificateService.FolderOf(configuration);

    public async Task<IActionResult> OnGetAsync()
    {
        Tab = Tabs.Contains(Tab) ? Tab : "general";
        if (Tab == "general")
        {
            var s = await LdapSettingsService.LoadAsync(db, HttpContext.RequestAborted);
            (Enabled, StartTlsEnabled, LdapsEnabled, BaseDn, AllowAnonymous, NumberFormat, DefaultCountry, SizeLimit) =
                (s.Enabled, s.StartTlsEnabled, s.LdapsEnabled, s.BaseDn, s.AllowAnonymous, s.NumberFormat, s.DefaultCountry, s.SizeLimit);
        }

        return await ShowAsync();
    }

    /// <summary>The settings of the tab General.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        Tab = "general";
        var current = await LdapSettingsService.LoadAsync(db, HttpContext.RequestAborted);
        var settings = new LdapSettings(Enabled, StartTlsEnabled, LdapsEnabled, BaseDn ?? string.Empty, AllowAnonymous, NumberFormat, DefaultCountry ?? string.Empty,
            SizeLimit, current.CertificateSource);
        var saved = await LdapSettingsService.SaveAsync(db, Who, settings, HttpContext.RequestAborted);
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            ModelState.AddModelError(string.Empty, saved.Error!);
            return await ShowAsync();
        }

        // the server takes the settings from its next snapshot (a moment): wait for it, so the state beside the form is the new one
        await server.WaitForVersionAsync(Services.Contacts.DirectoryChanges.Version, TimeSpan.FromSeconds(3), HttpContext.RequestAborted);
        Flash(Text.T("Common.Saved"));
        return LocalRedirect("/Admin/Ldap");
    }

    public async Task<IActionResult> OnPostUploadAsync()
    {
        Tab = "certificate";
        if (Pfx is null || Pfx.Length == 0)
        {
            ModelState.AddModelError(nameof(Pfx), Text.T("Ldap.Certificate.Upload.Missing"));
            return await ShowAsync();
        }

        if (Pfx.Length > LdapCertificateService.MaxUploadBytes)
        {
            ModelState.AddModelError(nameof(Pfx), Text.T("Ldap.Certificate.Upload.Size", LdapCertificateService.MaxUploadBytes / 1024));
            return await ShowAsync();
        }

        using var buffer = new MemoryStream();
        await Pfx.CopyToAsync(buffer, HttpContext.RequestAborted);
        var uploaded = await LdapCertificateService.UploadAsync(db, Who, protection, Folder, buffer.ToArray(), PfxPassword, HttpContext.RequestAborted);
        if (uploaded.NotAllowed)
        {
            return NotFound();
        }

        if (!uploaded.Ok)
        {
            ModelState.AddModelError(nameof(Pfx), uploaded.Error!);
            return await ShowAsync();
        }

        Flash(Text.T("Ldap.Certificate.Uploaded"));
        return LocalRedirect("/Admin/Ldap?tab=certificate");
    }

    public async Task<IActionResult> OnPostRegenerateAsync()
    {
        var done = await LdapCertificateService.RegenerateAsync(db, Who, protection, Folder, HttpContext.RequestAborted);
        if (done.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Ldap.Certificate.Regenerated"));
        return LocalRedirect("/Admin/Ldap?tab=certificate");
    }

    public async Task<IActionResult> OnPostSelfSignedAsync()
    {
        var done = await LdapCertificateService.UseSelfSignedAsync(db, Who, protection, Folder, HttpContext.RequestAborted);
        if (done.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Ldap.Certificate.SelfSignedInForce"));
        return LocalRedirect("/Admin/Ldap?tab=certificate");
    }

    /// <summary>The public certificate as a PEM file (for the trust store of a phone).</summary>
    public async Task<IActionResult> OnGetDownloadAsync()
    {
        var pem = await LdapCertificateService.ExportPemAsync(db, Who, protection, Folder, HttpContext.RequestAborted);
        return pem is null ? NotFound() : File(System.Text.Encoding.ASCII.GetBytes(pem), "application/x-x509-ca-cert", "matcontacthub-ldap.crt");
    }

    public IActionResult OnPostClearLog()
    {
        if (!log.Clear(Who))
        {
            return NotFound();
        }

        Flash(Text.T("Ldap.Log.Cleared"));
        return LocalRedirect("/Admin/Ldap?tab=log");
    }

    /// <summary>Loads what the tab shows.</summary>
    private async Task<IActionResult> ShowAsync()
    {
        Status = server.Status(Who) ?? throw new InvalidOperationException("Administrators only.");
        switch (Tab)
        {
            case "certificate":
                Certificate = await LdapCertificateService.DescribeAsync(db, Who, protection, Folder, HttpContext.RequestAborted);
                break;
            case "accounts":
                if (!await LoadAccountsAsync())
                {
                    return NotFound();
                }

                break;
            case "log":
                if (!LoadLog())
                {
                    return NotFound();
                }

                break;
            case "help":
                BaseDn = (await LdapSettingsService.LoadAsync(db, HttpContext.RequestAborted)).BaseDn;
                var accounts = await LdapAccountService.ListAsync(db, Who, new LdapAccountCriteria(null, "enabled", "name", false, 1, 1), HttpContext.RequestAborted);
                HelpAccount = accounts?.Items.FirstOrDefault()?.Name ?? "starface";
                break;
        }

        return Page();
    }

    private async Task<bool> LoadAccountsAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var state = ListQuery.Text(Request, "state");
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        var list = await LdapAccountService.ListAsync(db, Who, new LdapAccountCriteria(search, state, sort, desc, page, PageSize), HttpContext.RequestAborted);
        if (list is null)
        {
            return false;
        }

        Accounts = list.Items;
        Toolbar = new ToolbarModel
        {
            Id = "ldap-accounts",
            Action = "/Admin/Ldap",
            Label = Text.T("Ldap.Accounts"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Ldap.Accounts.Search"),
            Filters = [new FilterField("state", Text.T("Common.Status"), [new("enabled", Text.T("Ldap.Account.Enabled")), new("disabled", Text.T("Ldap.Account.Disabled"))], state)],
            SortOptions = [new("name", Text.T("Common.Name")), new("lastbind", Text.T("Ldap.Account.Col.LastBind")), new("created", Text.T("Ldap.Account.Sort.Created"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("Ldap.Accounts.Many"),
            NounOne = Text.T("Ldap.Accounts.One"),
            Keep = new Dictionary<string, string?> { ["tab"] = "accounts" },
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
        return true;
    }

    private bool LoadLog()
    {
        var search = ListQuery.Text(Request, "q");
        var outcome = ListQuery.Text(Request, "outcome");
        var page = ListQuery.Page(Request);
        var result = log.Read(Who, search, outcome, page, PageSize);
        if (result is null)
        {
            return false;
        }

        LogEntries = result.Items;
        Toolbar = new ToolbarModel
        {
            Id = "ldap-log",
            Action = "/Admin/Ldap",
            Label = Text.T("Ldap.Log"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Ldap.Log.Search"),
            Filters =
            [
                new FilterField("outcome", Text.T("Ldap.Log.Outcome"),
                    [.. new[] { "ok", "empty", "error", "bind" }.Select(o => new FilterOption(o, Text.T("Ldap.Log.Outcome." + o)))], outcome),
            ],
            Total = result.Total,
            Matching = result.Matching,
            Noun = Text.T("Ldap.Log.Many"),
            NounOne = Text.T("Ldap.Log.One"),
            Keep = new Dictionary<string, string?> { ["tab"] = "log" },
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = result.Matching, Url = Toolbar.UrlWithPage };
        return true;
    }
}
