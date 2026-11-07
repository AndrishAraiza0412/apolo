using System.Security.Claims;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

/// Reportes tabulares. Cada uno exige permiso sobre el módulo de sus datos, además del de Reportes.
public static class Reportes
{
    public record Column(string Key, string Label, string Type = "text");
    public record Table(string Title, List<Column> Columns, List<Dictionary<string, object?>> Rows);
    record Report(string Code, string Name, string Module, string Description, bool Period, Func<AppDb, DateOnly, DateOnly, Task<Table>> Run);

    static Dictionary<string, object?> Row(params (string Key, object? Value)[] cells) => cells.ToDictionary(c => c.Key, c => c.Value);
    static DateTime Utc(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    static readonly Report[] All =
    [
        new("ventas-por-cliente", "Ventas por cliente", "facturacion", "Facturas emitidas o pagadas en el periodo, por cliente.", true, async (db, from, to) =>
        {
            var rows = await db.Invoices.Where(i => (i.Status == "emitida" || i.Status == "pagada") && i.IssuedAt >= Utc(from) && i.IssuedAt < Utc(to.AddDays(1)))
                .GroupBy(i => i.CustomerId)
                .Select(g => new { Cliente = db.Customers.Where(c => c.Id == g.Key).Select(c => c.Name).First(), N = g.Count(), Sub = g.Sum(i => i.Subtotal), Iva = g.Sum(i => i.Tax), Total = g.Sum(i => i.Total) })
                .OrderByDescending(x => x.Total).ToListAsync();
            return new("Ventas por cliente", [new("cliente", "Cliente"), new("facturas", "Facturas", "number"), new("subtotal", "Subtotal", "money"), new("iva", "IVA", "money"), new("total", "Total", "money")],
                rows.Select(r => Row(("cliente", r.Cliente), ("facturas", r.N), ("subtotal", r.Sub), ("iva", r.Iva), ("total", r.Total))).ToList());
        }),

        new("antiguedad-saldos", "Antigüedad de saldos", "facturacion", "Cuentas por cobrar por cliente y días de atraso, a hoy.", false, async (db, _, _) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var open = await db.Invoices.Where(i => i.Status == "emitida")
                .Select(i => new { Total = i.Total - i.Paid - i.Credited, i.DueDate, Cliente = db.Customers.Where(c => c.Id == i.CustomerId).Select(c => c.Name).First() }).ToListAsync();
            string Bucket(DateOnly? due) => (today.DayNumber - (due ?? today).DayNumber) switch { <= 0 => "corriente", <= 30 => "d30", <= 60 => "d60", <= 90 => "d90", _ => "mas90" };
            string[] buckets = ["corriente", "d30", "d60", "d90", "mas90"];
            return new("Antigüedad de saldos",
                [new("cliente", "Cliente"), new("corriente", "Al corriente", "money"), new("d30", "1–30 días", "money"), new("d60", "31–60", "money"), new("d90", "61–90", "money"), new("mas90", "Más de 90", "money"), new("total", "Total", "money")],
                open.GroupBy(i => i.Cliente).OrderByDescending(g => g.Sum(i => i.Total)).Select(g =>
                {
                    var row = Row(("cliente", g.Key), ("total", g.Sum(i => i.Total)));
                    foreach (var b in buckets) row[b] = g.Where(i => Bucket(i.DueDate) == b).Sum(i => i.Total);
                    return row;
                }).ToList());
        }),

        new("valuacion-inventario", "Valuación de inventario", "inventario", "Existencia actual por producto al último costo.", false, async (db, _, _) =>
        {
            var rows = await db.Products.Where(p => p.Active).OrderBy(p => p.Sku).Select(p => new
            {
                p.Sku, p.Name, p.Unit, p.Cost, Stock = db.InventoryMovements.Where(m => m.ProductId == p.Id).Sum(m => m.Quantity),
            }).ToListAsync();
            return new("Valuación de inventario",
                [new("sku", "SKU"), new("producto", "Producto"), new("existencia", "Existencia", "number"), new("unidad", "Unidad"), new("costo", "Costo", "money"), new("valor", "Valor", "money")],
                rows.Select(r => Row(("sku", r.Sku), ("producto", r.Name), ("existencia", r.Stock), ("unidad", r.Unit), ("costo", r.Cost), ("valor", Math.Round(r.Stock * r.Cost, 2)))).ToList());
        }),

        new("compras-por-proveedor", "Compras por proveedor", "compras", "Órdenes recibidas en el periodo, por proveedor, con lo pendiente de pago.", true, async (db, from, to) =>
        {
            var rows = await db.PurchaseOrders.Where(o => (o.Status == "recibida" || o.Status == "pagada") && o.ReceivedAt >= Utc(from) && o.ReceivedAt < Utc(to.AddDays(1)))
                .GroupBy(o => o.SupplierId)
                .Select(g => new
                {
                    Proveedor = db.Suppliers.Where(s => s.Id == g.Key).Select(s => s.Name).First(), N = g.Count(), Total = g.Sum(o => o.Total),
                    PorPagar = g.Where(o => o.Status == "recibida").Sum(o => o.Total),
                }).OrderByDescending(x => x.Total).ToListAsync();
            return new("Compras por proveedor", [new("proveedor", "Proveedor"), new("ordenes", "Órdenes", "number"), new("total", "Total", "money"), new("porPagar", "Por pagar", "money")],
                rows.Select(r => Row(("proveedor", r.Proveedor), ("ordenes", r.N), ("total", r.Total), ("porPagar", r.PorPagar))).ToList());
        }),

        new("estado-resultados", "Estado de resultados", "contabilidad", "Ingresos, costos y gastos del periodo, desde las pólizas.", true, async (db, from, to) =>
        {
            var b = (await Contabilidad.BalancesAsync(db, from, to)).Where(a => a.Type is "ingreso" or "gasto").ToList();
            var rows = b.Where(a => a.Debit != 0 || a.Credit != 0)
                .Select(a => Row(("cuenta", $"{a.Code} {a.Name}"), ("tipo", a.Type == "ingreso" ? "Ingreso" : "Costo o gasto"), ("importe", Contabilidad.Balance(a.Type, a.Debit, a.Credit)))).ToList();
            var utilidad = b.Where(a => a.Type == "ingreso").Sum(a => Contabilidad.Balance(a.Type, a.Debit, a.Credit))
                - b.Where(a => a.Type == "gasto").Sum(a => Contabilidad.Balance(a.Type, a.Debit, a.Credit));
            rows.Add(Row(("cuenta", "Utilidad (pérdida) del periodo"), ("tipo", ""), ("importe", utilidad)));
            return new("Estado de resultados", [new("cuenta", "Cuenta"), new("tipo", "Tipo"), new("importe", "Importe", "money")], rows);
        }),

        new("nomina", "Nómina por periodo", "rrhh", "Nóminas pagadas en el periodo con bruto, deducciones y neto.", true, async (db, from, to) =>
        {
            var rows = await db.PayrollRuns.Where(r => r.Status == "pagada" && r.EndDate >= from && r.StartDate <= to).OrderBy(r => r.StartDate)
                .Select(r => new { r.Folio, r.StartDate, r.EndDate, N = r.Lines.Count, r.Gross, r.Deductions, r.Net }).ToListAsync();
            return new("Nómina por periodo",
                [new("folio", "Folio"), new("periodo", "Periodo"), new("empleados", "Empleados", "number"), new("bruto", "Bruto", "money"), new("deducciones", "Deducciones", "money"), new("neto", "Neto", "money")],
                rows.Select(r => Row(("folio", Rrhh.FolioText(r.Folio)), ("periodo", $"{r.StartDate:dd/MM/yyyy} – {r.EndDate:dd/MM/yyyy}"), ("empleados", r.N), ("bruto", r.Gross), ("deducciones", r.Deductions), ("neto", r.Net))).ToList());
        }),
    ];

