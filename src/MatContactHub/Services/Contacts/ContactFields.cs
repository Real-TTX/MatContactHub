using MatContactHub.Data;

namespace MatContactHub.Services.Contacts;

/// <summary>What a text field of a contact is: a name, a number, an e-mail address or plain text (it decides how it is checked, searched and merged).</summary>
public enum ContactFieldKind
{
    Text = 0,
    Phone = 1,
    Email = 2,
}

/// <summary>One text field of a <see cref="Contact"/>: its name (the property), how to read and write it, its longest value and its kind.</summary>
public sealed record ContactField(string Name, Func<Contact, string?> Get, Action<Contact, string?> Set, int MaxLength, ContactFieldKind Kind = ContactFieldKind.Text);

/// <summary>
/// The text fields of a contact in one table (the order of the edit page): the copy of a form, the merge of duplicates (fill what is empty), the check of the lengths and
/// the columns of a CSV import read it, so a field is named in one place only. <see cref="Contact.Birthday"/> is a date and not part of it.
/// </summary>
public static class ContactFields
{
    public static readonly IReadOnlyList<ContactField> All =
    [
        new("Salutation", c => c.Salutation, (c, v) => c.Salutation = v, 50),
        new("Title", c => c.Title, (c, v) => c.Title = v, 50),
        new("FirstName", c => c.FirstName, (c, v) => c.FirstName = v, 100),
        new("MiddleName", c => c.MiddleName, (c, v) => c.MiddleName = v, 100),
        new("LastName", c => c.LastName, (c, v) => c.LastName = v, 100),
        new("DisplayName", c => c.DisplayName, (c, v) => c.DisplayName = v ?? string.Empty, 255),
        new("Company", c => c.Company, (c, v) => c.Company = v, 255),
        new("Department", c => c.Department, (c, v) => c.Department = v, 255),
        new("JobTitle", c => c.JobTitle, (c, v) => c.JobTitle = v, 255),
        new("Email", c => c.Email, (c, v) => c.Email = v, 254, ContactFieldKind.Email),
        new("Email2", c => c.Email2, (c, v) => c.Email2 = v, 254, ContactFieldKind.Email),
        new("Email3", c => c.Email3, (c, v) => c.Email3 = v, 254, ContactFieldKind.Email),
        new("PhoneBusiness", c => c.PhoneBusiness, (c, v) => c.PhoneBusiness = v, 64, ContactFieldKind.Phone),
        new("PhoneBusiness2", c => c.PhoneBusiness2, (c, v) => c.PhoneBusiness2 = v, 64, ContactFieldKind.Phone),
        new("PhoneMobile", c => c.PhoneMobile, (c, v) => c.PhoneMobile = v, 64, ContactFieldKind.Phone),
        new("PhoneHome", c => c.PhoneHome, (c, v) => c.PhoneHome = v, 64, ContactFieldKind.Phone),
        new("PhoneOther", c => c.PhoneOther, (c, v) => c.PhoneOther = v, 64, ContactFieldKind.Phone),
        new("Fax", c => c.Fax, (c, v) => c.Fax = v, 64, ContactFieldKind.Phone),
        new("Street", c => c.Street, (c, v) => c.Street = v, 255),
        new("PostalCode", c => c.PostalCode, (c, v) => c.PostalCode = v, 20),
        new("City", c => c.City, (c, v) => c.City = v, 100),
        new("State", c => c.State, (c, v) => c.State = v, 100),
        new("Country", c => c.Country, (c, v) => c.Country = v, 100),
        new("Website", c => c.Website, (c, v) => c.Website = v, 500),
        new("CustomerNumber", c => c.CustomerNumber, (c, v) => c.CustomerNumber = v, 100),
        new("Notes", c => c.Notes, (c, v) => c.Notes = v, 8000),
        new("Custom1", c => c.Custom1, (c, v) => c.Custom1 = v, 500),
        new("Custom2", c => c.Custom2, (c, v) => c.Custom2 = v, 500),
        new("Custom3", c => c.Custom3, (c, v) => c.Custom3 = v, 500),
        new("Custom4", c => c.Custom4, (c, v) => c.Custom4 = v, 500),
        new("Custom5", c => c.Custom5, (c, v) => c.Custom5 = v, 500),
    ];

    /// <summary>The numbers of a contact (with the field each is in), in the order a caller-ID prefers them: business first, the fax last.</summary>
    public static readonly IReadOnlyList<ContactField> Phones = [.. All.Where(f => f.Kind == ContactFieldKind.Phone)];

    /// <summary>The e-mail addresses of a contact.</summary>
    public static readonly IReadOnlyList<ContactField> Emails = [.. All.Where(f => f.Kind == ContactFieldKind.Email)];

    private static readonly Dictionary<string, ContactField> ByName = All.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The field with this name (without case), or null.</summary>
    public static ContactField? Find(string? name) => name is not null && ByName.TryGetValue(name, out var field) ? field : null;

    /// <summary>The short name of a number field for the API and the badges (<c>business</c>, <c>mobile</c> …).</summary>
    public static string PhoneType(string field) => field switch
    {
        "PhoneBusiness" => "business",
        "PhoneBusiness2" => "business2",
        "PhoneMobile" => "mobile",
        "PhoneHome" => "home",
        "PhoneOther" => "other",
        "Fax" => "fax",
        _ => "other",
    };

    /// <summary>A value as it is stored: trimmed, null when empty.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
