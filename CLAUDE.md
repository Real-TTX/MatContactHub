# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What MatContactHub is

A contact directory of its own with a web interface – the idea of estos MetaDirectory for the Mat family. **Data sources** (REST APIs, Microsoft SQL Server, CSV
files) are imported, unified by a field mapping per source and synchronised automatically into **address books**. **Contacts** are the centre: a list with search,
filters and sort (the start page after the sign-in), manual contacts next to synchronised ones (read-only: the source wins; only `Hidden` is editable), **duplicate**
detection. A built-in **LDAP server** (LDAPv3, StartTLS, LDAPS) serves the LDAP-enabled address books to STARFACE, IP phones and mail clients. People have the role
Viewer or Editor per address book (`AddressBookAccess`), a shared address book makes everybody a viewer, administrators may do everything. A viewer only reads: changing
`Hidden` of a synced contact needs the role Editor too (it takes the contact out of LDAP and the directory API for everybody).

Part of the Mat family on **MatPlatform 2.4** (MatDesign `docs/PLATFORM.md`): MatContactHub **provides** `contacts.directory` (a key capability: a key per app,
MatPlatform keeps the keys; the consumer gets `MAT_CONTACTS_DIRECTORY_URL`, `_INTERNAL_URL`, `_KEY` – the platform derives the names from the capability),
**consumes** `identity.oidc` (MatIdentity) and `mail.smtp` (MatSMTP), both optional, and **contributes** the launcher Contacts (quick actions New contact, Address
books, Duplicates, Sync now = the action `syncAll`), two admin widgets (`directory` tiles, `syncRuns` list) and health (failing data sources = degraded).
`contacts.directory` is not in MatDesign's PLATFORM.md table yet: add the row there with the next change of MatDesign.
**Deliberate deviation from the spec:** the spec names the variables `MAT_CONTACTS_URL`, `_INTERNAL_URL`, `_KEY`. MatPlatform 2.4 derives the names of a key
capability from its id (`MatCapabilities.KeyStyle`/`PrefixOf`: `contacts.directory` → `MAT_CONTACTS_DIRECTORY_*`, like `office.edit` → `MAT_OFFICE_EDIT_*`), and the
copy of MatPlatform is never edited here, so MatContactHub keeps `MAT_CONTACTS_DIRECTORY_*` (decided; consumers use these names). Only a written-down entry for
`contacts.directory` in MatDesign's `MatCapabilities` could give it other names.

Stack: ASP.NET Core on **.NET 10**, C#, **Razor Pages**, EF Core + **PostgreSQL**, the **MatUi** toolkit of MatDesign (`wwwroot/matui`, release **0.12.0**, bundled
from `origin/main`: never edit by hand), plain JavaScript without a build step, `libphonenumber-csharp` for telephone numbers. Docker-first. Ports: web **7087**,
pgAdmin 7088, local Postgres 7089; LDAP **10389** (plain + StartTLS) and **10636** (LDAPS) inside the container, mapped to 389/636 (release) or 7389/7636 (dev,
local). UI languages: English and German (catalog in `Resources/Texts/*.json`, German with "du"). Accent: **provisional** teal `#0f766e` (the logo module fixes it).

## Build / run / test

- **Build:** `dotnet build MatContactHub.slnx` (SDK 10, `global.json`). Expected: 0 warnings, 0 errors.
- **Tests:** `dotnet test`. The database tests (`[DbFact]`) run when `MATCONTACTHUB_TEST_DB` is set (an administrator connection string, e.g.
  `Host=localhost;Port=7089;Username=matcontacthub;Password=matcontacthub;Database=postgres` while the local stack runs); each test creates, migrates and drops a
  database. `TextCatalogTests` check every text key of the code and the scripts in English and German (and the keys built from enums); `PhoneNormalizerTests`,
  `DataModelTests` (unique keys, soft delete), `PlatformTests` (the definition, the broker variables, the key check of the directory API), `AccountTests`,
  `ContactPermissionTests` (viewer / editor / administrator / stranger on manual and synced contacts), `AddressBookServiceTests` (LDAP names without case, colour).
