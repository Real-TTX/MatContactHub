using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace MatContactHub.Pages.Account;

/// <summary>Registering. The first account of an installation is its administrator; later accounts only while the administrator keeps the registration open.</summary>
[EnableRateLimiting("signin")]
public sealed class RegisterModel(AppDbContext db, PasswordService passwords, SessionService sessions) : PageModel
{
    [BindProperty] public string DisplayName { get; set; } = string.Empty;

    [BindProperty] public string Email { get; set; } = string.Empty;

    [BindProperty] public string Password { get; set; } = string.Empty;

    [BindProperty] public string TimeZone { get; set; } = "Europe/Berlin";

    public bool First { get; private set; }

    public bool Open { get; private set; }

    private async Task LoadAsync()
    {
        First = !await AccountService.AnyUserAsync(db, HttpContext.RequestAborted);
        Open = await AccountService.RegistrationOpenAsync(db, HttpContext.RequestAborted);
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (!Open)
        {
            return Page();
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ModelState.AddModelError(nameof(DisplayName), Text.T("Account.Name.Required"));
            return Page();
        }

        var (user, error) = await AccountService.RegisterAsync(db, Actor.Anonymous, passwords,
            new NewAccount(Email, DisplayName, Password, TimeZone, Language: Languages.Current), HttpContext.RequestAborted);
        if (user is null)
        {
            ModelState.AddModelError(error!.Field, error.Message);
            return Page();
        }

        await sessions.SignInAsync(HttpContext, user, remember: true);
        TempData["Flash"] = Text.T("Account.Register.Welcome");
        return LocalRedirect("/Contacts");
    }
}
