using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Accounts;
using MatContactHub.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Account;

/// <summary>A new password: the current one first. Every other session of the person ends.</summary>
public sealed class PasswordModel(AppDbContext db, PasswordService passwords) : AppPageModel
{
    [BindProperty] public string Current { get; set; } = string.Empty;

    [BindProperty] public string New { get; set; } = string.Empty;

    [BindProperty] public string Repeat { get; set; } = string.Empty;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await Profiles.ChangePasswordAsync(db, passwords, UserId, Current, New, Repeat, HttpContext.RequestAborted) is { } error)
        {
            ModelState.AddModelError(error.Field, error.Message);
            return Page();
        }

        await UserSessions.RevokeAllAsync(db, Actor.ForUser(Me), UserId, SessionService.CurrentSessionId(HttpContext), HttpContext.RequestAborted);
        Flash(Text.T("Password.Changed"));
        return LocalRedirect("/Account/Password");
    }
}
