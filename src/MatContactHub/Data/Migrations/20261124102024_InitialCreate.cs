using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatContactHub.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Activity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    Consumer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Target = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TargetId = table.Column<long>(type: "bigint", nullable: true),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OccurredDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activity", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AddressBook",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LdapName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LdapEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AppsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsShared = table.Column<bool>(type: "boolean", nullable: false),
                    Color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateState = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddressBook", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LdapAccount",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AllAddressBooks = table.Column<bool>(type: "boolean", nullable: false),
                    LastBindDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateState = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LdapAccount", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemSetting",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RegistrationOpen = table.Column<bool>(type: "boolean", nullable: false),
                    PublicUrl = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    DefaultCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    SyncRunDays = table.Column<int>(type: "integer", nullable: false),
                    LdapEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LdapStartTlsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LdapsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LdapBaseDn = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    LdapAllowAnonymous = table.Column<bool>(type: "boolean", nullable: false),
                    LdapNumberFormat = table.Column<int>(type: "integer", nullable: false),
                    LdapSizeLimit = table.Column<int>(type: "integer", nullable: false),
                    LdapCertificateSource = table.Column<int>(type: "integer", nullable: false),
                    LdapCertificateFile = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    LdapCertificatePasswordProtected = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LdapCertificateThumbprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LdapCertificateSubject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LdapCertificateExpires = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSetting", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "User",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    FailedLogins = table.Column<int>(type: "integer", nullable: false),
                    LockoutUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastLoginDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Theme = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Scheme = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ExternalSubject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateState = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataSource",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Token = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    AddressBookId = table.Column<long>(type: "bigint", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    SettingsJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    SecretProtected = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    KeyField = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DeleteMissing = table.Column<bool>(type: "boolean", nullable: false),
                    LastRunDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunState = table.Column<int>(type: "integer", nullable: true),
                    NextRunDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateState = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSource", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataSource_AddressBook_AddressBookId",
                        column: x => x.AddressBookId,
                        principalTable: "AddressBook",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LdapAccountAddressBook",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LdapAccountId = table.Column<long>(type: "bigint", nullable: false),
                    AddressBookId = table.Column<long>(type: "bigint", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LdapAccountAddressBook", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LdapAccountAddressBook_AddressBook_AddressBookId",
                        column: x => x.AddressBookId,
                        principalTable: "AddressBook",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LdapAccountAddressBook_LdapAccount_LdapAccountId",
                        column: x => x.LdapAccountId,
                        principalTable: "LdapAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AddressBookAccess",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AddressBookId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddressBookAccess", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AddressBookAccess_AddressBook_AddressBookId",
                        column: x => x.AddressBookId,
                        principalTable: "AddressBook",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AddressBookAccess_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSession",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Token = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    LastSeenDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IdentityToken = table.Column<string>(type: "text", nullable: true),
                    IdentitySessionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSession", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSession_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Contact",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Token = table.Column<Guid>(type: "uuid", nullable: false),
                    AddressBookId = table.Column<long>(type: "bigint", nullable: false),
                    DataSourceId = table.Column<long>(type: "bigint", nullable: true),
                    ExternalKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false),
                    Salutation = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Title = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MiddleName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Company = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Department = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    JobTitle = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    Email2 = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    Email3 = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    PhoneBusiness = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PhoneBusiness2 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PhoneMobile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PhoneHome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PhoneOther = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Fax = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Street = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    State = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Website = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Birthday = table.Column<DateOnly>(type: "date", nullable: true),
                    CustomerNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Custom1 = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Custom2 = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Custom3 = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Custom4 = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Custom5 = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SyncHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSyncDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateState = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contact", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contact_AddressBook_AddressBookId",
                        column: x => x.AddressBookId,
                        principalTable: "AddressBook",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Contact_DataSource_DataSourceId",
                        column: x => x.DataSourceId,
                        principalTable: "DataSource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FieldMapping",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DataSourceId = table.Column<long>(type: "bigint", nullable: false),
                    TargetField = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceExpression = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Transform = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FieldMapping_DataSource_DataSourceId",
                        column: x => x.DataSourceId,
                        principalTable: "DataSource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Token = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<long>(type: "bigint", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecordsRead = table.Column<int>(type: "integer", nullable: false),
                    Created = table.Column<int>(type: "integer", nullable: false),
                    Updated = table.Column<int>(type: "integer", nullable: false),
                    Unchanged = table.Column<int>(type: "integer", nullable: false),
                    Deleted = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRun_DataSource_DataSourceId",
                        column: x => x.DataSourceId,
                        principalTable: "DataSource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DuplicateIgnore",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContactIdLow = table.Column<long>(type: "bigint", nullable: false),
                    ContactIdHigh = table.Column<long>(type: "bigint", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateIgnore", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DuplicateIgnore_Contact_ContactIdHigh",
                        column: x => x.ContactIdHigh,
                        principalTable: "Contact",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DuplicateIgnore_Contact_ContactIdLow",
                        column: x => x.ContactIdLow,
                        principalTable: "Contact",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activity_OccurredDate",
                table: "Activity",
                column: "OccurredDate");

            migrationBuilder.CreateIndex(
                name: "IX_AddressBook_LdapName",
                table: "AddressBook",
                column: "LdapName",
                unique: true,
                filter: "\"UpdateState\" <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_AddressBookAccess_AddressBookId_UserId",
                table: "AddressBookAccess",
                columns: new[] { "AddressBookId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AddressBookAccess_UserId",
                table: "AddressBookAccess",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Contact_AddressBookId",
                table: "Contact",
                column: "AddressBookId");

            migrationBuilder.CreateIndex(
                name: "IX_Contact_DataSourceId_ExternalKey",
                table: "Contact",
                columns: new[] { "DataSourceId", "ExternalKey" },
                unique: true,
                filter: "\"DataSourceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Contact_DisplayName",
                table: "Contact",
                column: "DisplayName");

            migrationBuilder.CreateIndex(
                name: "IX_Contact_Token",
                table: "Contact",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataSource_AddressBookId",
                table: "DataSource",
                column: "AddressBookId");

            migrationBuilder.CreateIndex(
                name: "IX_DataSource_Token",
                table: "DataSource",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateIgnore_ContactIdHigh",
                table: "DuplicateIgnore",
                column: "ContactIdHigh");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateIgnore_ContactIdLow_ContactIdHigh",
                table: "DuplicateIgnore",
                columns: new[] { "ContactIdLow", "ContactIdHigh" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FieldMapping_DataSourceId",
                table: "FieldMapping",
                column: "DataSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_LdapAccount_Name",
                table: "LdapAccount",
                column: "Name",
                unique: true,
                filter: "\"UpdateState\" <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_LdapAccountAddressBook_AddressBookId",
                table: "LdapAccountAddressBook",
                column: "AddressBookId");

            migrationBuilder.CreateIndex(
                name: "IX_LdapAccountAddressBook_LdapAccountId_AddressBookId",
                table: "LdapAccountAddressBook",
                columns: new[] { "LdapAccountId", "AddressBookId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRun_DataSourceId_StartDate",
                table: "SyncRun",
                columns: new[] { "DataSourceId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRun_State",
                table: "SyncRun",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRun_Token",
                table: "SyncRun",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_User_Email",
                table: "User",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_User_ExternalSubject",
                table: "User",
                column: "ExternalSubject",
                unique: true,
                filter: "\"ExternalSubject\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserSession_IdentitySessionId",
                table: "UserSession",
                column: "IdentitySessionId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSession_Token",
                table: "UserSession",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSession_UserId",
                table: "UserSession",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Activity");

            migrationBuilder.DropTable(
                name: "AddressBookAccess");

            migrationBuilder.DropTable(
                name: "DuplicateIgnore");

            migrationBuilder.DropTable(
                name: "FieldMapping");

            migrationBuilder.DropTable(
                name: "LdapAccountAddressBook");

            migrationBuilder.DropTable(
                name: "SyncRun");

            migrationBuilder.DropTable(
                name: "SystemSetting");

            migrationBuilder.DropTable(
                name: "UserSession");

            migrationBuilder.DropTable(
                name: "Contact");

            migrationBuilder.DropTable(
                name: "LdapAccount");

            migrationBuilder.DropTable(
                name: "User");

            migrationBuilder.DropTable(
                name: "DataSource");

            migrationBuilder.DropTable(
                name: "AddressBook");
        }
    }
}
