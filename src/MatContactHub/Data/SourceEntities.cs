namespace MatContactHub.Data;

/// <summary>Where the contacts of a data source come from.</summary>
public enum DataSourceKind
{
    /// <summary>A REST API (JSON).</summary>
    Rest = 1,

    /// <summary>A query against Microsoft SQL Server.</summary>
    SqlServer = 2,

    /// <summary>A CSV file (uploaded or from an address).</summary>
    Csv = 3,
}

/// <summary>What is done to a value on its way from the source into the contact.</summary>
public enum FieldTransform
{
    None = 0,
    Trim = 1,

    /// <summary>A telephone number in E.164 (with <see cref="SystemSetting.DefaultCountry"/> for numbers without a prefix).</summary>
    Phone = 2,
    Lower = 3,
    Upper = 4,
    Capitalize = 5,
}

/// <summary>Why a sync ran.</summary>
public enum SyncTrigger
{
    Schedule = 1,
    Manual = 2,
}

public enum SyncState
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>
/// A data source: a REST API, a SQL Server query or a CSV file whose records become the contacts of one address book. The settings that are no secret are in
/// <see cref="SettingsJson"/> (their shape depends on <see cref="Kind"/>), the secrets (passwords, tokens, the connection string) in <see cref="SecretProtected"/>
/// (DataProtection); they never go back to the browser.
/// </summary>
public sealed class DataSource : SoftEntity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public DataSourceKind Kind { get; set; } = DataSourceKind.Rest;

    /// <summary>The address book the contacts are written into.</summary>
    public long AddressBookId { get; set; }

    public AddressBook? AddressBook { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Synchronise every this many minutes; 0 = only by hand.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>The longest <see cref="SettingsJson"/> (the column).</summary>
    public const int SettingsJsonMaxLength = 20000;

    /// <summary>The longest <see cref="SecretProtected"/> (the column).</summary>
    public const int SecretProtectedMaxLength = 8000;

    /// <summary>The settings of the kind that are no secret (JSON).</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>The secrets of the kind (JSON, protected with DataProtection); null = none.</summary>
    public string? SecretProtected { get; set; }

    /// <summary>The field of the source whose value is the <see cref="Contact.ExternalKey"/>.</summary>
    public string KeyField { get; set; } = string.Empty;

    /// <summary>Contacts that are not in the source any more are deleted (softly).</summary>
    public bool DeleteMissing { get; set; } = true;

    public DateTime? LastRunDate { get; set; }

    public SyncState? LastRunState { get; set; }

    public DateTime? NextRunDate { get; set; }

    public string? LastError { get; set; }

    /// <summary>The fields the source was last seen with (a JSON array of names, from the preview and the sync): the suggestions of the mapping.</summary>
    public string? DiscoveredFields { get; set; }

    public List<FieldMapping> Mappings { get; set; } = [];
}

/// <summary>One field of the contact and where its value comes from: a field of the source (<c>first_name</c>, <c>address.city</c>) or a template (<c>{first} {last}</c>).</summary>
public sealed class FieldMapping : Entity
{
    public long DataSourceId { get; set; }

    public DataSource? DataSource { get; set; }

    /// <summary>The name of the property of <see cref="Contact"/> (<c>FirstName</c>, <c>PhoneBusiness</c>).</summary>
    public string TargetField { get; set; } = string.Empty;

    public string SourceExpression { get; set; } = string.Empty;

    public FieldTransform Transform { get; set; } = FieldTransform.None;

    public int SortOrder { get; set; }
}

/// <summary>One run of the sync of a data source, with its numbers.</summary>
public sealed class SyncRun : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long DataSourceId { get; set; }

    public DataSource? DataSource { get; set; }

    public SyncTrigger Trigger { get; set; } = SyncTrigger.Schedule;

    public SyncState State { get; set; } = SyncState.Running;

    public DateTime StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public int RecordsRead { get; set; }

    public int Created { get; set; }

    public int Updated { get; set; }

    public int Unchanged { get; set; }

    public int Deleted { get; set; }

    public int Skipped { get; set; }

    /// <summary>Errors and warnings of the run (capped).</summary>
    public string? Message { get; set; }
}
