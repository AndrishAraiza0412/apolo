using System.Security.Claims;
using Apolo.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public record LoginRequest(string Email, string Password);
public record SetupRequest(string Token, string Company, string Name, string Email, string Password);
public record ThemeRequest(string Theme);
public record NewUserRequest(string Name, string Email, string Password, Guid RoleId);
public record UserUpdateRequest(Guid RoleId, bool Active, string? Password = null);
public record PasswordRequest(string Current, string Next);
public record CompanyRequest(string Name, string? Rfc, string? LegalName, string? TaxRegime, string? ZipCode, decimal? RiskPremium = null);

public static class Endpoints
{
    static readonly PasswordHasher<User> Hasher = new();
    // Se verifica contra este hash cuando el correo no existe, para que la respuesta tarde lo mismo.
    static readonly string DummyHash = Hasher.HashPassword(null!, "apolo-dummy");

    public const string VersionClaim = "ver";
    const int MaxFailedLogins = 5;
    static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    /// Huella de lo que autoriza la sesión: si cambia la contraseña, el rol, sus módulos o el usuario se desactiva, la cookie deja de valer.
    public static string SessionVersion(User u) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{u.PasswordHash}|{u.RoleId}|{u.Active}|{string.Join(",", u.Role.Modules.Order())}")))[..16];

    static AuditEntry Event(User u, string action, string? detail = null) =>
        new() { CompanyId = u.CompanyId, Action = action, Entity = nameof(User), EntityId = u.Id, Changes = detail, CreatedBy = u.Id };

    public static string? PasswordError(string? p) =>
        p is null || p.Length < 12 ? "Mínimo 12 caracteres." : !p.Any(char.IsLetter) || !p.Any(char.IsDigit) ? "Combina letras y números." : null;

