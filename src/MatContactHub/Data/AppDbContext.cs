using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatContactHub.Data;

/// <summary>
/// The database (PostgreSQL). Tables are PascalCase and singular, the key is always <c>Id</c> (BIGINT), every record has CreateDate, CreateUserId, UpdateDate and UpdateUserId
/// (filled in <see cref="SaveChangesAsync(bool, CancellationToken)"/>), and a record with an <c>UpdateState</c> is deleted softly (0 = deleted, 1 = created, 2 = updated).
/// The uploaded CSV files of the data sources and the LDAP certificate are not in the database: they lie in the volume (<c>/data/sources</c>, <c>/data/ldap</c>).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, CurrentUser? current = null) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<Activity> Activities => Set<Activity>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public DbSet<AddressBook> AddressBooks => Set<AddressBook>();

    public DbSet<AddressBookAccess> AddressBookAccess => Set<AddressBookAccess>();

    public DbSet<Contact> Contacts => Set<Contact>();

    public DbSet<DuplicateIgnore> DuplicateIgnores => Set<DuplicateIgnore>();

    public DbSet<DataSource> DataSources => Set<DataSource>();

    public DbSet<FieldMapping> FieldMappings => Set<FieldMapping>();

    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    public DbSet<LdapAccount> LdapAccounts => Set<LdapAccount>();

    public DbSet<LdapAccountAddressBook> LdapAccountAddressBooks => Set<LdapAccountAddressBook>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("User");
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.Language).HasMaxLength(5);
            e.Property(x => x.Theme).HasMaxLength(10);
            e.Property(x => x.Scheme).HasMaxLength(10);
            e.Property(x => x.ExternalSubject).HasMaxLength(200);
            e.HasIndex(x => x.ExternalSubject).IsUnique().HasFilter("\"ExternalSubject\" IS NOT NULL");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("UserSession");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.IdentitySessionId).HasMaxLength(200);
            e.HasIndex(x => x.IdentitySessionId);
        });

        b.Entity<Activity>(e =>
        {
            e.ToTable("Activity");
            e.HasIndex(x => x.OccurredDate);
            e.Property(x => x.Consumer).HasMaxLength(64);
            e.Property(x => x.Action).HasMaxLength(30);
            e.Property(x => x.Target).HasMaxLength(255);
            e.Property(x => x.Detail).HasMaxLength(1000);
        });

        b.Entity<SystemSetting>(e =>
        {
            e.ToTable("SystemSetting");
            e.Property(x => x.PublicUrl).HasMaxLength(300);
            e.Property(x => x.DefaultCountry).HasMaxLength(2);
            e.Property(x => x.LdapBaseDn).HasMaxLength(255);
            e.Property(x => x.LdapCertificateFile).HasMaxLength(255);
            e.Property(x => x.LdapCertificatePasswordProtected).HasMaxLength(2000);
            e.Property(x => x.LdapCertificateThumbprint).HasMaxLength(64);
            e.Property(x => x.LdapCertificateSubject).HasMaxLength(500);
        });

        b.Entity<AddressBook>(e =>
        {
            // LDAP compares ou values without case: the LDAP name is kept in small letters, so the unique index below is one without case too
            e.ToTable("AddressBook", t => t.HasCheckConstraint("CK_AddressBook_LdapName_Lower", "\"LdapName\" = lower(\"LdapName\")"));
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.LdapName).HasMaxLength(64);
            e.HasIndex(x => x.LdapName).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.Property(x => x.Color).HasMaxLength(9);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<AddressBookAccess>(e =>
        {
            e.ToTable("AddressBookAccess");
            e.HasIndex(x => new { x.AddressBookId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.AddressBook).WithMany(x => x.Access).HasForeignKey(x => x.AddressBookId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Contact>(e =>
        {
            e.ToTable("Contact");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.AddressBookId);

            // A record of a source is one contact for its whole life: a contact the sync deleted (softly) comes back as the same row (same Token) when the record returns.
            e.HasIndex(x => new { x.DataSourceId, x.ExternalKey }).IsUnique().HasFilter("\"DataSourceId\" IS NOT NULL");
            e.HasIndex(x => x.DisplayName);
            e.HasOne(x => x.AddressBook).WithMany().HasForeignKey(x => x.AddressBookId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DataSource).WithMany().HasForeignKey(x => x.DataSourceId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ExternalKey).HasMaxLength(255);
            e.Property(x => x.Salutation).HasMaxLength(50);
            e.Property(x => x.Title).HasMaxLength(50);
            e.Property(x => x.FirstName).HasMaxLength(100);
            e.Property(x => x.MiddleName).HasMaxLength(100);
            e.Property(x => x.LastName).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(255);
            e.Property(x => x.Company).HasMaxLength(255);
            e.Property(x => x.Department).HasMaxLength(255);
            e.Property(x => x.JobTitle).HasMaxLength(255);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Email2).HasMaxLength(254);
            e.Property(x => x.Email3).HasMaxLength(254);
            e.Property(x => x.PhoneBusiness).HasMaxLength(64);
            e.Property(x => x.PhoneBusiness2).HasMaxLength(64);
            e.Property(x => x.PhoneMobile).HasMaxLength(64);
            e.Property(x => x.PhoneHome).HasMaxLength(64);
            e.Property(x => x.PhoneOther).HasMaxLength(64);
            e.Property(x => x.Fax).HasMaxLength(64);
            e.Property(x => x.Street).HasMaxLength(255);
            e.Property(x => x.PostalCode).HasMaxLength(20);
            e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.State).HasMaxLength(100);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.Website).HasMaxLength(500);
            e.Property(x => x.CustomerNumber).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(8000);
            e.Property(x => x.Custom1).HasMaxLength(500);
            e.Property(x => x.Custom2).HasMaxLength(500);
            e.Property(x => x.Custom3).HasMaxLength(500);
            e.Property(x => x.Custom4).HasMaxLength(500);
            e.Property(x => x.Custom5).HasMaxLength(500);
            e.Property(x => x.SyncHash).HasMaxLength(64);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<DuplicateIgnore>(e =>
        {
            e.ToTable("DuplicateIgnore");
            e.HasIndex(x => new { x.ContactIdLow, x.ContactIdHigh }).IsUnique();
            e.HasIndex(x => x.ContactIdHigh);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactIdLow).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactIdHigh).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DataSource>(e =>
        {
            e.ToTable("DataSource");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.AddressBookId);
            e.HasOne(x => x.AddressBook).WithMany().HasForeignKey(x => x.AddressBookId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.SettingsJson).HasMaxLength(20000);
            e.Property(x => x.SecretProtected).HasMaxLength(8000);
            e.Property(x => x.KeyField).HasMaxLength(255);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<FieldMapping>(e =>
        {
            e.ToTable("FieldMapping");
            e.HasIndex(x => x.DataSourceId);
            e.HasOne(x => x.DataSource).WithMany(x => x.Mappings).HasForeignKey(x => x.DataSourceId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.TargetField).HasMaxLength(64);
            e.Property(x => x.SourceExpression).HasMaxLength(1000);
        });

        b.Entity<SyncRun>(e =>
        {
            e.ToTable("SyncRun");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => new { x.DataSourceId, x.StartDate });
            e.HasIndex(x => x.State);
            e.HasOne(x => x.DataSource).WithMany().HasForeignKey(x => x.DataSourceId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Message).HasMaxLength(8000);
        });

        b.Entity<LdapAccount>(e =>
        {
            // the bind name (cn=) is compared without case in LDAP: kept in small letters, so the unique index below is one without case too
            e.ToTable("LdapAccount", t => t.HasCheckConstraint("CK_LdapAccount_Name_Lower", "\"Name\" = lower(\"Name\")"));
            e.Property(x => x.Name).HasMaxLength(64);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<LdapAccountAddressBook>(e =>
        {
            e.ToTable("LdapAccountAddressBook");
            e.HasIndex(x => new { x.LdapAccountId, x.AddressBookId }).IsUnique();
            e.HasIndex(x => x.AddressBookId);
            e.HasOne(x => x.LdapAccount).WithMany(x => x.AddressBooks).HasForeignKey(x => x.LdapAccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AddressBook).WithMany().HasForeignKey(x => x.AddressBookId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var directory = Stamp();
        var count = base.SaveChanges(acceptAllChangesOnSuccess);
        if (directory)
        {
            DirectoryChanges.Bump();
        }

        return count;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var directory = Stamp();
        var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (directory)
        {
            // inside an explicit transaction this is before the commit: whoever commits later calls DirectoryChanges.Bump() once more
            DirectoryChanges.Bump();
        }

        return count;
    }

    /// <summary>The tables the directory (the LDAP snapshot, the directory API) is made of: a change of one of them bumps <see cref="DirectoryChanges.Version"/>.</summary>
    private static bool IsDirectory(object entity)
        => entity is Contact or AddressBook or Data.AddressBookAccess or LdapAccount or LdapAccountAddressBook or SystemSetting;

    /// <summary>Fills the audit columns, and turns the delete of a soft record into UpdateState 0. True when a table of the directory changes.</summary>
    private bool Stamp()
    {
        var now = DateTime.UtcNow;
        var user = current?.Id;
        var directory = false;
        foreach (var entry in ChangeTracker.Entries<Entity>().ToList())
        {
            var entity = entry.Entity;
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && IsDirectory(entity))
            {
                directory = true;
            }

            if (entry.State == EntityState.Deleted && entity is SoftEntity soft)
            {
                entry.State = EntityState.Modified;
                soft.UpdateState = SoftEntity.Deleted;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entity.CreateDate == default)
                    {
                        entity.CreateDate = now;
                    }

                    entity.CreateUserId ??= user;
                    entity.UpdateDate = now;
                    entity.UpdateUserId = user ?? entity.CreateUserId;
                    break;
                case EntityState.Modified:
                    entity.UpdateDate = now;
                    entity.UpdateUserId = user ?? entity.UpdateUserId;
                    if (entity is SoftEntity { UpdateState: not SoftEntity.Deleted } updated)
                    {
                        updated.UpdateState = SoftEntity.Updated;
                    }

                    break;
            }
        }

        return directory;
    }
}

/// <summary>For <c>dotnet ef migrations add</c>: no database is needed to create a migration.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=matcontacthub;Username=matcontacthub;Password=matcontacthub").Options);
}
