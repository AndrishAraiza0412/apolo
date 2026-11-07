using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public record ProductRequest(string Sku, string Name, string? Unit, decimal Cost, decimal Price, decimal MinStock, bool Active = true,
    string? SatCode = null, string? SatUnit = null);
public record MovementRequest(string Kind, decimal Quantity, string? Note);

public static class Inventario
{
    /// Registra un movimiento sin dejar la existencia negativa. Devuelve el mensaje de error o null.
    /// El producto debe existir en la empresa del usuario (lo valida quien llama).
    public static async Task<string?> RegisterAsync(AppDb db, Guid productId, string kind, decimal quantity, string? note)
    {
        decimal? delta = kind switch
        {
            "entrada" when quantity > 0 => quantity,
            "salida" when quantity > 0 => -quantity,
            "ajuste" when quantity != 0 => quantity,
            _ => null,
        };
        if (delta is null) return "Tipo o cantidad inválidos.";

        var relational = db.Database.IsRelational();
        // Si ya hay transacción (p. ej. al emitir una factura) se une a ella; si no, abre la suya.
        await using var tx = relational && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync() : null;
        // Bloquea la fila del producto: dos salidas simultáneas no pueden leer la misma existencia.
        if (relational) await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Products\" WHERE \"Id\" = {productId} FOR UPDATE");

        var stock = await Stock(db, productId);
        if (stock + delta < 0) return $"Existencia insuficiente: hay {stock:0.####}.";

        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = productId,
            Kind = kind,
            Quantity = delta.Value,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        });
        await db.SaveChangesAsync();
        if (tx is not null) await tx.CommitAsync();
        return null;
    }

    public static async Task<decimal> Stock(AppDb db, Guid productId) =>
        await db.InventoryMovements.Where(m => m.ProductId == productId).SumAsync(m => m.Quantity);

    static Dictionary<string, string[]> Validate(ProductRequest r)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.Sku)) e["sku"] = ["SKU requerido."];
        if (string.IsNullOrWhiteSpace(r.Name)) e["name"] = ["Nombre requerido."];
        if (r.Cost < 0 || r.Price < 0 || r.MinStock < 0) e["price"] = ["Costo, precio y mínimo no pueden ser negativos."];
        if (Ventas.Clean(r.SatCode) is { } code && !Cfdi.ValidSatCode(code)) e["satCode"] = ["Clave de producto SAT de 8 dígitos."];
        if (Ventas.Clean(r.SatUnit) is { Length: > 3 }) e["satUnit"] = ["Clave de unidad SAT de hasta 3 caracteres (H87, E48, KGM…)."];
        return e;
    }

    public static void MapInventario(this WebApplication app)
    {
        var g = app.MapGroup("/api/products").RequireAuthorization("inventario");

        g.MapGet("/", (AppDb db, string? q) =>
        {
            var query = db.Products.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(p => EF.Functions.ILike(p.Sku, $"%{q}%") || EF.Functions.ILike(p.Name, $"%{q}%"));
            return query.OrderBy(p => p.Sku).Select(p => new
            {
                p.Id, p.Sku, p.Name, p.Unit, p.SatCode, p.SatUnit, p.Cost, p.Price, p.MinStock, p.Active,
                Stock = db.InventoryMovements.Where(m => m.ProductId == p.Id).Sum(m => m.Quantity),
            }).ToListAsync();
        });

        g.MapPost("/", async (ProductRequest req, AppDb db) =>
        {
            var errors = Validate(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var sku = req.Sku.Trim().ToUpperInvariant();
            if (await db.Products.AnyAsync(p => p.Sku == sku))
                return Results.Problem($"Ya existe el SKU {sku}.", statusCode: 409);

            var p = new Product
            {
                Sku = sku, Name = req.Name.Trim(), Unit = string.IsNullOrWhiteSpace(req.Unit) ? "pza" : req.Unit.Trim(),
                SatCode = Ventas.Clean(req.SatCode) ?? "01010101", SatUnit = Ventas.Clean(req.SatUnit)?.ToUpperInvariant() ?? "H87",
                Cost = req.Cost, Price = req.Price, MinStock = req.MinStock,
            };
            db.Products.Add(p);
            await db.SaveChangesAsync();
            return Results.Created($"/api/products/{p.Id}", new { p.Id });
        });

        g.MapPut("/{id:guid}", async (Guid id, ProductRequest req, AppDb db) =>
        {
            var errors = Validate(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var p = await db.Products.SingleOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            var sku = req.Sku.Trim().ToUpperInvariant();
            if (await db.Products.AnyAsync(x => x.Sku == sku && x.Id != id))
                return Results.Problem($"Ya existe el SKU {sku}.", statusCode: 409);

            p.Sku = sku;
            p.Name = req.Name.Trim();
            p.Unit = string.IsNullOrWhiteSpace(req.Unit) ? "pza" : req.Unit.Trim();
            (p.SatCode, p.SatUnit) = (Ventas.Clean(req.SatCode) ?? "01010101", Ventas.Clean(req.SatUnit)?.ToUpperInvariant() ?? "H87");
            p.Cost = req.Cost;
            p.Price = req.Price;
            p.MinStock = req.MinStock;
            p.Active = req.Active;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/{id:guid}/movements", (Guid id, AppDb db) =>
            db.InventoryMovements.Where(m => m.ProductId == id).OrderByDescending(m => m.CreatedAt).Take(200)
                .Select(m => new
                {
                    m.Id, m.CreatedAt, m.Kind, m.Quantity, m.Note,
                    User = db.Users.Where(u => u.Id == m.CreatedBy).Select(u => u.Name).FirstOrDefault(),
                }).ToListAsync());

        g.MapPost("/{id:guid}/movements", async (Guid id, MovementRequest req, AppDb db) =>
        {
            if (!await db.Products.AnyAsync(p => p.Id == id)) return Results.NotFound();
            var error = await RegisterAsync(db, id, req.Kind, req.Quantity, req.Note);
            return error is null ? Results.NoContent() : Results.Problem(error, statusCode: 422);
        });
    }
}