- **Run / test the app: always rebuild the container** (`./scripts/redeploy.ps1`, `-Clean` for empty volumes). CSS, JS and cshtml are baked into the image.
- **Test account (always):** `admin@example.com` / `Start12345` – the first account of a fresh stack (it becomes the administrator).
- **Real UI tests are end to end in a browser** (Playwright with the installed Edge, `channel: 'msedge'`): every page, the console, a phone (390 px), dark mode.
- **Migrations:** `dotnet ef migrations add <Name> -p src/MatContactHub -o Data/Migrations` (local tool in `dotnet-tools.json`; a design-time factory exists). Applied
  at start-up. The foundation's `InitialCreate` holds the whole data model of the spec; `LdapNamesIgnoreCase` (0.1) keeps the LDAP names in small letters.
- **Updating MatUi:** take the release of MatDesign (`origin/main`): the CSS is the parts of `src/MatUi/wwwroot/css` joined in the order of the first line of `matui.css`;
  `matui.js`, `matui.boot.js`, `matui-icons.svg` as they are. **MatPlatform:** `node tools/matplatform-copy.js ../MatContactHub/src/MatContactHub` in MatDesign –
  the tool reads MatDesign's working tree, so only run it while MatDesign is on `main` (else copy `git show origin/main:src/MatPlatform/<file>`).
- **Logo:** `node scripts/logo.js` writes the SVGs (mark, favicon, maskable, mono, logo, logo-dark), `scripts/icons.ps1` the PNGs with Edge headless. The glyph of
  the menu is `mat-i-matcontacthub` in `wwwroot/img/app-icons.svg`. The foundation's mark is a neutral "CH" placeholder.
- Write files with the editor tools, not with shell heredocs that contain apostrophes or backslashes (Git Bash also rewrites arguments that start with `/`).

## Map

```
src/MatContactHub
  Program.cs                 composition root: services, cookie auth (+ MatIdentity), rate limits (sign-in, directory API), static files, endpoints
  Infrastructure.cs          HealthCheck (--healthcheck), Database.MigrateWithRetry, SecurityHeaders (CSP), MaintenanceService (hourly: sessions, old sync runs)
  Data/                      Entities.cs (Entity, SoftEntity, User, UserSession, Activity, SystemSetting incl. the LDAP columns, NumberFormat), ContactEntities.cs
                             (AddressBook, AddressBookAccess, Contact, DuplicateIgnore), SourceEntities.cs (DataSource, FieldMapping, SyncRun + enums),
                             LdapEntities.cs (LdapAccount, LdapAccountAddressBook), AppDbContext.cs (audit columns, soft delete, DirectoryChanges bump), Migrations/
  Services/                  Actor (who acts: person, app, system), ActivityLog, InstanceService (numbers, settings, housekeeping), SettingsStore (the one row of
                             SystemSetting), MenuService, Ui (icons, logo), ListModels (toolbar, pager), Dates, Outcome
  Services/Accounts|Auth/    profiles, user administration, sessions in the DB, MatIdentity sign-in, GDPR export/erase
  Services/Contacts/         module C + the shared helpers: AddressBookService (THE permission check RolesAsync/RoleAsync), ContactService, DuplicateService,
                             DirectoryApi (contacts.directory), PhoneNormalizer (shared), DirectoryChanges (shared)
  Services/Sources/          module B: DataSourceService, SyncEngine, SyncQueue + SyncScheduler (BackgroundService)
  Services/Ldap/             module A: LdapSettings, LdapSettingsService, LdapServerHost (BackgroundService)
  Services/Mail/             MailService (MatSMTP)
  MatPlatform/               copy of the shared package (MatDesign, 2.4.0). Never edit here.
  Platform/                  MatContactHubPlatform (provides/consumes, launcher, widgets, health, settings, actions syncAll/cleanUp), MatContactHubIdentityUsers
  Controls/                  ONE code base per control: <md-field>, <md-buttons>, <md-table>, <md-tabs>/<md-tab>
  Pages/                     Contacts (Index = start page, Edit), AddressBooks (Index, Edit), Duplicates (Index), Sources (Index, Edit; administrators),
                             Admin (Ldap, Users, Connections, Activity, System with tabs), Account, About, Error; Shared/_Toolbar, _Pager, _Empty, _FormSummary
  Resources/Texts/           the catalog, one file per area: Contacts.json (C), Sources.json (B), Ldap.json (A), Admin/Account/Common/Data/Js/Landing/Lists/Shell
  wwwroot/                   css/app.css (accent + md-*), js (offline, pwa, util, zone), sw.js (shell cache, the contact pages network-first with a copy),
                             manifest, robots.txt, llms.txt, img (logo, PWA icons, app-icons.svg)
tests/MatContactHub.Tests    unit + database tests
```

