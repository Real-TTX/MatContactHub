using System.Text.Json;
using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using MatContactHub.Services.Sources;
using MatPlatform;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Platform;

/// <summary>
/// MatContactHub as an app of the platform contract (MatDesign docs/PLATFORM.md, contract 2) on the shared package MatPlatform (the folder <c>MatPlatform/</c>, a copy):
/// how a consumer such as Mattiverse shows MatContactHub (<see cref="Describe"/>: tiles, the few settings, buttons), what it provides (<c>contacts.directory</c>: an app
/// such as a telephone system gets a key to look numbers up and search the address books shared with apps), what it consumes (the sign-in and the mail that their
/// variables set), what it adds to a shell (the launcher Contacts, widgets for administrators), its values and what its buttons do. The settings are those of the admin
/// page System, kept in the same place (<see cref="InstanceService"/>).
/// </summary>
public sealed class MatContactHubPlatform(InstanceService instance, MatPlatformSettings settings, SyncQueue syncQueue, IServiceScopeFactory scopes,
    ILogger<MatContactHubPlatform> log)
    : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth, IMatPlatformWidgets
{
    /// <summary>The id of MatContactHub in the platform (and the default client id at MatIdentity).</summary>
    public const string AppId = "matcontacthub";

    /// <summary>The accent of MatContactHub (a fuchsia; see <c>wwwroot/css/app.css</c>).</summary>
    public const string Accent = Ui.Accent;

    /// <summary>The MatApiDefinition of MatContactHub (<c>GET /api</c>) without <c>MAT_PUBLIC_URL</c>.</summary>
    public static void Describe(MatPlatformBuilder o) => Describe(o, null);

    /// <summary>
    /// The MatApiDefinition of MatContactHub (<c>GET /api</c>); <c>auth</c> and <c>endpoints</c> are set by MatPlatform. <paramref name="publicUrl"/> is the value of
    /// <c>MAT_PUBLIC_URL</c>: while it is set, the field <c>publicUrl</c> is fixed.
    /// </summary>
    public static void Describe(MatPlatformBuilder o, string? publicUrl)
    {
        var T = o.T;

        o.App(AppId, "MatContactHub", T("Kontaktverzeichnis: Kontakte aus REST, SQL Server und CSV, Adressbücher, Dubletten, LDAP-Server für Telefone",
            "Contact directory: contacts from REST, SQL Server and CSV, address books, duplicates, an LDAP server for phones"), AppInfo.Version,
            "/img/logo/matcontacthub-mark.svg", Accent, "/Contacts");

        // What MatContactHub does for other apps: caller-ID and search in the address books shared with apps. A key capability: MatPlatform keeps a key per app.
        o.Provides(DirectoryApi.Capability, label: T("Stellt Kontakte bereit für", "Serves contacts to"));

        // What MatContactHub uses from other apps: the sign-in and the mail, set by their variables (MatContactHub works without both).
        o.Consumes(MatCapabilities.IdentityOidc, optional: true);
        o.Consumes(MatCapabilities.MailSmtp, optional: true);

        // What MatContactHub adds to the home screen of a shell (Mattiverse): the contacts with the views of its menu, and widgets for administrators.
        o.Launcher("contacts", T("Kontakte", "Contacts"), "/img/logo/matcontacthub-mark.svg", "/Contacts");
        o.QuickAction("contacts", T("Neuer Kontakt", "New contact"), "plus", url: "/Contacts/Edit");
        o.QuickAction("contacts", T("Adressbücher", "Address books"), "book", url: "/AddressBooks");
        o.QuickAction("contacts", T("Dubletten", "Duplicates"), "copy", url: "/Duplicates");
        o.QuickAction("contacts", T("Jetzt synchronisieren", "Sync now"), "refresh", action: "syncAll");
        o.Widget(new MatWidget("directory", T("Verzeichnis", "Directory"), MatWidgetKinds.Tiles, "medium")
        {
            Items = ["contacts", "addressBooks", "sources", "sourcesFailing"],
            Icon = "users",
            Url = "/Contacts",
            Audience = MatAudience.Admin,
        });
        o.Widget(new MatWidget("syncRuns", T("Letzte Synchronisierungen", "Last syncs"), MatWidgetKinds.List, "medium")
        {
            Icon = "refresh",
            Url = "/Sources",
            Refresh = 60,
            Audience = MatAudience.Admin,
        });

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("users", "contacts", "manualContacts", "addressBooks", "ldapAddressBooks", "sources", "sourcesFailing", "syncFailed", "lastSync", "registration",
                "lastActivity"),
            MatSection.Actions("openApp", "openSources", "openLdap", "openUsers", "openConnections", "openActivity", "openSystem", "syncAll", "cleanUp"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Allgemein", "General"), "registrationOpen", "publicUrl", "defaultCountry"));

        o.Tile("users", T("Aktive Benutzer", "Active users"), icon: "users");
        o.Tile("contacts", T("Kontakte", "Contacts"), icon: "user");
        o.Tile("manualContacts", T("Manuelle Kontakte", "Manual contacts"), icon: "edit");
        o.Tile("addressBooks", T("Adressbücher", "Address books"), icon: "book");
        o.Tile("ldapAddressBooks", T("Adressbücher im LDAP", "Address books in LDAP"), icon: "server");
        o.Tile("sources", T("Datenquellen", "Data sources"), icon: "database");
        o.Tile("sourcesFailing", T("Fehlerhafte Datenquellen", "Failing data sources"), icon: "alert");
        o.Tile("syncFailed", T("Fehlgeschlagene Läufe (7 Tage)", "Failed runs (7 days)"), icon: "x-circle");
        o.Tile("lastSync", T("Letzte Synchronisierung", "Last sync"), "time", icon: "refresh");
        o.Tile("registration", T("Registrierung", "Sign-up"), "status", icon: "user");
        o.Tile("lastActivity", T("Letzte Aktivität", "Last activity"), "time", icon: "activity");

        o.Field(new MatField("registrationOpen", T("Registrierung offen", "Open sign-up"), "bool")
        {
            Hint = T("Aus: nur Administratoren legen Konten an.", "Off: only administrators create accounts."),
        });
        var address = new MatField("publicUrl", T("Öffentliche Adresse", "Public address"), "url")
        {
            Max = 300,
            Placeholder = "https://contacts.example.com",
            Hint = T("Für Links in Mails und die Verzeichnis-API; leer: die Adresse des Aufrufs.", "For links in mails and the directory API; empty: the address of the request."),
        };
        o.Field(publicUrl is null ? address : address with
        {
            Fixed = true,
            Hint = T("Festgelegt durch MAT_PUBLIC_URL (Mattiverse); hier lässt sie sich nicht ändern.", "Set by MAT_PUBLIC_URL (Mattiverse); it cannot be changed here."),
        });
        o.Field(new MatField("defaultCountry", T("Standardland der Rufnummern", "Default country of the numbers"), "text")
        {
            Max = 2,
            Placeholder = "DE",
            Hint = T("ISO-Ländercode für Nummern ohne Vorwahl des Landes.", "ISO country code for numbers without a country prefix."),
        });

        o.LinkAction("openApp", T("Öffnen", "Open"), "external", "/Contacts");
        o.LinkAction("openSources", T("Datenquellen", "Data sources"), "database", "/Sources");
        o.LinkAction("openLdap", T("LDAP", "LDAP"), "server", "/Admin/Ldap");
        o.LinkAction("openUsers", T("Benutzer", "Users"), "users", "/Admin/Users");
        o.LinkAction("openConnections", T("Verbindungen", "Connections"), "plug", "/Admin/Connections");
        o.LinkAction("openActivity", T("Aktivität", "Activity"), "activity", "/Admin/Activity");
        o.LinkAction("openSystem", T("System", "System"), "settings", "/Admin/System");
        o.PostAction("syncAll", T("Alle synchronisieren", "Sync all"), "refresh");
        o.PostAction(
            "cleanUp",
            T("Aufräumen", "Clean up"),
            "broom",
            T("Beendete Sitzungen und alte Synchronisierungsläufe jetzt entfernen?", "Remove ended sessions and old sync runs now?"));

        o.Role("admin", T("Administrator", "Administrator"));
        o.Role("user", T("Benutzer", "User"));
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
    {
        if (!await instance.CanConnectAsync(ct))
        {
            return [new MatHealthCheck("database", "down", call.T("Die Datenbank ist nicht erreichbar.", "The database cannot be reached."))];
        }

        var checks = new List<MatHealthCheck> { new("database", "ok") };
        checks.Add(instance.CanWriteFiles()
            ? new MatHealthCheck("files", "ok")
            : new MatHealthCheck("files", "down", call.T("Der Datenordner ist nicht beschreibbar.", "The data folder cannot be written.")));

        var snapshot = await instance.SnapshotAsync(ct);
        if (await instance.AnyUserAsync() && snapshot.Admins == 0)
        {
            checks.Add(new MatHealthCheck("administrators", "degraded", call.T("Kein aktiver Administrator.", "No active administrator.")));
        }

        checks.Add(snapshot.SourcesFailing == 0
            ? new MatHealthCheck("sync", "ok")
            : new MatHealthCheck("sync", "degraded", call.T($"{snapshot.SourcesFailing} Datenquellen schlagen fehl.", $"{snapshot.SourcesFailing} data sources fail.")));
        return checks;
    }

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var s = await instance.SnapshotAsync(ct);
        var st = await instance.SettingsAsync();
        static MatStateValue Time(DateTime? utc) => new(utc is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null);
        return new Dictionary<string, MatStateValue>
        {
            ["users"] = new(s.Users),
            ["contacts"] = new(s.Contacts),
            ["manualContacts"] = new(s.ManualContacts),
            ["addressBooks"] = new(s.AddressBooks),
            ["ldapAddressBooks"] = new(s.LdapAddressBooks),
            ["sources"] = new(s.DataSources),
            ["sourcesFailing"] = s.SourcesFailing > 0 ? new MatStateValue(s.SourcesFailing, "warn") : new MatStateValue(s.SourcesFailing),
            ["syncFailed"] = s.SyncRunsFailed7Days > 0 ? new MatStateValue(s.SyncRunsFailed7Days, "warn") : new MatStateValue(s.SyncRunsFailed7Days),
            ["lastSync"] = Time(s.LastSyncUtc),
            ["registration"] = st.RegistrationOpen
                ? new MatStateValue("info", Text: call.T("Offen", "Open"))
                : new MatStateValue("ok", Text: call.T("Geschlossen", "Closed")),
            ["lastActivity"] = Time(s.LastActivityUtc),
        };
    }

    /// <summary>The list widget <c>syncRuns</c>: the last runs of the data sources, newest first.</summary>
    public async Task<IReadOnlyList<MatWidgetItem>?> GetWidgetAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        if (id != "syncRuns")
        {
            return null;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var runs = await db.SyncRuns.AsNoTracking().OrderByDescending(r => r.StartDate).Take(8)
            .Select(r => new { r.Id, r.DataSourceId, Source = r.DataSource!.Name, r.State, r.StartDate, r.Created, r.Updated, r.Deleted }).ToListAsync(ct);
        return [.. runs.Select(r => new MatWidgetItem(r.Source)
        {
            Subtitle = r.State switch
            {
                SyncState.Running => call.T("Läuft", "Running"),
                SyncState.Succeeded => call.T($"{r.Created} neu, {r.Updated} geändert, {r.Deleted} gelöscht", $"{r.Created} new, {r.Updated} changed, {r.Deleted} deleted"),
                SyncState.Cancelled => call.T("Abgebrochen", "Cancelled"),
                _ => call.T("Fehlgeschlagen", "Failed"),
            },
            Icon = "refresh",
            Url = $"/Sources/Run?id={r.Id}",
            Time = new DateTimeOffset(DateTime.SpecifyKind(r.StartDate, DateTimeKind.Utc)),
            Level = r.State switch { SyncState.Succeeded => "ok", SyncState.Running => "info", SyncState.Cancelled => "warn", _ => "error" },
        })];
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        var st = await instance.SettingsAsync();
        return new Dictionary<string, object?>
        {
            ["registrationOpen"] = st.RegistrationOpen,
            ["publicUrl"] = st.PublicUrl ?? string.Empty,
            ["defaultCountry"] = st.DefaultCountry,
        };
    }

    /// <summary>The values are checked against the definition already (MatPlatform, <c>CheckValues</c>); they are checked once more like on the admin page.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var change = new SettingsChange();
        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "registrationOpen" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    change = change with { RegistrationOpen = value.GetBoolean() };
                    break;
                case "publicUrl" when settings.AppPublicUrl is not null:
                    errors[key] = call.T("Festgelegt durch MAT_PUBLIC_URL.", "Set by MAT_PUBLIC_URL.");
                    break;
                case "publicUrl" when value.ValueKind is JsonValueKind.String:
                    change = change with { PublicUrl = value.GetString()! };
                    break;
                case "publicUrl" when value.ValueKind is JsonValueKind.Null:
                    change = change with { PublicUrl = string.Empty };
                    break;
                case "defaultCountry" when value.ValueKind is JsonValueKind.String && PhoneNormalizer.IsKnownCountry(value.GetString()):
                    change = change with { DefaultCountry = value.GetString()! };
                    break;
                default:
                    errors[key] = call.T("Dieser Wert passt nicht.", "This value does not fit.");
                    break;
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        if (await instance.SaveSettingsAsync(change) is { } error)
        {
            return new Dictionary<string, string> { [char.ToLowerInvariant(error.Field[0]) + error.Field[1..]] = error.Message };
        }

        log.LogInformation("Settings changed through the platform API by {Caller}: {Keys}", call.Caller, string.Join(", ", values.Keys));
        return errors;
    }

    public async Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        switch (id)
        {
            case "syncAll":
                // the platform API is the app's own caller (signed with the platform key)
                DataSourceService.RequestSyncAll(Actor.System("platform"), syncQueue);
                log.LogInformation("Sync of every data source requested through the platform API by {Caller}", call.Caller);
                return new MatActionResult(true, call.T("Die Synchronisierung aller Datenquellen ist angestoßen.", "The sync of every data source has been started."));
            case "cleanUp":
                var result = await instance.CleanUpAsync(ct);
                log.LogInformation("Clean-up through the platform API by {Caller}: {Sessions} sessions, {Runs} sync runs", call.Caller, result.Sessions, result.SyncRuns);
                return new MatActionResult(true, call.T($"Aufgeräumt: {result.Sessions} Sitzungen, {result.SyncRuns} Läufe.",
                    $"Cleaned up: {result.Sessions} sessions, {result.SyncRuns} runs."));
            default:
                return null;
        }
    }
}
