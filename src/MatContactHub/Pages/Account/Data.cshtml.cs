using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Accounts;
using MatContactHub.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Account;

/// <summary>The data of the person (GDPR): download everything as JSON (Art. 15/20), and delete the account for good (Art. 17) after the password.</summary>
public sealed class DataModel(AppDbContext db, PasswordService passwords, SessionService sessions) : AppPageModel
{
    [BindProperty] public string Password { get; set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public async Task OnGetAsync() => Email = (await Profiles.GetAsync(db, UserId, HttpContext.RequestAborted))?.Email ?? string.Empty;

    /// <summary>The export: a file to keep or to take to another app.</summary>
    public async Task<IActionResult> OnGetExportAsync()
    {
        if (await AccountDataService.ExportAsync(db, Actor.ForUser(Me), UserId, HttpContext.RequestAborted) is not { } json)
        {
            return NotFound();
        }

        return File(json, "application/json", $"matcontacthub-{Dates.Iso(Today)}.json");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (await Profiles.CheckPasswordAsync(db, passwords, UserId, Password, HttpContext.RequestAborted) is not { } check)
        {
            return NotFound();
        }

        Email = check.Email;
        if (!check.Correct)
        {
            ModelState.AddModelError(nameof(Password), Text.T("Data.Delete.WrongPassword"));
            return Page();
        }

        var erased = await AccountDataService.EraseAsync(db, Actor.ForUser(Me), UserId, HttpContext.RequestAborted);
        if (erased.NotAllowed)
        {
            return NotFound();
        }

        if (!erased.Ok)
        {
            ModelState.AddModelError(string.Empty, erased.Error!);
            return Page();
        }

        await sessions.SignOutAsync(HttpContext);
        TempData["Flash"] = Text.T("Data.Delete.Done");
        return LocalRedirect("/Account/Login");
    }
}
