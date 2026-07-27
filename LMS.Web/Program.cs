using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Do not advertise the web server in responses (technology-disclosure hardening, VUL-10).
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

// ---- Database selection: the customer chooses the DB at first-run (setup wizard),
// or a headless deploy sets DatabaseProvider in env/appsettings to skip the wizard.
var setupFilePath = LMS.Web.Services.Setup.SetupStore.FilePath(builder.Configuration, builder.Environment);
var setupFile = LMS.Web.Services.Setup.SetupStore.LoadFile(setupFilePath);          // wizard progress (persisted)
var headless = LMS.Web.Services.Setup.SetupStore.FromConfig(builder.Configuration); // explicit config skips the wizard

LMS.Web.Services.Setup.DbSettings dbSettings;
bool setupMode;
if (setupFile != null && !string.IsNullOrWhiteSpace(setupFile.ConnectionString))
{
    dbSettings = new LMS.Web.Services.Setup.DbSettings
    {
        Provider = LMS.Web.Services.Setup.SetupStore.ParseProvider(setupFile.Provider),
        ConnectionString = setupFile.ConnectionString,
        Configured = true
    };
    setupMode = !setupFile.Completed;    // resume the wizard where it left off
    if (!string.IsNullOrWhiteSpace(setupFile.OllamaUrl))
        builder.Configuration["Ollama:BaseUrl"] = setupFile.OllamaUrl;
}
else if (headless != null)
{
    dbSettings = headless; setupMode = false;
}
else
{
    // Placeholder provider so DI/EF resolve; never created or queried while unconfigured.
    dbSettings = new LMS.Web.Services.Setup.DbSettings
    {
        Provider = LMS.Web.Services.Setup.DbProvider.Sqlite,
        ConnectionString = "Data Source=" + Path.Combine(Path.GetDirectoryName(setupFilePath)!, "setup-placeholder.db"),
        Configured = false
    };
    setupMode = true;
}
builder.Services.AddSingleton(dbSettings);
builder.Services.AddSingleton(new LMS.Web.Services.Setup.SetupState { SetupMode = setupMode, SetupFilePath = setupFilePath });
builder.Services.AddSingleton(new LMS.Web.Services.Setup.SetupWizardState
{
    DatabaseDone = dbSettings.Configured,
    Step = dbSettings.Configured ? LMS.Web.Services.Setup.SetupStep.Organisation : LMS.Web.Services.Setup.SetupStep.Welcome
});

var tenantWriteGuard = new LMS.Web.Data.TenantSaveChangesInterceptor();
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    // Write-side tenant guard (stamps org on insert, blocks cross-tenant writes).
    options.AddInterceptors(tenantWriteGuard);
    // Silence the one expected, by-design required-navigation-filtered warning (see below);
    // SplitQuery avoids the cartesian-explosion perf warning on multi-collection Includes.
    options.ConfigureWarnings(w =>
        w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
    // Provider (SQLite / SQL Server / PostgreSQL / MySQL) applied from the active DbSettings,
    // read per context build so the first-run wizard can switch database without a restart.
    LMS.Web.Services.Setup.DbProviderConfigurator.Apply(options, sp.GetRequiredService<LMS.Web.Services.Setup.DbSettings>());
});

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    // Password policy
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 8;
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedAccount = false;
    // Brute-force protection: lock the account after repeated failures
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    // Require HTTPS for the auth cookie in production; keep SameAsRequest in
    // Development so the local plain-HTTP endpoint (http://localhost:5100) still works.
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

// Validate the sign-in cookie against the database on every request, so
// sessions from before a database rebuild are signed out immediately
// instead of causing foreign-key errors.
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.Zero;
});

// ---- Single sign-on (§AUTH-09): OpenID Connect, configured per organisation in the
// database and applied without a redeploy. The scheme is always registered; the sign-in
// page only offers it once an administrator has enabled a configuration.
builder.Services.AddScoped<LMS.Web.Services.Sso.SsoService>();
builder.Services.AddSingleton<LMS.Web.Services.Sso.SsoOptionsCache>();
// NOTE: must be registered as IConfigureOptions<> — the options factory resolves that
// interface, not IConfigureNamedOptions<>, even though our class implements the named one.
builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<
    Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>,
    LMS.Web.Services.Sso.SsoOptionsConfigurator>();
builder.Services.AddAuthentication()
    .AddOpenIdConnect(LMS.Web.Services.Sso.SsoService.Scheme, _ => { });   // options come from the DB

// Custom organisation roles behave as their mapped platform role (role mapping)
builder.Services.AddScoped<Microsoft.AspNetCore.Identity.IUserClaimsPrincipalFactory<LMS.Web.Models.ApplicationUser>, LMS.Web.Services.MappedRolesClaimsFactory>();

// Short-lived, user-bound access tokens for hosted Knowledge Hub media (SEC-16)
builder.Services.AddSingleton<LMS.Web.Services.MediaTokenService>();

// Lets UploadHelper track files written during a request so orphans (a saved file
// with no committed database row) can be cleaned up if the request fails.
builder.Services.AddHttpContextAccessor();

// Multi-tenant scope resolved per request from the principal; drives the
// AppDbContext global query filters (default-deny tenant isolation).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<LMS.Web.Services.ITenantContext, LMS.Web.Services.HttpTenantContext>();

builder.Services.AddControllersWithViews();
builder.Services.AddMemoryCache();
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
// Subscription-license expiry reminders (first at T-2 months, then weekly)
builder.Services.AddHostedService<LMS.Web.Services.LicenseExpiryNotifier>();

