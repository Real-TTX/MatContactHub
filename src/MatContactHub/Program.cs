using System.Globalization;
using System.Reflection;
using System.Threading.RateLimiting;
using MatContactHub;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Platform;
using MatContactHub.Services;
using MatContactHub.Services.Mail;
using MatContactHub.Services.Contacts;
using MatContactHub.Services.Ldap;
using MatContactHub.Services.Sources;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;

if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

// ---- the database (PostgreSQL): the connection string of appsettings.json fits the compose files, no variable is needed ----
// No Kerberos on the database connection (the image has no GSSAPI library: Npgsql would look for it and log an error at every start).
var connection = new Npgsql.NpgsqlConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Default") ?? "Host=db;Port=5432;Database=matcontacthub;Username=matcontacthub;Password=matcontacthub")
{
    GssEncryptionMode = Npgsql.GssEncryptionMode.Disable,
}.ConnectionString;

// A deleted account, address book or contact (soft delete) drops out of every query, also as the end of a required relationship: wanted, so EF need not warn about it.
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection)
    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));

// The keys that protect the cookie and the secrets of the data sources are in the volume: they survive a restart of the container.
var dataDir = builder.Configuration["DataDirectory"] ?? "/data";
var keys = Path.Combine(dataDir, "keys");
Directory.CreateDirectory(keys);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("MatContactHub");

// An uploaded CSV file of a data source may be large (the pages of the sources set their own limit; 100 MB is the most).
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 100L * 1024 * 1024);

builder.Services.AddMemoryCache();
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Assets>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<ExternalSignInService>();
builder.Services.AddScoped<MatContactHub.Services.Auth.AccountDataService>();
builder.Services.AddScoped<MenuService>();
builder.Services.AddScoped<InstanceService>();
builder.Services.AddHostedService<MaintenanceService>();

// The mail through MatSMTP (only when the environment names it).
builder.Services.AddSingleton<MailService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MailService>());

// The data sources (module B): the queue of "Sync now" and the scheduler that runs the syncs in the background.
builder.Services.AddSingleton<SyncQueue>();
builder.Services.AddHostedService<SyncScheduler>();

// The LDAP server (module A): listens on 10389 (LDAP, StartTLS) and 10636 (LDAPS) inside the container.
builder.Services.AddHostedService<LdapServerHost>();

// The Mat platform contract (MatDesign docs/PLATFORM.md, contract 2): /api, /api/health, /api/state, /api/configure, /api/actions with MAT_API_KEY or a token of
// MatIdentity, what MatContactHub provides (contacts.directory: a key per app that reads the shared address books) and consumes (identity.oidc, mail.smtp; shown in Administration → Connections)
// and the contributions to a shell. The package MatPlatform (a copy in MatPlatform/) serves the endpoints; MatContactHubPlatform describes MatContactHub and supplies the
// values through the services. MAT_PUBLIC_URL fixes the public address.
var publicUrl = MatPlatformSettings.From(builder.Configuration, MatContactHubPlatform.AppId).AppPublicUrl;
var platform = builder.AddMatPlatform<MatContactHubPlatform>(o => MatContactHubPlatform.Describe(o, publicUrl), o =>
{
    o.DefaultLanguage = Languages.Default;
    o.DataDirectory = dataDir;
    o.ConnectionsAccess = http => Task.FromResult(http.User.IsInRole(nameof(UserRole.Admin)));
});

// The interface speaks English and German (MatContactHub.Localization); every localizer reads the one catalog of JSON texts.
builder.Services.AddSingleton<Microsoft.Extensions.Localization.IStringLocalizerFactory, CatalogLocalizerFactory>();
builder.Services.AddSingleton(typeof(Microsoft.Extensions.Localization.IStringLocalizer<>), typeof(CatalogLocalizer<>));
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, Languages.UiCulture(Languages.Default));
    options.SupportedCultures = [CultureInfo.InvariantCulture];
    options.SupportedUICultures = [.. Languages.Codes.Select(Languages.UiCulture)];
    options.RequestCultureProviders = [new AccountLanguageProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "matcontacthub.session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Cookie.IsEssential = true;
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Login";
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = SessionService.ValidateAsync;
        o.Events.OnRedirectToLogin = context =>
        {
            // a script or an API call gets a status, not the sign-in page
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/live")
                || context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            else
            {
                context.Response.Redirect(context.RedirectUri);
            }

            return Task.CompletedTask;
        };
    })

    // Sign-in through MatIdentity, only when MATIDENTITY_AUTHORITY is set (nothing changes without it); with it also the back-channel logout.
    .AddMatIdentitySignIn<MatContactHubIdentityUsers>(platform, o =>
    {
        o.CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Login?local=1&denied=1";
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(nameof(UserRole.Admin)));
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Register");
    options.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Sources", "AdminOnly");
}).AddMvcOptions(o =>
{
    o.ModelBinderProviders.Insert(0, new EmptyStringBinderProvider());
    o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // the sign-in form: 10 attempts a minute per address
    o.AddPolicy("signin", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    // the directory API of the apps (caller-ID on every call of a telephone system): 600 a minute per address
    o.AddPolicy("directory", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // behind a reverse proxy (Caddy, nginx, Traefik) the address and the scheme of the visitor are in the headers
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

await Database.MigrateWithRetryAsync(app);

app.UseForwardedHeaders();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
}

app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");
app.UseMiddleware<SecurityHeaders>();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // files are versioned with ?v= (asp-append-version): they can be kept for a long time
        if (ctx.Context.Request.Query.ContainsKey("v"))
        {
            ctx.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        }
        else
        {
            // the modules of the scripts import each other without a version: the browser asks again (an unchanged file answers 304)
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    },
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseRequestLocalization();
app.UseAuthorization();

// The language switch (EN / DE): the cookie for the pages without an account, and the account of a signed-in person.
app.MapPost("/language", async (HttpContext http, CurrentUser me, AppDbContext db, Microsoft.AspNetCore.Antiforgery.IAntiforgery anti) =>
{
    await anti.ValidateRequestAsync(http);
    var form = await http.Request.ReadFormAsync();
    var code = Languages.Normalize(form["language"].ToString());
    Languages.SetCookie(http.Response, code, http.Request.IsHttps);
    if (me.Id is { } id)
    {
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, code));
        SessionService.Invalidate();
    }

    var back = form["returnUrl"].ToString();
    return Results.LocalRedirect(!string.IsNullOrEmpty(back) && back.StartsWith('/') && !back.StartsWith("//", StringComparison.Ordinal) ? back : "/");
}).AllowAnonymous();

app.MapGet("/health", async (AppDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok("ok") : Results.StatusCode(503)).AllowAnonymous();

app.MapDirectoryApi();
app.MapMatPlatform();
app.MapRazorPages();

app.Run();
return 0;

/// <summary>The build: <c>0.1.42-20261118</c> (release), <c>nightly-42-20261118</c> (development) or <c>local-20261118</c>.</summary>
public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "local";
}

public partial class Program;
