using System.Text.RegularExpressions;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services.Contacts;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Ldap;

/// <summary>A line of the list of LDAP accounts.</summary>
public sealed record LdapAccountRow(long Id, string Name, string? Description, bool Enabled, bool AllAddressBooks, int AddressBooks, DateTime? LastBindDate,
    DateTime CreateDate);

/// <summary>What the list asks for: a search, the state (<c>enabled</c>, <c>disabled</c>), the sort (<c>name</c>, <c>lastbind</c>, <c>created</c>), a page.</summary>
public sealed record LdapAccountCriteria(string? Search, string? State, string Sort, bool Descending, int Page, int PageSize);

public sealed record LdapAccountList(IReadOnlyList<LdapAccountRow> Items, int Total, int Matching);

/// <summary>What the edit page of an account shows and sends (the password separately: it is never shown).</summary>
public sealed record LdapAccountForm(string Name, string? Description, bool Enabled, bool AllAddressBooks, IReadOnlyList<long> AddressBookIds);

/// <summary>An account as the edit page shows it.</summary>
public sealed record LdapAccountDetails(long Id, LdapAccountForm Form, DateTime? LastBindDate, DateTime CreateDate);

/// <summary>An address book to choose for an account (only the LDAP-enabled ones are served).</summary>
public sealed record LdapBookChoice(long Id, string Name, string LdapName, bool LdapEnabled);

