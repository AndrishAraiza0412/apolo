using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public record CustomerRequest(string Name, string? Rfc, string? Email, string? Phone, int CreditDays, bool Active = true,
    string? TaxRegime = null, string? ZipCode = null, string? CfdiUse = null);
public record OpportunityRequest(Guid CustomerId, string Title, decimal Amount, string Stage, DateOnly? ExpectedClose, Guid? OwnerId);

public static class Ventas
{
    internal static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// Clientes y proveedores comparten estas reglas.
    internal static Dictionary<string, string[]> ValidateContact(string name, string? rfc, int creditDays)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name)) e["name"] = ["Nombre requerido."];
        if (rfc is { Length: > 0 } && rfc.Trim().Length is not (12 or 13)) e["rfc"] = ["El RFC tiene 12 o 13 caracteres."];
        if (creditDays is < 0 or > 365) e["creditDays"] = ["Días de crédito entre 0 y 365."];
        return e;
    }

    /// Los datos fiscales son opcionales al capturar; si vienen, deben ser válidos. Se exigen completos al timbrar.
    static Dictionary<string, string[]> ValidateCustomer(CustomerRequest r)
    {
        var e = ValidateContact(r.Name, r.Rfc, r.CreditDays);
        if (Clean(r.TaxRegime) is { } reg && !Cfdi.Regimes.ContainsKey(reg)) e["taxRegime"] = ["Régimen fiscal inválido."];
        if (Clean(r.ZipCode) is { } zip && !Cfdi.ValidZip(zip)) e["zipCode"] = ["Código postal de 5 dígitos."];
        if (Clean(r.CfdiUse) is { } use && !Cfdi.Uses.ContainsKey(use)) e["cfdiUse"] = ["Uso de CFDI inválido."];
        return e;
    }

    /// Cliente y responsable se buscan con el filtro de empresa: un id de otra empresa no existe aquí.
    public static async Task<Dictionary<string, string[]>> ValidateAsync(AppDb db, OpportunityRequest r)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.Title)) e["title"] = ["Título requerido."];
        if (r.Amount < 0) e["amount"] = ["El monto no puede ser negativo."];
        if (!Opportunity.Stages.Contains(r.Stage)) e["stage"] = ["Etapa inválida."];
        if (!await db.Customers.AnyAsync(c => c.Id == r.CustomerId)) e["customerId"] = ["Cliente inválido."];
        if (r.OwnerId is Guid o && !await db.Users.AnyAsync(u => u.Id == o)) e["ownerId"] = ["Responsable inválido."];
        return e;
    }

    public static void MapVentas(this WebApplication app)
    {
        var c = app.MapGroup("/api/customers").RequireAuthorization("ventas");

        c.MapGet("/", (AppDb db, string? q) =>
        {
            var query = db.Customers.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(x => EF.Functions.ILike(x.Name, $"%{q}%") || EF.Functions.ILike(x.Rfc!, $"%{q}%"));
            return query.OrderBy(x => x.Name).Select(x => new
            {
                x.Id, x.Name, x.Rfc, x.Email, x.Phone, x.CreditDays, x.Active, x.TaxRegime, x.ZipCode, x.CfdiUse,
                OpenAmount = db.Opportunities
                    .Where(o => o.CustomerId == x.Id && o.Stage != "ganada" && o.Stage != "perdida")
                    .Sum(o => o.Amount),
            }).ToListAsync();
        });

        c.MapPost("/", async (CustomerRequest req, AppDb db) =>
        {
            var errors = ValidateCustomer(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var x = new Customer
            {
                Name = req.Name.Trim(), Rfc = Clean(req.Rfc)?.ToUpperInvariant(), Email = Clean(req.Email),
                Phone = Clean(req.Phone), CreditDays = req.CreditDays,
                TaxRegime = Clean(req.TaxRegime), ZipCode = Clean(req.ZipCode), CfdiUse = Clean(req.CfdiUse) ?? "G03",
            };
            db.Customers.Add(x);
            await db.SaveChangesAsync();
            return Results.Created($"/api/customers/{x.Id}", new { x.Id });
        });

        c.MapPut("/{id:guid}", async (Guid id, CustomerRequest req, AppDb db) =>
        {
            var errors = ValidateCustomer(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var x = await db.Customers.SingleOrDefaultAsync(z => z.Id == id);
            if (x is null) return Results.NotFound();
            x.Name = req.Name.Trim();
            x.Rfc = Clean(req.Rfc)?.ToUpperInvariant();
            x.Email = Clean(req.Email);
            x.Phone = Clean(req.Phone);
            x.CreditDays = req.CreditDays;
            x.Active = req.Active;
            (x.TaxRegime, x.ZipCode, x.CfdiUse) = (Clean(req.TaxRegime), Clean(req.ZipCode), Clean(req.CfdiUse) ?? "G03");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        var o = app.MapGroup("/api/opportunities").RequireAuthorization("ventas");

        o.MapGet("/", (AppDb db) =>
            db.Opportunities.OrderByDescending(x => x.Amount).Select(x => new
            {
                x.Id, x.CustomerId, x.Title, x.Amount, x.Stage, x.ExpectedClose, x.OwnerId, x.UpdatedAt,
                Customer = db.Customers.Where(z => z.Id == x.CustomerId).Select(z => z.Name).First(),
                Owner = db.Users.Where(u => u.Id == x.OwnerId).Select(u => u.Name).FirstOrDefault(),
            }).ToListAsync());

        o.MapPost("/", async (OpportunityRequest req, AppDb db) =>
        {
            var errors = await ValidateAsync(db, req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var x = new Opportunity
            {
                CustomerId = req.CustomerId, Title = req.Title.Trim(), Amount = req.Amount,
                Stage = req.Stage, ExpectedClose = req.ExpectedClose, OwnerId = req.OwnerId,
            };
            db.Opportunities.Add(x);
            await db.SaveChangesAsync();
            return Results.Created($"/api/opportunities/{x.Id}", new { x.Id });
        });

        o.MapPut("/{id:guid}", async (Guid id, OpportunityRequest req, AppDb db) =>
        {
            var errors = await ValidateAsync(db, req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var x = await db.Opportunities.SingleOrDefaultAsync(z => z.Id == id);
            if (x is null) return Results.NotFound();
            x.CustomerId = req.CustomerId;
            x.Title = req.Title.Trim();
            x.Amount = req.Amount;
            x.Stage = req.Stage;
            x.ExpectedClose = req.ExpectedClose;
            x.OwnerId = req.OwnerId;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Para el selector de responsable; solo nombres de la propia empresa.
        app.MapGet("/api/sellers", (AppDb db) =>
            db.Users.Where(u => u.Active).OrderBy(u => u.Name).Select(u => new { u.Id, u.Name }).ToListAsync())
            .RequireAuthorization("ventas");
    }
}
