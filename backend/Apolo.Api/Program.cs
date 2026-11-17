using System.Threading.RateLimiting;
using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
// Production: JSON logs to send to a collector (A.8.15). Request bodies and passwords are not logged.
if (!builder.Environment.IsDevelopment()) builder.Logging.AddJsonConsole(o => o.IncludeScopes = true); // the scopes carry the RequestId of each line

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddHostedService<Tareas.Worker>();
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
// The keys that encrypt the session cookie survive restarts and new container versions.
if (builder.Configuration["DataProtection:Path"] is { Length: > 0 } keys)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("apolo");
// Behind the TLS proxy (Caddy): scheme and real client IP for secure cookies and the attempt limit.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "apolo";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        // It is an API: 401/403 instead of redirecting to a login page.
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
        // Revocation: each request confirms the user is still active and has the same password and permissions.
        // Own scope so an empty CurrentUser is not set in the request scope.
        // one query per request; with heavy traffic, revalidate every few minutes.
        o.Events.OnValidatePrincipal = async c =>
        {
            using var scope = c.HttpContext.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var id = Guid.TryParse(c.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var g) ? g : Guid.Empty;
            var user = await db.Users.IgnoreQueryFilters().Include(u => u.Role).SingleOrDefaultAsync(u => u.Id == id);
            if (user is null || !user.Active || c.Principal?.FindFirst(Endpoints.VersionClaim)?.Value != Endpoints.SessionVersion(user))
            {
                c.RejectPrincipal();
                await c.HttpContext.SignOutAsync();
            }
        };
    });

// One policy per module; permissions are enforced here, the menu only reflects them (DESIGN.md §2).
builder.Services.AddAuthorization(o =>
{
    foreach (var m in Modules.All) o.AddPolicy(m.Code, p => p.RequireClaim(Modules.ClaimType, m.Code));
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // General limit per user (or per IP without a session) against abuse and bulk extraction.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(
        c.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(
        c.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
{
    if (c.Exception is PeriodClosedException e) (c.ProblemDetails.Title, c.ProblemDetails.Detail) = ("Periodo cerrado", e.Message);
});

var app = builder.Build();

// Migrations on startup (single instance). The demo data and the admin/admin access exist only in development;
// in production the first company is created with /api/setup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment()) await Seed.RunAsync(db);
}

app.UseForwardedHeaders();
// Security headers. The CSP allows the theme script in index.html by its hash and Google fonts.
var csp = $"default-src 'self'; script-src 'self' {Security.InlineScriptHashes(app.Environment.WebRootPath)}; " +
    "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data:; " +
    "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Request-Id"] = ctx.TraceIdentifier;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h["Content-Security-Policy"] = csp;
    if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
    await next();
});
if (!app.Environment.IsDevelopment()) app.UseHsts();

app.UseExceptionHandler(new ExceptionHandlerOptions { StatusCodeSelector = e => e switch { PeriodClosedException => 422, BadHttpRequestException b => b.StatusCode, _ => 500 } });
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Availability monitoring (SOC 2 A1): returns 200 if the API reaches the database.
app.MapGet("/healthz", async (AppDb db) => await db.Database.CanConnectAsync() ? Results.Text("ok") : Results.StatusCode(503)).DisableRateLimiting();
app.MapAuth();
app.MapConfiguracion();
app.MapInventario();
app.MapVentas();
app.MapFacturacion();
app.MapCompras();
app.MapProyectos();
app.MapIa();
app.MapContabilidad();
app.MapFiscal();
app.MapRrhh();
app.MapReportes();
app.MapPronosticos();
app.MapTareas();
app.MapBancos();
app.MapImpuestos();
app.MapDashboard();

// In production the same API serves the built interface (wwwroot); any route that is not /api falls back to index.html.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("{*path:regex(^(?!api/).*$)}", "index.html");

app.Run();