    public static void MapAuth(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest req, AppDb db, HttpContext http) =>
        {
            var email = req.Email.Trim().ToLowerInvariant();
            var user = await db.Users.IgnoreQueryFilters().Include(u => u.Role)
                .SingleOrDefaultAsync(u => u.Email == email && u.Active);

            var ok = Hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? DummyHash, req.Password);
            if (user?.LockedUntil > DateTime.UtcNow)
                return Results.Problem("Cuenta bloqueada por intentos fallidos. Intenta de nuevo en 15 minutos o pide a un administrador que la desbloquee.", statusCode: 423);
            if (user is null || ok == PasswordVerificationResult.Failed)
            {
                if (user is not null)
                {
                    user.FailedLogins++;
                    db.AuditEntries.Add(Event(user, "acceso fallido", $"{{\"intento\":{user.FailedLogins}}}"));
                    if (user.FailedLogins >= MaxFailedLogins)
                    {
                        (user.LockedUntil, user.FailedLogins) = (DateTime.UtcNow + Lockout, 0);
                        db.AuditEntries.Add(Event(user, "bloqueo"));
                    }
                    await db.SaveChangesAsync();
                }
                return Results.Problem("Correo o contraseña incorrectos.", statusCode: 401);
            }
            if (ok == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = Hasher.HashPassword(user, req.Password);
            (user.FailedLogins, user.LockedUntil) = (0, null);
            db.AuditEntries.Add(Event(user, "acceso", $"{{\"ip\":\"{http.Connection.RemoteIpAddress}\"}}"));
            await db.SaveChangesAsync();

            // ponytail: los módulos viajan en la cookie; un cambio de rol aplica al siguiente login.
            // Si hace falta revocar al instante, validar el rol en OnValidatePrincipal.
            List<Claim> claims =
            [
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Name),
                new(CurrentUser.CompanyClaim, user.CompanyId.ToString()),
                new(VersionClaim, SessionVersion(user)),
                .. user.Role.Modules.Select(m => new Claim(Modules.ClaimType, m)),
            ];
            await http.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.NoContent();
        }).RequireRateLimiting("login");

        // Alta de la primera empresa en una instalación vacía. Pide el token de instalación (Setup__Token) para que nadie más
        // que quien despliega pueda reclamar el sistema; después de la primera empresa queda cerrado.
        app.MapGet("/api/setup", async (AppDb db) => new { Needed = !await db.Companies.AnyAsync() });

        app.MapPost("/api/setup", async (SetupRequest req, AppDb db, IConfiguration config) =>
        {
            var token = config["Setup:Token"];
            if (string.IsNullOrEmpty(token) || await db.Companies.AnyAsync()) return Results.NotFound();
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(req.Token ?? ""), System.Text.Encoding.UTF8.GetBytes(token)))
                return Results.Problem("Token de instalación incorrecto.", statusCode: 403);
            var email = req.Email.Trim().ToLowerInvariant();
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(req.Company)) errors["company"] = ["Requerido."];
            if (string.IsNullOrWhiteSpace(req.Name)) errors["name"] = ["Requerido."];
            if (!email.Contains('@')) errors["email"] = ["Correo inválido."];
            if (PasswordError(req.Password) is { } bad) errors["password"] = [bad];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var company = new Company { Name = req.Company.Trim() };
            var roles = Seed.DefaultRoles(company.Id);
            var admin = new User { CompanyId = company.Id, RoleId = roles[0].Id, Name = req.Name.Trim(), Email = email };
            admin.PasswordHash = Hasher.HashPassword(admin, req.Password);
            db.Companies.Add(company);
            db.Roles.AddRange(roles);
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireRateLimiting("login");

        app.MapPost("/api/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync();
            return Results.NoContent();
        });

        var me = app.MapGroup("/api/me").RequireAuthorization();

        me.MapGet("/", async (AppDb db, CurrentUser cu) =>
        {
            var u = await db.Users.Include(x => x.Role).SingleAsync(x => x.Id == cu.UserId);
            var company = await db.Companies.SingleAsync(c => c.Id == u.CompanyId);
            return Results.Ok(new
            {
                u.Id, u.Name, u.Email, u.Theme,
                Role = u.Role.Name,
                Company = new { company.Name, company.Currency },
                Modules = Modules.All.Where(m => u.Role.Modules.Contains(m.Code)),
            });
        });

        me.MapPut("/password", async (PasswordRequest req, AppDb db, CurrentUser cu, HttpContext http) =>
        {
            var u = await db.Users.Include(x => x.Role).SingleAsync(x => x.Id == cu.UserId);
            if (Hasher.VerifyHashedPassword(u, u.PasswordHash, req.Current) == PasswordVerificationResult.Failed)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["current"] = ["La contraseña actual no es correcta."] });
            if (PasswordError(req.Next) is { } bad) return Results.ValidationProblem(new Dictionary<string, string[]> { ["next"] = [bad] });
            u.PasswordHash = Hasher.HashPassword(u, req.Next);
            db.AuditEntries.Add(Event(u, "cambio de contraseña"));
            await db.SaveChangesAsync();
            await http.SignOutAsync(); // las demás sesiones también caen: cambió la huella
            return Results.NoContent();
        });

        me.MapPut("/theme", async (ThemeRequest req, AppDb db, CurrentUser cu) =>
        {
            if (req.Theme is not ("light" or "dark")) return Results.BadRequest();
            var u = await db.Users.SingleAsync(x => x.Id == cu.UserId);
            u.Theme = req.Theme;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    public static void MapConfiguracion(this WebApplication app)
    {
        app.MapGet("/api/cfdi/catalogs", () => new { Cfdi.Regimes, Cfdi.Uses, Cfdi.CancelReasons }).RequireAuthorization();

        var g = app.MapGroup("/api").RequireAuthorization("configuracion");

        g.MapGet("/users", (AppDb db) => db.Users.OrderBy(u => u.Name)
            .Select(u => new { u.Id, u.Name, u.Email, Role = u.Role.Name, u.RoleId, u.Active, Locked = u.LockedUntil > DateTime.UtcNow }).ToListAsync());

        // Altas, bajas y cambios de rol (ISO 27001 A.5.18). Una baja o cambio de rol cierra las sesiones abiertas del usuario.
        g.MapPut("/users/{id:guid}", async (Guid id, UserUpdateRequest req, AppDb db, CurrentUser cu) =>
        {
            var u = await db.Users.Include(x => x.Role).SingleOrDefaultAsync(x => x.Id == id);
            if (u is null) return Results.NotFound();
            if (!await db.Roles.AnyAsync(r => r.Id == req.RoleId))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["roleId"] = ["Rol inválido."] });
            if (id == cu.UserId && (!req.Active || req.RoleId != u.RoleId))
                return Results.Problem("No puedes desactivarte ni cambiar tu propio rol.", statusCode: 409);
            if (!string.IsNullOrEmpty(req.Password))
            {
                if (PasswordError(req.Password) is { } bad) return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [bad] });
                u.PasswordHash = Hasher.HashPassword(u, req.Password);
            }
            (u.RoleId, u.Active, u.FailedLogins, u.LockedUntil) = (req.RoleId, req.Active, 0, null);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/users", async (NewUserRequest req, AppDb db) =>
        {
            var email = req.Email.Trim().ToLowerInvariant();
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(req.Name)) errors["name"] = ["Requerido."];
            if (!email.Contains('@')) errors["email"] = ["Correo inválido."];
            if (PasswordError(req.Password) is { } bad) errors["password"] = [bad];
            if (!await db.Roles.AnyAsync(r => r.Id == req.RoleId)) errors["roleId"] = ["Rol inválido."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email))
                return Results.Problem("Ya existe un usuario con ese correo.", statusCode: 409);

            var user = new User { Name = req.Name.Trim(), Email = email, RoleId = req.RoleId };
            user.PasswordHash = Hasher.HashPassword(user, req.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Created($"/api/users/{user.Id}", new { user.Id });
        });

        // Datos fiscales del emisor. Con RFC, las facturas se timbran como CFDI 4.0.
        g.MapGet("/company", (AppDb db, CurrentUser me) => db.Companies.Where(c => c.Id == me.CompanyId)
            .Select(c => new { c.Name, c.Rfc, c.LegalName, c.TaxRegime, c.ZipCode, c.Currency, c.RiskPremium }).SingleAsync());

        g.MapPut("/company", async (CompanyRequest req, AppDb db, CurrentUser me) =>
        {
            var rfc = Ventas.Clean(req.Rfc)?.ToUpperInvariant();
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(req.Name)) errors["name"] = ["Nombre requerido."];
            if (rfc is not null)
            {
                if (!Cfdi.ValidRfc(rfc)) errors["rfc"] = ["RFC inválido."];
                if (string.IsNullOrWhiteSpace(req.LegalName)) errors["legalName"] = ["La razón social es obligatoria para facturar."];
                if (!Cfdi.Regimes.ContainsKey(req.TaxRegime ?? "")) errors["taxRegime"] = ["Elige el régimen fiscal."];
                if (!Cfdi.ValidZip(req.ZipCode)) errors["zipCode"] = ["Código postal de 5 dígitos."];
            }
            // Art. 74 LSS: la prima va de 0.5% a 15%.
            if (req.RiskPremium is < 0.005m or > 0.15m) errors["riskPremium"] = ["La prima de riesgo va de 0.5% a 15%."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var c = await db.Companies.SingleAsync(x => x.Id == me.CompanyId);
            if (req.RiskPremium is decimal risk) c.RiskPremium = Math.Round(risk, 7);
            (c.Name, c.Rfc, c.LegalName, c.TaxRegime, c.ZipCode) = (req.Name.Trim(), rfc, Ventas.Clean(req.LegalName), Ventas.Clean(req.TaxRegime), Ventas.Clean(req.ZipCode));
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/roles", (AppDb db) => db.Roles.OrderBy(r => r.Name)
            .Select(r => new { r.Id, r.Name, r.Modules }).ToListAsync());

        g.MapGet("/audit", (AppDb db, int take = 100) =>
            db.AuditEntries.OrderByDescending(a => a.CreatedAt).Take(Math.Clamp(take, 1, 500))
                .Select(a => new
                {
                    a.Id, a.CreatedAt, a.Action, a.Entity, a.EntityId, a.Changes,
                    User = db.Users.Where(u => u.Id == a.CreatedBy).Select(u => u.Name).FirstOrDefault(),
                }).ToListAsync());
    }
}