// Subjective auto-grading (§AIG): local Ollama/Qwen 2.5 grading + retrieval index.
builder.Services.AddHttpClient<LMS.Web.Services.Grading.IOllamaClient, LMS.Web.Services.Grading.OllamaClient>();
builder.Services.AddScoped<LMS.Web.Services.Grading.GradingOptions>();
builder.Services.AddScoped<LMS.Web.Services.Grading.KnowledgeIndexService>();
builder.Services.AddScoped<LMS.Web.Services.Grading.RetrievalService>();
builder.Services.AddScoped<LMS.Web.Services.Grading.ISubjectiveGrader, LMS.Web.Services.Grading.OllamaSubjectiveGrader>();
builder.Services.AddScoped<LMS.Web.Services.Grading.GradingService>();
builder.Services.AddScoped<LMS.Web.Services.Grading.QuizGenerator>();
builder.Services.AddHostedService<LMS.Web.Services.Grading.SubjectiveGradingWorker>();

var app = builder.Build();

// Create the database and seed demo data. If the seed version changed
// (new schema or new sample data), rebuild the database automatically.
// Create/seed the database — only once a provider is configured. In first-run setup mode
// the wizard performs this on its Save step (against the customer's chosen database).
if (!setupMode)
    await LMS.Web.Services.Setup.DbInitializer.InitializeAsync(app.Services);

// Give UploadHelper access to the current request so it can track written files.
LMS.Web.Services.UploadHelper.ConfigureTracking(app.Services.GetRequiredService<IHttpContextAccessor>());

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Orphan-file guard: if a request throws after a file was written but before the row
// that references it is committed, delete the uncommitted file so it can never be
// left saved with no owning database row. Placed inside the exception handler so the
// exception still reaches this catch before the error page renders.
app.Use(async (context, next) =>
{
    try { await next(); }
    catch { LMS.Web.Services.UploadHelper.CleanupPending(context); throw; }
});

// Behind cloud load balancers / reverse proxies, honour the original scheme
// and client IP so HTTPS detection, cookies and generated URLs are correct.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                       Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});

// Containers usually terminate TLS at the proxy; set LMS_DISABLE_HTTPS_REDIRECT=true there.
if (!string.Equals(Environment.GetEnvironmentVariable("LMS_DISABLE_HTTPS_REDIRECT"), "true", StringComparison.OrdinalIgnoreCase))
    app.UseHttpsRedirection();
app.UseResponseCompression();

// Security headers on every response
app.Use(async (context, next) =>
{
    // Fresh CSP nonce per request; inline <script> blocks echo it via @Context.CspNonce().
    var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    context.Items[LMS.Web.Services.CspExtensions.NonceKey] = nonce;

    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "SAMEORIGIN";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

    // Content-Security-Policy. script-src uses the per-request nonce (no 'unsafe-inline'),
    // so injected scripts cannot run even if markup sanitisation is ever bypassed.
    // Uploaded SCORM/cmi5 content is third-party HTML served under /scorm and relies on
    // its own inline scripts, so the strict policy is not applied there (that content is
    // sandboxed in an iframe and is a separate-origin hardening item).
    if (!context.Request.Path.StartsWithSegments("/scorm"))
    {
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            $"script-src 'self' 'nonce-{nonce}'; " +
            "style-src 'self' 'unsafe-inline'; " +
            // data: for inline images; YouTube thumbnail hosts for Knowledge Hub video cards.
            "img-src 'self' data: https://i.ytimg.com https://*.ytimg.com https://img.youtube.com; " +
            "font-src 'self'; " +
            // 'self' plays hosted videos; blob: lets the Add-Video form read a chosen
            // file's duration client-side before upload.
            "media-src 'self' blob:; " +
            // Knowledge Hub embeds YouTube videos linked by URL (the embed iframe player).
            "frame-src 'self' https://www.youtube.com https://www.youtube-nocookie.com; " +
            "connect-src 'self'; " +
            "object-src 'none'; " +
            "base-uri 'self'; " +
            "frame-ancestors 'self'; " +
            "form-action 'self'";
    }
    await next();
});

// Hosted Knowledge Hub files (uploaded documents & videos) must never be reachable as
// open static files — that would bypass the "download only for Admins" rule. Block the
// two folders here so every request goes through the gated MediaController instead.
// (Other uploads — logos, signatures, lesson files — remain publicly served below.)
app.Use(async (context, next) =>
{
    var p = context.Request.Path;
    if (p.StartsWithSegments("/uploads/documents") || p.StartsWithSegments("/uploads/videos"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

// First-run database setup gate: until a database has been chosen, route every request to
// the setup wizard (static assets still pass through so the page can style itself). Runs
// before authentication so no database is touched while unconfigured.
app.Use(async (context, next) =>
{
    var setup = context.RequestServices.GetRequiredService<LMS.Web.Services.Setup.SetupState>();
    if (setup.SetupMode)
    {
        var p = context.Request.Path;
        bool allowed = p.StartsWithSegments("/Setup")
            || p.StartsWithSegments("/css") || p.StartsWithSegments("/js") || p.StartsWithSegments("/lib")
            || p.StartsWithSegments("/assets") || p.StartsWithSegments("/images") || p.StartsWithSegments("/favicon.ico");
        if (!allowed) { context.Response.Redirect("/Setup"); return; }
    }
    await next();
});

// Long-lived caching for static assets (all local asset URLs are content-hash versioned)
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? "";
        if (path.StartsWith("/assets") || path.StartsWith("/css") || path.StartsWith("/images"))
            ctx.Context.Response.Headers.CacheControl = "public,max-age=2592000";
        else if (path.StartsWith("/uploads") || path.StartsWith("/scorm"))
            ctx.Context.Response.Headers.CacheControl = "private,max-age=3600";
    }
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
