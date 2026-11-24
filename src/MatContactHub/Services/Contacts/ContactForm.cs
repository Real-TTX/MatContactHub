using MatContactHub.Data;

namespace MatContactHub.Services.Contacts;

/// <summary>
/// What the edit page of a contact shows and sends: every field of the tabs General, Communication, Address and More, the address book and <see cref="Hidden"/>.
/// A class with setters, so a page binds it as it is (<c>Form.FirstName</c>); the business layer cleans every value (trimmed, null when empty) before it keeps it.
/// </summary>
public sealed class ContactForm
{
    public long AddressBookId { get; set; }

    /// <summary>Not served by LDAP and the directory API (the only field of a synced contact that can be changed).</summary>
    public bool Hidden { get; set; }

    public string? Salutation { get; set; }

    public string? Title { get; set; }

    public string? FirstName { get; set; }

    public string? MiddleName { get; set; }

    public string? LastName { get; set; }

    public string? DisplayName { get; set; }

    public string? Company { get; set; }

    public string? Department { get; set; }

    public string? JobTitle { get; set; }

    public string? Email { get; set; }

    public string? Email2 { get; set; }

    public string? Email3 { get; set; }

    public string? PhoneBusiness { get; set; }

    public string? PhoneBusiness2 { get; set; }

    public string? PhoneMobile { get; set; }

    public string? PhoneHome { get; set; }

    public string? PhoneOther { get; set; }

    public string? Fax { get; set; }

    public string? Website { get; set; }

    public string? Street { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Country { get; set; }

    public DateOnly? Birthday { get; set; }

    public string? CustomerNumber { get; set; }

    public string? Notes { get; set; }

    public string? Custom1 { get; set; }

    public string? Custom2 { get; set; }

    public string? Custom3 { get; set; }

    public string? Custom4 { get; set; }

    public string? Custom5 { get; set; }

    /// <summary>The form of a contact as it is stored.</summary>
    public static ContactForm Of(Contact c) => new()
    {
        AddressBookId = c.AddressBookId,
        Hidden = c.Hidden,
        Salutation = c.Salutation,
        Title = c.Title,
        FirstName = c.FirstName,
        MiddleName = c.MiddleName,
        LastName = c.LastName,
        DisplayName = c.DisplayName,
        Company = c.Company,
        Department = c.Department,
        JobTitle = c.JobTitle,
        Email = c.Email,
        Email2 = c.Email2,
        Email3 = c.Email3,
        PhoneBusiness = c.PhoneBusiness,
        PhoneBusiness2 = c.PhoneBusiness2,
        PhoneMobile = c.PhoneMobile,
        PhoneHome = c.PhoneHome,
        PhoneOther = c.PhoneOther,
        Fax = c.Fax,
        Website = c.Website,
        Street = c.Street,
        PostalCode = c.PostalCode,
        City = c.City,
        State = c.State,
        Country = c.Country,
        Birthday = c.Birthday,
        CustomerNumber = c.CustomerNumber,
        Notes = c.Notes,
        Custom1 = c.Custom1,
        Custom2 = c.Custom2,
        Custom3 = c.Custom3,
        Custom4 = c.Custom4,
        Custom5 = c.Custom5,
    };

    /// <summary>Writes the fields (cleaned: trimmed, null when empty) into <paramref name="c"/>; the address book, Hidden and the display name are the caller's.</summary>
    public void CopyTo(Contact c)
    {
        string? C(string? v) => ContactFields.Clean(v);
        c.Salutation = C(Salutation);
        c.Title = C(Title);
        c.FirstName = C(FirstName);
        c.MiddleName = C(MiddleName);
        c.LastName = C(LastName);
        c.Company = C(Company);
        c.Department = C(Department);
        c.JobTitle = C(JobTitle);
        c.Email = C(Email);
        c.Email2 = C(Email2);
        c.Email3 = C(Email3);
        c.PhoneBusiness = C(PhoneBusiness);
        c.PhoneBusiness2 = C(PhoneBusiness2);
        c.PhoneMobile = C(PhoneMobile);
        c.PhoneHome = C(PhoneHome);
        c.PhoneOther = C(PhoneOther);
        c.Fax = C(Fax);
        c.Website = C(Website);
        c.Street = C(Street);
        c.PostalCode = C(PostalCode);
        c.City = C(City);
        c.State = C(State);
        c.Country = C(Country);
        c.Birthday = Birthday;
        c.CustomerNumber = C(CustomerNumber);
        c.Notes = C(Notes);
        c.Custom1 = C(Custom1);
        c.Custom2 = C(Custom2);
        c.Custom3 = C(Custom3);
        c.Custom4 = C(Custom4);
        c.Custom5 = C(Custom5);
    }
}
