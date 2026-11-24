using System.Net.Mail;
using MatContactHub.Data;
using MatContactHub.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Auth;

public sealed record SignInResult(User? User, string? Error);

/// <summary>A new account as the registration or an administrator fills it in (not yet checked). <see cref="Role"/> null: the first account is the administrator.</summary>
public sealed record NewAccount(string Email, string DisplayName, string Password, string? TimeZone = null, UserRole? Role = null, string? Language = null);

/// <summary>
/// Accounts: registering (the first account is the administrator) and signing in with a lockout after failed attempts. A visitor (<see cref="Actor.Anonymous"/>)
/// registers only while the registration is open and never chooses a role; an administrator creates accounts with any role.
/// </summary>
public static class AccountService
{
    private const int MaxFailures = 8;

    public static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || !email.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        return MailAddress.TryCreate(email, out var parsed) && parsed.Address == email.Trim();
    }

    public static async Task<bool> AnyUserAsync(AppDbContext db, CancellationToken ct = default) => await db.Users.IgnoreQueryFilters().AnyAsync(ct);

    /// <summary>May a visitor register: the first account of the installation, later only while an administrator keeps the registration open.</summary>
    public static async Task<bool> RegistrationOpenAsync(AppDbContext db, CancellationToken ct = default)
        => !await AnyUserAsync(db, ct) || (await SettingsStore.LoadAsync(db, ct)).RegistrationOpen;

    /// <summary>Creates an account. The very first account is an administrator. A visitor may only while the registration is open.</summary>
    public static async Task<(User? User, FieldError? Error)> RegisterAsync(AppDbContext db, Actor actor, PasswordService passwords, NewAccount input,
        CancellationToken ct = default)
    {
        if (!actor.IsAdmin && (input.Role is not null || !await RegistrationOpenAsync(db, ct)))
        {
            return (null, new FieldError(string.Empty, Text.T("Common.NotAllowed")));
        }

        var email = Normalize(input.Email);
        if (!IsEmail(email))
        {
            return (null, new FieldError("Email", Text.T("Account.Email.Invalid")));
        }

        if (PasswordService.Check(input.Password, email) is { } problem)
        {
            return (null, new FieldError("Password", problem));
        }

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
        {
            return (null, new FieldError("Email", Text.T("Account.Email.Taken")));
        }

        var first = !await AnyUserAsync(db, ct);
        var user = new User
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? email.Split('@')[0] : input.DisplayName.Trim(),
            PasswordHash = passwords.Hash(input.Password),
            Role = input.Role ?? (first ? UserRole.Admin : UserRole.User),
            TimeZone = Dates.IsZone(input.TimeZone) ? input.TimeZone! : "Europe/Berlin",
            Language = Languages.Normalize(input.Language),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await ProvisionAsync(db, user, ct);
        return (user, null);
    }

    /// <summary>What a new account needs after it was saved: it is its own creator (the audit column could not name it before it had an id).</summary>
    public static async Task ProvisionAsync(AppDbContext db, User user, CancellationToken ct = default)
    {
        user.CreateUserId ??= user.Id;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The sign-in with address and password (the visitor proves who they are, so there is no actor yet). Locks the account after 8 failures for 10 minutes.</summary>
    public static async Task<SignInResult> SignInAsync(AppDbContext db, PasswordService passwords, string email, string password, CancellationToken ct = default)
    {
        email = Normalize(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            passwords.SpendTime(password);
            return new SignInResult(null, Text.T("Account.Login.Wrong"));
        }

        var now = DateTime.UtcNow;
        if (user.LockoutUntil is { } until && until > now)
        {
            return new SignInResult(null, Text.T("Account.LockedOut"));
        }

        if (!passwords.Verify(user.PasswordHash, password))
        {
            user.FailedLogins++;
            if (user.FailedLogins >= MaxFailures)
            {
                user.LockoutUntil = now.AddMinutes(10);
                user.FailedLogins = 0;
            }

            await db.SaveChangesAsync(ct);
            return new SignInResult(null, Text.T("Account.Login.Wrong"));
        }

        if (!user.IsActive)
        {
            return new SignInResult(null, Text.T("Account.Login.Inactive"));
        }

        user.FailedLogins = 0;
        user.LockoutUntil = null;
        user.LastLoginDate = now;
        await db.SaveChangesAsync(ct);
        return new SignInResult(user, null);
    }
}