    static IEnumerable<Report> For(ClaimsPrincipal user) => All.Where(r => user.HasClaim(Modules.ClaimType, r.Module));

    /// Para Apolo IA: los reportes que el rol puede generar.
    public static IEnumerable<(string Code, string Name)> Available(ClaimsPrincipal user) => For(user).Select(r => (r.Code, r.Name));

    public static async Task<Table?> RunAsync(AppDb db, ClaimsPrincipal user, string code, DateOnly? from, DateOnly? to)
    {
        var r = For(user).FirstOrDefault(x => x.Code == code);
        if (r is null) return null;
        var today = DateOnly.FromDateTime(DateTime.Today);
        return await r.Run(db, from ?? new DateOnly(today.Year, today.Month, 1), to ?? today);
    }

    public static void MapReportes(this WebApplication app)
    {
        var g = app.MapGroup("/api/reports").RequireAuthorization("reportes");

        g.MapGet("/", (ClaimsPrincipal user) => For(user).Select(r => new { r.Code, r.Name, r.Description, r.Period, r.Module }));

        g.MapGet("/{code}", async (string code, DateOnly? from, DateOnly? to, AppDb db, ClaimsPrincipal user) =>
        {
            if (from > to) return Results.Problem("El periodo es inválido.", statusCode: 400);
            return await RunAsync(db, user, code, from, to) is { } t ? Results.Ok(t) : Results.NotFound();
        });
    }
}