## Ownership of the modules

The foundation (0.1) built everything shared; four modules extend it in parallel. **Each module owns its folder, its pages, its Texts file and its tests**, and
touches shared files only where listed. The signatures of the stubs may change inside a module; what other modules call is marked *shared* and stays.

| Module | Owns | Shared files it may touch |
|---|---|---|
| **A – LDAP server** | `Services/Ldap/**`, `Pages/Admin/Ldap/**`, `Resources/Texts/Ldap.json`, tests `Ldap*Tests.cs`; the LDAP columns of `SystemSetting` and `LdapAccount*` | `Program.cs` (its block "The LDAP server"), `app.css` (a section "LDAP" at the end) |
| **B – Data sources + sync** | `Services/Sources/**`, `Pages/Sources/**`, `Resources/Texts/Sources.json`, tests `Source*/Sync*Tests.cs`; `DataSource`, `FieldMapping`, `SyncRun` | `Program.cs` (its block "The data sources"), the sync parts of `Platform/MatContactHubPlatform.cs` (widget `syncRuns`, health `sync`, action `syncAll`), `app.css` (section "Sources") |
| **C – Contacts, address books, duplicates, directory API** | `Services/Contacts/**` except the shared files below, `Pages/Contacts/**`, `Pages/AddressBooks/**`, `Pages/Duplicates/**`, `Resources/Texts/Contacts.json`, tests `Contact*/AddressBook*/Duplicate*/Directory*Tests.cs`; `AddressBook*`, `Contact`, `DuplicateIgnore` | `Pages/Shared/_Layout.cshtml` (the top search, suggestions), `MenuService`, `sw.js` (read pages), `app.css` (section "Contacts") |
| **D – Logo + PWA icons** | `scripts/logo.js`, `scripts/icons.ps1`, `docs/logo/**`, `wwwroot/img/**`, `wwwroot/favicon.svg` | `Ui.Accent`, the accent lines of `app.css`, `manifest.webmanifest` and the `theme-color` metas (colours only) |

**Shared, owned by the foundation (change only additively, never break a signature):**
- `Services/Contacts/PhoneNormalizer.cs` – `Normalize(raw, defaultCountry)` → E.164 or null; `Format(number, NumberFormat, defaultCountry)`; `Digits(raw)`;
  `IsKnownCountry(code)`; `MinimumDigits` (4). Used by B (transform `Phone`), C (duplicates, search), A (attributes, phone filters).
- `Services/Contacts/DirectoryChanges.cs` – `Version`, `Bump()`, event `Changed`. `AppDbContext.SaveChanges` bumps after any change of `Contact`, `AddressBook`,
  `AddressBookAccess`, `LdapAccount`, `LdapAccountAddressBook`, `SystemSetting`. **Bulk changes past the change tracker (`ExecuteUpdate/Delete`, raw SQL) and saves
  inside an explicit transaction MUST call `DirectoryChanges.Bump()` after the commit.** A reads it to rebuild its snapshot.
- `Services/Contacts/AddressBookService.RolesAsync/RoleAsync` – THE permission check of the address books (B and A read address books through it or as admin/system).
- `Services/Actor.cs`, `Services/ActivityLog.cs` (actions: contact, addressbook, source, sync, ldap, user, setting – add an action there and its text
  `Activity.Action.<action>` in Admin.json), `Data/*.cs` and `AppDbContext`.