/// <summary>
/// The LDAP accounts (administrators only; every method checks the actor and answers null / <see cref="Outcome{T}.Denied"/> to anybody else). The name is the bind
/// name: ASCII in small letters (what a person types is lower-cased), unique among the accounts that exist. The password is hashed with <see cref="PasswordService"/>
/// and never shown again; a new account needs one, a saved account keeps its own unless a new one is given. An account reads every LDAP-enabled address book or
/// those it is given. Every change goes to the activity and, through <c>AppDbContext.SaveChanges</c>, to the snapshot of the server.
/// </summary>
public static partial class LdapAccountService
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex NameRegex();

    public static bool IsName(string? name) => name is not null && NameRegex().IsMatch(name);

    public static async Task<LdapAccountList?> ListAsync(AppDbContext db, Actor actor, LdapAccountCriteria criteria, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        var query = db.LdapAccounts.AsNoTracking();
        var total = await query.CountAsync(ct);
        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var pattern = "%" + criteria.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(a => EF.Functions.ILike(a.Name, pattern) || EF.Functions.ILike(a.Description ?? string.Empty, pattern));
        }

        query = criteria.State switch
        {
            "enabled" => query.Where(a => a.Enabled),
            "disabled" => query.Where(a => !a.Enabled),
            _ => query,
        };
        var matching = await query.CountAsync(ct);
        query = (criteria.Sort, criteria.Descending) switch
        {
            ("lastbind", false) => query.OrderBy(a => a.LastBindDate).ThenBy(a => a.Name),
            ("lastbind", true) => query.OrderByDescending(a => a.LastBindDate).ThenBy(a => a.Name),
            ("created", false) => query.OrderBy(a => a.CreateDate).ThenBy(a => a.Id),
            ("created", true) => query.OrderByDescending(a => a.CreateDate).ThenByDescending(a => a.Id),
            (_, true) => query.OrderByDescending(a => a.Name),
            _ => query.OrderBy(a => a.Name),
        };
        var items = await query.Skip(Math.Max(0, criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize)
            .Select(a => new LdapAccountRow(a.Id, a.Name, a.Description, a.Enabled, a.AllAddressBooks,
                a.AddressBooks.Count(x => db.AddressBooks.Any(b => b.Id == x.AddressBookId)), a.LastBindDate, a.CreateDate))
            .ToListAsync(ct);
        return new LdapAccountList(items, total, matching);
    }

    public static async Task<LdapAccountDetails?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        var a = await db.LdapAccounts.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Name, x.Description, x.Enabled, x.AllAddressBooks, x.LastBindDate, x.CreateDate, Books = x.AddressBooks.Select(b => b.AddressBookId).ToList() })
            .FirstOrDefaultAsync(ct);
        return a is null ? null
            : new LdapAccountDetails(a.Id, new LdapAccountForm(a.Name, a.Description, a.Enabled, a.AllAddressBooks, a.Books), a.LastBindDate, a.CreateDate);
    }

    /// <summary>The address books an account can be given (through the permission check of the address books: an administrator sees them all).</summary>
    public static async Task<IReadOnlyList<LdapBookChoice>?> AddressBooksAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        return [.. (await AddressBookService.ListAsync(db, actor, ct)).Select(b => new LdapBookChoice(b.Id, b.Name, b.LdapName, b.LdapEnabled))];
    }

    /// <summary>
    /// Creates (<paramref name="id"/> null) or changes an account. <paramref name="password"/> is the new password (needed for a new account; empty keeps the one a
    /// saved account has). Answers the id, or the message of the first wrong value (nothing is saved then).
    /// </summary>
    public static async Task<Outcome<long>> SaveAsync(AppDbContext db, Actor actor, PasswordService passwords, long? id, LdapAccountForm form, string? password,
        CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return Outcome<long>.Denied();
        }

        // the bind name is compared without case (cn=STARFACE is cn=starface): it is kept in small letters
        var name = (form.Name ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsName(name))
        {
            return Outcome<long>.Fail(Text.T("Ldap.Account.Name.Invalid"));
        }

        if (await db.LdapAccounts.AnyAsync(a => a.Name == name && a.Id != (id ?? 0), ct))
        {
            return Outcome<long>.Fail(Text.T("Ldap.Account.Name.Taken"));
        }

        var description = string.IsNullOrWhiteSpace(form.Description) ? null : form.Description.Trim();
        if (description is { Length: > 1000 })
        {
            return Outcome<long>.Fail(Text.T("Ldap.Account.Description.TooLong"));
        }

        var newPassword = string.IsNullOrEmpty(password) ? null : password;
        if (id is null && newPassword is null)
        {
            return Outcome<long>.Fail(Text.T("Ldap.Account.Password.Required"));
        }

        if (newPassword is not null && PasswordService.Check(newPassword) is { } weak)
        {
            return Outcome<long>.Fail(weak);
        }

        var existing = await db.AddressBooks.AsNoTracking().Select(b => b.Id).ToListAsync(ct);
        var books = form.AddressBookIds.Distinct().Where(existing.Contains).ToList();
        if (!form.AllAddressBooks && books.Count == 0)
        {
            return Outcome<long>.Fail(Text.T("Ldap.Account.AddressBooks.Required"));
        }

        LdapAccount account;
        if (id is { } current)
        {
            var found = await db.LdapAccounts.Include(a => a.AddressBooks).FirstOrDefaultAsync(a => a.Id == current, ct);
            if (found is null)
            {
                return Outcome<long>.Denied();
            }

            account = found;
        }
        else
        {
            account = new LdapAccount();
            db.LdapAccounts.Add(account);
        }

        account.Name = name;
        account.Description = description;
        account.Enabled = form.Enabled;
        account.AllAddressBooks = form.AllAddressBooks;
        if (newPassword is not null)
        {
            account.PasswordHash = passwords.Hash(newPassword);
        }

        // with "all" and no choice sent (the page hides the field) the chosen address books stay, so switching back keeps them
        if (!form.AllAddressBooks || books.Count > 0)
        {
            account.AddressBooks.RemoveAll(x => !books.Contains(x.AddressBookId));
            foreach (var book in books.Where(b => account.AddressBooks.All(x => x.AddressBookId != b)))
            {
                account.AddressBooks.Add(new LdapAccountAddressBook { AddressBookId = book });
            }
        }

        await db.SaveChangesAsync(ct);
        var detail = id is null ? "created" : newPassword is not null ? "changed, new password" : "changed";
        await ActivityLog.AddAsync(db, actor, "ldap", account.Name, detail, account.Id, ct);
        return Outcome<long>.Success(account.Id);
    }

    /// <summary>Deletes an account (softly; administrators only). Its connections lose their rights with the next snapshot.</summary>
    public static async Task<Outcome<bool>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || await db.LdapAccounts.FirstOrDefaultAsync(a => a.Id == id, ct) is not { } account)
        {
            return Outcome<bool>.Denied();
        }

        db.LdapAccounts.Remove(account);
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "ldap", account.Name, "deleted", account.Id, ct);
        return Outcome<bool>.Success(true);
    }
}
