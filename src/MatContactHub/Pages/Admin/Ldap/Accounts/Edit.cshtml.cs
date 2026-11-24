using System.Globalization;
using MatContactHub.Controls;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Ldap;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Admin.Ldap.Accounts;

/// <summary>
/// An LDAP account (administrators): the bind name, a description, whether it may bind, a new password (never shown again), and what it reads (every LDAP-enabled
/// address book or the chosen ones). Beside it the bind DN to enter in the client and the last bind. Save, Back, and after a gap Delete.
/// </summary>
public sealed class EditModel(AppDbContext db, PasswordService passwords) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string? Description { get; set; }

    [BindProperty] public bool Enabled { get; set; } = true;

    [BindProperty] public string? Password { get; set; }

    [BindProperty] public bool AllAddressBooks { get; set; } = true;

    /// <summary>The chosen address books, as the picker posts them (ids joined by commas).</summary>
    [BindProperty] public string? AddressBookIds { get; set; }

    public List<Choice> Books { get; private set; } = [];

    public DateTime? LastBindDate { get; private set; }

    public DateTime? CreateDate { get; private set; }

    public string BaseDn { get; private set; } = LdapSettings.DefaultBaseDn;

    /// <summary>The name as saved (the title, the bind DN), not what was typed into the form.</summary>
    public string SavedName { get; private set; } = string.Empty;

    public bool IsNew => Id is null;

    public string Zone => Me.TimeZone;

    private Actor Who => Actor.ForUser(Me);

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            if (await LdapAccountService.GetAsync(db, Who, id, HttpContext.RequestAborted) is not { } account)
            {
                return NotFound();
            }

            (Name, Description, Enabled, AllAddressBooks) = (account.Form.Name, account.Form.Description, account.Form.Enabled, account.Form.AllAddressBooks);
            AddressBookIds = string.Join(',', account.Form.AddressBookIds);
        }

        return await ShowAsync() ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ids = (AddressBookIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).Where(n => n > 0).ToList();
        var form = new LdapAccountForm(Name ?? string.Empty, Description, Enabled, AllAddressBooks, ids);
        var saved = await LdapAccountService.SaveAsync(db, Who, passwords, Id, form, Password, HttpContext.RequestAborted);
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            ModelState.AddModelError(string.Empty, saved.Error!);
            return await ShowAsync() ? Page() : NotFound();
        }

        Flash(Id is null ? Text.T("Ldap.Account.Created", form.Name.Trim().ToLowerInvariant()) : Text.T("Common.Saved"));
        return LocalRedirect("/Admin/Ldap?tab=accounts");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var account = Id is { } id ? await LdapAccountService.GetAsync(db, Who, id, HttpContext.RequestAborted) : null;
        if (account is null || !(await LdapAccountService.DeleteAsync(db, Who, account.Id, HttpContext.RequestAborted)).Ok)
        {
            return NotFound();
        }

        Flash(Text.T("Ldap.Account.Deleted", account.Form.Name));
        return LocalRedirect("/Admin/Ldap?tab=accounts");
    }

    /// <summary>What the page shows besides the form: the address books to choose, the base DN, the last bind. False when the actor may not see it.</summary>
    private async Task<bool> ShowAsync()
    {
        var books = await LdapAccountService.AddressBooksAsync(db, Who, HttpContext.RequestAborted);
        if (books is null)
        {
            return false;
        }

        Books = [.. books.Select(b => new Choice(b.Id.ToString(CultureInfo.InvariantCulture), b.Name, b.LdapEnabled ? "ou=" + b.LdapName : Text.T("Ldap.Account.NotInLdap")))];
        BaseDn = (await LdapSettingsService.LoadAsync(db, HttpContext.RequestAborted)).BaseDn;
        if (Id is { } id && await LdapAccountService.GetAsync(db, Who, id, HttpContext.RequestAborted) is { } account)
        {
            SavedName = account.Form.Name;
            LastBindDate = account.LastBindDate;
            CreateDate = account.CreateDate;
        }

        return true;
    }
}
