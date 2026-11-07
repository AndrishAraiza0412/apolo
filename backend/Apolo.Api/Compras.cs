using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public record SupplierRequest(string Name, string? Rfc, string? Email, string? Phone, int CreditDays, bool Active = true);
public record PurchaseLineRequest(Guid ProductId, decimal Quantity, decimal UnitCost, decimal TaxRate);
public record PurchaseRequest(Guid SupplierId, string? Notes, List<PurchaseLineRequest> Lines);

public static class Compras
{
    public static string FolioText(int? folio) => folio is int f ? $"OC-{f:0000}" : "Borrador";

    public static async Task<Dictionary<string, string[]>> ValidateAsync(AppDb db, PurchaseRequest r)
    {
        var e = new Dictionary<string, string[]>();
        if (!await db.Suppliers.AnyAsync(s => s.Id == r.SupplierId && s.Active)) e["supplierId"] = ["Proveedor inválido."];
        if (r.Lines is not { Count: > 0 and <= 200 }) e["lines"] = ["La orden necesita entre 1 y 200 partidas."];
        else
        {
            var productIds = r.Lines.Select(l => l.ProductId).Distinct().ToList();
            if (await db.Products.CountAsync(p => productIds.Contains(p.Id) && p.Active) != productIds.Count)
                e["productId"] = ["Producto inválido o inactivo."];
            if (r.Lines.Any(l => l.Quantity <= 0)) e["quantity"] = ["La cantidad debe ser mayor a cero."];
            if (r.Lines.Any(l => l.UnitCost < 0)) e["unitCost"] = ["El costo no puede ser negativo."];
            if (r.Lines.Any(l => !Facturacion.TaxRates.Contains(l.TaxRate))) e["taxRate"] = ["IVA válido: 16%, 8% o 0%."];
        }
        return e;
    }

    internal static void Apply(PurchaseOrder po, PurchaseRequest r)
    {
        po.SupplierId = r.SupplierId;
        po.Notes = Ventas.Clean(r.Notes);
        po.Lines = r.Lines.Select((l, i) => new PurchaseLine
        {
            Position = i, ProductId = l.ProductId, Quantity = l.Quantity, UnitCost = l.UnitCost, TaxRate = l.TaxRate,
        }).ToList();
        (po.Subtotal, po.Tax, po.Total) = Facturacion.Totals(po.Lines.Select(l => (l.Quantity, l.UnitCost, l.TaxRate)));
    }

    /// Cambia de estado bajo bloqueo de fila; `act` corre dentro de la misma transacción. Devuelve el error o null.
    static async Task<string?> Transition(AppDb db, Guid id, Func<PurchaseOrder, Task<string?>> act)
    {
        var relational = db.Database.IsRelational();
        await using var tx = relational ? await db.Database.BeginTransactionAsync() : null;
        if (relational) await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"PurchaseOrders\" WHERE \"Id\" = {id} FOR UPDATE");

