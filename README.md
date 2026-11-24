<p align="center"><img src="docs/logo/matcontacthub-logo.svg" alt="MatContactHub" width="280"></p>

# MatContactHub

A **contact directory of your own** with a web interface – the idea of estos MetaDirectory for the Mat family:

- **Data sources**: contacts from **REST APIs**, **Microsoft SQL Server** and **CSV files** are imported, unified by a field mapping per source and kept in sync
  automatically.
- **Contacts** are the centre: a list with search, filters and sort, manual contacts next to the synchronised ones, **address books** with roles per person, and
  **duplicate** detection (the same number, e-mail address or name and company).
- A built-in **LDAP server** (LDAP, StartTLS and LDAPS) serves selected address books to STARFACE, IP phones (Yealink, Snom, Grandstream …) and mail clients –
  a number matches in any notation (`+4930123`, `030123`, `004930123`).
- Apps look numbers up and search the shared address books through the capability **`contacts.directory`** (a key per app).

Everything is configured in the web interface. Part of the Mat family: the look is [MatDesign](https://github.com/Real-TTX/MatDesign)'s MatUi, the platform
contract is MatPlatform 2.4 (contract 2). English and German, a PWA (installable, the contacts seen last stay readable offline).

> **State: 0.1 foundation.** The shell, the accounts, the administration, the data model and the platform contract are in place; the LDAP server, the sync of
> the data sources, the full contact pages, the duplicates and the final logo are being built (see CLAUDE.md, "Ownership").

## Start

```bash
docker compose up -d          # docker-compose.yml: MatContactHub and PostgreSQL; web 7087, LDAP 389, LDAPS 636
```

Open <http://localhost:7087>: the first account that registers is the administrator. Then **Address books → New address book**, **Administration → Data
sources** and **Administration → LDAP**.

| Port (host) | Container | What |
|---|---|---|
| 7087 | 7087 | the web interface |
| 389 (dev/local: 7389) | 10389 | LDAP, plain and StartTLS |
| 636 (dev/local: 7636) | 10636 | LDAPS |

## Settings

No variable is required. All of these are optional and set by Mattiverse when it installs MatContactHub; without it they go under `environment:` of `web`.

| Variable | Meaning |
|---|---|
| `MAT_PUBLIC_URL` | the public address (links in mails, the directory API); while set, it is fixed on Administration → System |
| `MAT_API_KEY` | the key of the platform API (`/api`) for a broker |
| `MAT_FRAME_ANCESTORS` | the origins that may show MatContactHub in a frame (Mattiverse) |
| `MATIDENTITY_AUTHORITY`, `_INTERNAL_URL`, `_CLIENT_ID`, `_CLIENT_SECRET` | sign-in through MatIdentity |
| `MATSMTP_HOST`, `_PORT`, `_USERNAME`, `_PASSWORD`, `_FROM`, `_TLS` | mail through MatSMTP |

**Provides `contacts.directory`** (a key capability): a binding is a key for the consumer (`MAT_CONTACTS_DIRECTORY_URL`, `_INTERNAL_URL`, `_KEY`); the app calls
`GET /api/provides/contacts.directory/lookup?number=` and `/search?q=&limit=` (also under `/api/directory/`) with `Authorization: Bearer <key>` and reads the
address books shared with apps.

## Development

- `./scripts/redeploy.ps1` (or `.sh`) rebuilds the image and starts `docker-compose.local.yml`: the app on <http://localhost:7087>, LDAP on `localhost:7389`,
  LDAPS on `localhost:7636`, pgAdmin on <http://localhost:7088>, PostgreSQL on `localhost:7089`. `-Clean` starts with empty volumes.
- `dotnet build MatContactHub.slnx`, `dotnet test` (the database tests run with `MATCONTACTHUB_TEST_DB`, an administrator connection string of a PostgreSQL, e.g.
  `Host=localhost;Port=7089;Username=matcontacthub;Password=matcontacthub;Database=postgres` while the local stack runs).
- Compose files: `docker-compose.yml` (release, `:latest`), `docker-compose.dev.yml` (nightly, with pgAdmin), `docker-compose.local.yml` (built here, with pgAdmin).
- Versions: release `<major>.<minor>.<build>-<date>` (branch `main`), nightly `nightly-<build>-<date>` (branch `dev`), local `local-<date>`; the GitHub action builds,
  tests and pushes `ghcr.io/real-ttx/matcontacthub` for both branches (amd64 and arm64).
- The logo in its levels of detail: `node scripts/logo.js` (SVGs), `./scripts/icons.ps1` (PNGs with Edge).

Made by Matthias Schmoldt.
