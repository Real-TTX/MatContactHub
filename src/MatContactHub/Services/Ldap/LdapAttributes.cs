namespace MatContactHub.Services.Ldap;

/// <summary>
/// The attribute types of the directory: their names (an LDAP client may write <c>CN</c>, <c>commonName</c> or <c>2.5.4.3</c> for <c>cn</c>, with or without options
/// such as <c>;lang-de</c>), which are telephone numbers (compared as numbers) and which are operational (only sent when asked for, or with <c>+</c>).
/// </summary>
public static class LdapAttributes
{
    public const string ObjectClass = "objectClass";
    public const string Uid = "uid";
    public const string Cn = "cn";
    public const string Sn = "sn";
    public const string GivenName = "givenName";
    public const string DisplayName = "displayName";
    public const string Mail = "mail";
    public const string TelephoneNumber = "telephoneNumber";
    public const string Mobile = "mobile";
    public const string HomePhone = "homePhone";
    public const string Fax = "facsimileTelephoneNumber";
    public const string OtherTelephone = "otherTelephone";
    public const string O = "o";
    public const string Ou = "ou";
    public const string Title = "title";
    public const string Street = "street";
    public const string PostalCode = "postalCode";
    public const string L = "l";
    public const string St = "st";
    public const string C = "c";
    public const string Co = "co";
    public const string LabeledUri = "labeledURI";
    public const string Description = "description";
    public const string Dc = "dc";
    public const string EntryUuid = "entryUUID";
    public const string EntryDn = "entryDN";
    public const string StructuralObjectClass = "structuralObjectClass";
    public const string HasSubordinates = "hasSubordinates";
    public const string SubschemaSubentry = "subschemaSubentry";
    public const string CreateTimestamp = "createTimestamp";
    public const string ModifyTimestamp = "modifyTimestamp";
    public const string NamingContexts = "namingContexts";
    public const string SupportedLdapVersion = "supportedLDAPVersion";
    public const string SupportedControl = "supportedControl";
    public const string SupportedExtension = "supportedExtension";
    public const string SupportedFeatures = "supportedFeatures";
    public const string VendorName = "vendorName";
    public const string VendorVersion = "vendorVersion";
    public const string AttributeTypes = "attributeTypes";
    public const string ObjectClasses = "objectClasses";

    /// <summary>Every name and OID a client may use → the canonical name (keys without case).</summary>
    private static readonly Dictionary<string, string> Names = Build(
        (ObjectClass, ["2.5.4.0"]),
        (Uid, ["userid", "0.9.2342.19200300.100.1.1"]),
        (Cn, ["commonName", "2.5.4.3"]),
        (Sn, ["surname", "2.5.4.4"]),
        (GivenName, ["gn", "2.5.4.42"]),
        (DisplayName, ["2.16.840.1.113730.3.1.241"]),
        (Mail, ["rfc822Mailbox", "0.9.2342.19200300.100.1.3"]),
        (TelephoneNumber, ["2.5.4.20"]),
        (Mobile, ["mobileTelephoneNumber", "0.9.2342.19200300.100.1.41"]),
        (HomePhone, ["homeTelephoneNumber", "0.9.2342.19200300.100.1.20"]),
        (Fax, ["fax", "2.5.4.23"]),
        (OtherTelephone, []),
        (O, ["organizationName", "2.5.4.10"]),
        (Ou, ["organizationalUnitName", "2.5.4.11"]),
        (Title, ["2.5.4.12"]),
        (Street, ["streetAddress", "2.5.4.9"]),
        (PostalCode, ["2.5.4.17"]),
        (L, ["localityName", "2.5.4.7"]),
        (St, ["stateOrProvinceName", "2.5.4.8"]),
        (C, ["countryName", "2.5.4.6"]),
        (Co, ["friendlyCountryName", "0.9.2342.19200300.100.1.43"]),
        (LabeledUri, ["1.3.6.1.4.1.250.1.57"]),
        (Description, ["2.5.4.13"]),
        (Dc, ["domainComponent", "0.9.2342.19200300.100.1.25"]),
        (EntryUuid, ["1.3.6.1.1.16.4"]),
        (EntryDn, ["1.3.6.1.1.20"]),
        (StructuralObjectClass, ["2.5.21.9"]),
        (HasSubordinates, ["2.5.18.9"]),
        (SubschemaSubentry, ["2.5.18.10"]),
        (CreateTimestamp, ["2.5.18.1"]),
        (ModifyTimestamp, ["2.5.18.2"]),
        (NamingContexts, ["1.3.6.1.4.1.1466.101.120.5"]),
        (SupportedLdapVersion, ["1.3.6.1.4.1.1466.101.120.15"]),
        (SupportedControl, ["1.3.6.1.4.1.1466.101.120.13"]),
        (SupportedExtension, ["1.3.6.1.4.1.1466.101.120.7"]),
        (SupportedFeatures, ["1.3.6.1.4.1.4203.1.3.5"]),
        (VendorName, ["1.3.6.1.1.4"]),
        (VendorVersion, ["1.3.6.1.1.5"]),
        (AttributeTypes, ["2.5.21.5"]),
        (ObjectClasses, ["2.5.21.6"]));

    /// <summary>The attributes that hold telephone numbers: a filter compares them as numbers (<see cref="LdapPhone"/>).</summary>
    public static readonly IReadOnlySet<string> Phones = new HashSet<string>([TelephoneNumber, Mobile, HomePhone, Fax, OtherTelephone], StringComparer.OrdinalIgnoreCase);

    /// <summary>The operational attributes of the entries (RFC 4512, 3.4): sent only when asked for by name or with <c>+</c>.</summary>
    public static readonly IReadOnlySet<string> Operational = new HashSet<string>([EntryUuid, EntryDn, StructuralObjectClass, HasSubordinates, SubschemaSubentry, CreateTimestamp, ModifyTimestamp],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>The canonical name of an attribute description (<c>CN;lang-de</c> → <c>cn</c>, <c>2.5.4.3</c> → <c>cn</c>); an unknown name is kept as it is (without options).</summary>
    public static string Canonical(string description)
    {
        var name = description.Trim();
        var semicolon = name.IndexOf(';');
        if (semicolon >= 0)
        {
            name = name[..semicolon];
        }

        return Names.TryGetValue(name, out var canonical) ? canonical : name;
    }

    public static bool IsPhone(string canonical) => Phones.Contains(canonical);

    /// <summary>Does the directory know the attribute type? A filter on an unknown one is Undefined (RFC 4511, 4.5.1.7).</summary>
    public static bool IsKnown(string canonical) => Names.ContainsKey(canonical);

    private static Dictionary<string, string> Build(params (string Name, string[] Aliases)[] list)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, aliases) in list)
        {
            map[name] = name;
            foreach (var alias in aliases)
            {
                map[alias] = name;
            }
        }

        return map;
    }
}