        var po = await db.PurchaseOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id);
        if (po is null) return "Orden no encontrada.";
        if (await act(po) is { } error) return error;
        await db.SaveChangesAsync();
        if (tx is not null) await tx.CommitAsync();
        return null;
    }

    public static Task<string?> SendAsync(AppDb db, Guid id) => Transition(db, id, async po =>
    {
        if (po.Status != "borrador") return "Solo se envían borradores.";
        po.Folio = await Facturacion.NextFolioAsync(db, po.CompanyId, "compra");
        po.Status = "enviada";
        po.SentAt = DateTime.UtcNow;
        return null;
    });

    /// Recepción completa: toda la mercancía entra al inventario y el costo del producto pasa a ser el último pagado.
    // ponytail: sin recepciones parciales; agregar cantidad recibida por partida cuando un proveedor entregue en varias exhibiciones.
    public static Task<string?> ReceiveAsync(AppDb db, Guid id) => Transition(db, id, async po =>
    {
        if (po.Status != "enviada") return "Solo se reciben órdenes enviadas.";
        foreach (var l in po.Lines.OrderBy(l => l.Position))
        {
            await Inventario.RegisterAsync(db, l.ProductId, "entrada", l.Quantity, $"Compra {FolioText(po.Folio)}");
            (await db.Products.SingleAsync(p => p.Id == l.ProductId)).Cost = l.UnitCost;
        }
        po.Status = "recibida";
        po.ReceivedAt = DateTime.UtcNow;
        await Contabilidad.PostPurchaseReceivedAsync(db, po);
        return null;
    });

    /// Abono al proveedor (o pago del saldo si no se indica monto) con su póliza de egreso; queda pagada al cubrir el total.
    public static Task<string?> PayAsync(AppDb db, Guid id, DateTime? paidAt = null, decimal? amount = null) => Transition(db, id, async po =>
    {
        if (po.Status != "recibida") return "Solo se pagan órdenes recibidas.";
        var balance = po.Total - po.Paid;
        var paid = Math.Round(amount ?? balance, 2, MidpointRounding.AwayFromZero);
        if (paid <= 0 || paid > balance) return $"El abono debe ser mayor a cero y no mayor al saldo ({balance:C}).";
        var at = paidAt ?? DateTime.UtcNow;
        po.Paid += paid;
        if (po.Paid == po.Total) (po.Status, po.PaidAt) = ("pagada", at);
        await Contabilidad.PostPurchasePaidAsync(db, po, paid, at);
        return null;
    });

    public static Task<string?> CancelAsync(AppDb db, Guid id) => Transition(db, id, po =>
    {
        if (po.Status is not ("borrador" or "enviada")) return Task.FromResult<string?>($"Una orden {po.Status} no se puede cancelar.");
        po.Status = "cancelada";
        return Task.FromResult<string?>(null);
    });

    public static void MapCompras(this WebApplication app)
    {
        var s = app.MapGroup("/api/suppliers").RequireAuthorization("compras");

        s.MapGet("/", (AppDb db, string? q) =>
        {
            var query = db.Suppliers.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(x => EF.Functions.ILike(x.Name, $"%{q}%") || EF.Functions.ILike(x.Rfc!, $"%{q}%"));
            return query.OrderBy(x => x.Name).Select(x => new
            {
                x.Id, x.Name, x.Rfc, x.Email, x.Phone, x.CreditDays, x.Active,
                OpenAmount = db.PurchaseOrders.Where(o => o.SupplierId == x.Id && (o.Status == "enviada" || o.Status == "recibida")).Sum(o => o.Total - o.Paid),
            }).ToListAsync();
        });

        s.MapPost("/", async (SupplierRequest req, AppDb db) =>
        {
            var errors = Ventas.ValidateContact(req.Name, req.Rfc, req.CreditDays);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var x = new Supplier
            {
                Name = req.Name.Trim(), Rfc = Ventas.Clean(req.Rfc)?.ToUpperInvariant(), Email = Ventas.Clean(req.Email),
                Phone = Ventas.Clean(req.Phone), CreditDays = req.CreditDays,
            };
            db.Suppliers.Add(x);
            await db.SaveChangesAsync();
            return Results.Created($"/api/suppliers/{x.Id}", new { x.Id });
        });

        s.MapPut("/{id:guid}", async (Guid id, SupplierRequest req, AppDb db) =>
        {
            var errors = Ventas.ValidateContact(req.Name, req.Rfc, req.CreditDays);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var x = await db.Suppliers.SingleOrDefaultAsync(z => z.Id == id);
            if (x is null) return Results.NotFound();
            x.Name = req.Name.Trim();
            x.Rfc = Ventas.Clean(req.Rfc)?.ToUpperInvariant();
            x.Email = Ventas.Clean(req.Email);
            x.Phone = Ventas.Clean(req.Phone);
            x.CreditDays = req.CreditDays;
            x.Active = req.Active;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        var g = app.MapGroup("/api/purchases").RequireAuthorization("compras");

        g.MapGet("/", async (AppDb db) =>
        {
            var rows = await db.PurchaseOrders.OrderByDescending(o => o.Folio == null).ThenByDescending(o => o.Folio).ThenByDescending(o => o.CreatedAt)
                .Select(o => new
                {
                    o.Id, o.Folio, o.Status, o.SentAt, o.ReceivedAt, o.PaidAt, o.Total, o.Paid,
                    Supplier = db.Suppliers.Where(x => x.Id == o.SupplierId).Select(x => x.Name).First(),
                }).ToListAsync();
            return rows.Select(o => new { o.Id, FolioText = FolioText(o.Folio), o.Status, o.SentAt, o.ReceivedAt, o.PaidAt, o.Total, Balance = o.Status == "recibida" ? o.Total - o.Paid : 0, o.Supplier });
        });

        // Lo que necesita el formulario. Compras no requiere permiso de Inventario.
        g.MapGet("/catalog", async (AppDb db) => new
        {
            Suppliers = await db.Suppliers.Where(x => x.Active).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(),
            Products = await db.Products.Where(p => p.Active).OrderBy(p => p.Sku).Select(p => new
            {
                p.Id, p.Sku, p.Name, p.Unit, p.Cost, p.MinStock,
                Stock = db.InventoryMovements.Where(m => m.ProductId == p.Id).Sum(m => m.Quantity),
            }).ToListAsync(),
        });

        g.MapGet("/{id:guid}", async (Guid id, AppDb db) =>
        {
            var o = await db.PurchaseOrders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id);
            return o is null ? Results.NotFound() : Results.Ok(new
            {
                o.Id, FolioText = FolioText(o.Folio), o.SupplierId, o.Status, o.SentAt, o.ReceivedAt, o.PaidAt,
                o.Subtotal, o.Tax, o.Total, o.Paid, Balance = o.Status == "recibida" ? o.Total - o.Paid : 0, o.Notes,
                // Los abonos son sus pólizas de egreso.
                Payments = await db.JournalEntries.Where(e => e.Source == "compra" && e.SourceId == o.Id && e.Kind == "egreso")
                    .OrderBy(e => e.Date).Select(e => new { e.Date, Amount = e.Lines.Sum(l => l.Debit) }).ToListAsync(),
                Lines = o.Lines.OrderBy(l => l.Position).Select(l => new { l.ProductId, l.Quantity, l.UnitCost, l.TaxRate }),
            });
        });

        g.MapPost("/", async (PurchaseRequest req, AppDb db) =>
        {
            var errors = await ValidateAsync(db, req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var po = new PurchaseOrder();
            Apply(po, req);
            db.PurchaseOrders.Add(po);
            await db.SaveChangesAsync();
            return Results.Created($"/api/purchases/{po.Id}", new { po.Id });
        });

        g.MapPut("/{id:guid}", async (Guid id, PurchaseRequest req, AppDb db) =>
        {
            var po = await db.PurchaseOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id);
            if (po is null) return Results.NotFound();
            if (po.Status != "borrador") return Results.Problem("Solo se pueden editar borradores.", statusCode: 409);
            var errors = await ValidateAsync(db, req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            db.PurchaseLines.RemoveRange(po.Lines);
            Apply(po, req);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/{id:guid}/send", async (Guid id, AppDb db) =>
            await SendAsync(db, id) is { } error ? Results.Problem(error, statusCode: 422) : Results.NoContent());

        g.MapPost("/{id:guid}/receive", async (Guid id, AppDb db) =>
            await ReceiveAsync(db, id) is { } error ? Results.Problem(error, statusCode: 422) : Results.NoContent());

        g.MapPost("/{id:guid}/pay", async (Guid id, AppDb db, decimal? amount) =>
            await PayAsync(db, id, null, amount) is { } error ? Results.Problem(error, statusCode: 422) : Results.NoContent());

        g.MapPost("/{id:guid}/cancel", async (Guid id, AppDb db) =>
            await CancelAsync(db, id) is { } error ? Results.Problem(error, statusCode: 422) : Results.NoContent());
    }
}
