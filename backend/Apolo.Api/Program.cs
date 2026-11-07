using System.Threading.RateLimiting;
using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
// Producción: logs en JSON para enviarlos a un colector (A.8.15). No se registran cuerpos de petición ni contraseñas.
if (!builder.Environment.IsDevelopment()) builder.Logging.AddJsonConsole();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddHostedService<Tareas.Worker>();
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
// Las llaves que cifran la cookie de sesión sobreviven reinicios y nuevas versiones del contenedor.
if (builder.Configuration["DataProtection:Path"] is { Length: > 0 } keys)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("apolo");
// Detrás del proxy con TLS (Caddy): esquema e IP real del cliente para cookies seguras y el límite de intentos.
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
        // Es una API: 401/403 en lugar de redirigir a una página de login.
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
        // Revocación: cada petición confirma que el usuario sigue activo y con la misma contraseña y permisos.
        // Ámbito propio para no fijar un CurrentUser vacío en el ámbito de la petición.
        // ponytail: una consulta por petición; con mucho tráfico, revalidar cada pocos minutos.
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

// Una política por módulo; los permisos se aplican aquí, el menú solo los refleja (DESIGN.md §2).
builder.Services.AddAuthorization(o =>
{
    foreach (var m in Modules.All) o.AddPolicy(m.Code, p => p.RequireClaim(Modules.ClaimType, m.Code));
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Límite general por usuario (o IP sin sesión) contra abuso y extracción masiva.
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

// Migraciones al arrancar (una sola instancia). Los datos demo y el acceso admin/admin solo existen en desarrollo;
// en producción la primera empresa se da de alta con /api/setup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment()) await Seed.RunAsync(db);
}

app.UseForwardedHeaders();
// Encabezados de seguridad. La CSP permite el script de tema de index.html por su hash y las fuentes de Google.
var csp = $"default-src 'self'; script-src 'self' {Security.InlineScriptHashes(app.Environment.WebRootPath)}; " +
    "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data:; " +
    "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
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

// Monitoreo de disponibilidad (SOC 2 A1): responde 200 si la API llega a la base.
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

// En producción la misma API sirve la interfaz compilada (wwwroot); cualquier ruta que no sea /api cae en index.html.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("{*path:regex(^(?!api/).*$)}", "index.html");

app.Run();