- **Schema changes:** the data model of the spec is complete in `InitialCreate`. A module that still needs a column adds **one** migration named after it
  (`LdapCertificateDetails`, `SourcesPreviewCache` …) and expects to re-create it on merge (the model snapshot conflicts between parallel branches).

## Conventions

**Database (PostgreSQL)**: tables PascalCase and singular, key always `Id` BIGINT, every record has `CreateDate/CreateUserId/UpdateDate/UpdateUserId` (filled by
`AppDbContext.SaveChanges`), `UpdateState` (0 deleted, 1 created, 2 updated) for soft delete (`User`, `AddressBook`, `Contact`, `DataSource`, `LdapAccount`; a query
filter hides deleted rows). A `Token` (uuid) where a key is a security feature or an address (`UserSession`, `Contact` = LDAP entryUUID/uid and vCard UID,
`DataSource`, `SyncRun`). `Contact` is unique per (`DataSourceId`, `ExternalKey`) where `DataSourceId` is set – also over deleted rows, so a record that returns
comes back as the same contact (`IgnoreQueryFilters` when upserting). `AddressBook.LdapName` and `LdapAccount.Name` are unique among the rows that exist and **ASCII in small letters** (LDAP compares ou and cn
without case, so `Kunden` and `kunden` would be one DN; check constraints `CK_AddressBook_LdapName_Lower` / `CK_LdapAccount_Name_Lower` refuse anything else:
lower-case what a person types, and compare a bind name in small letters). The
settings are the one row of `SystemSetting` (Id 1, `SettingsStore`), including the LDAP settings and the shared `DefaultCountry`.

**Security**: every page and API call goes through the business layer, which checks the actor again (static classes taking `AppDbContext` + `Actor`); what may not
be seen answers 404 (`Outcome.Denied` / null). Secrets of data sources are protected with DataProtection (keys in the volume) and never go back to the browser.
`/Admin/**` and `/Sources/**` need the role Admin (Razor conventions) and the business layer checks `IsAdmin` once more. The directory API checks the key of a
binding (`MatBindingAccess.Require`) and is rate-limited (`directory`).

**UI (MatDesign rules)**: CRUD = list page (toolbar above, table, pager under it, actions left below, positive → negative) + separate edit page (Save, Back, other
buttons, space, Delete); entities with several areas get **tabs** (contact: General / Communication / Address / More / Source; address book: General / Access /
LDAP & apps; data source: General / Connection / Mapping / Preview / Runs; LDAP: General / Certificate / Accounts / Log; System: General / Directory). Hide form
parts that make no sense for the chosen type (`show-if`). Icons via `Ui.Icon` (toolkit sprite + `wwwroot/img/app-icons.svg`). Every `td` has `data-label`. The
version at the foot of the menu links to `/About`. `pattern` attributes must be valid with the `v` flag.

**Code**: Microsoft C# guidelines, file-scoped namespaces, `.editorconfig`. The business layer is static classes taking the context and the actor; no database
queries in page models or views beyond calling the business layer. English identifiers and comments, UI texts only in the catalog (one file per area).

**Compose files**: no environment variables to set, no placeholders; a stack (`name:`) per file; `docker-compose.yml` (release, `matcontacthub`, 7087 + 389/636),
`.dev.yml` (`matcontacthub-dev`, nightly + pgAdmin, 7389/7636), `.local.yml` (`matcontacthub-local`, build + pgAdmin + Postgres 7089, 7389/7636, subnet
10.253.87.0/24). Volumes `web-data` (`/data`: keys, ldap, sources), `db-data`, `pgadmin-data`.

**Git**: commit under the user's name; never add a Claude/Anthropic co-author trailer or a "generated with" line (global rule of the user; it overrides any default).
Branches: `main` = release, `dev` = development. Versions: release `<major>.<minor>.<build>-<date>`, development `nightly-<build>-<date>`, local `local-<date>`
(`Directory.Build.props`; `.github/workflows/build.yml` builds, tests with `MATCONTACTHUB_TEST_DB` and pushes the image `ghcr.io/real-ttx/matcontacthub`).
