using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

/// Servicio estadístico de pronósticos (DESIGN.md §4): el cálculo es determinista y vive aquí; Apolo IA solo lo explica.
public static class Pronosticos
{
    public record DayPoint(DateOnly Date, decimal Stock);
    public record Demand(Guid ProductId, string Sku, string Name, string Unit, decimal Stock, decimal MinStock, decimal DailyDemand,
        int? DaysToMinimum, int? DaysToStockout, decimal SuggestedOrder, List<DayPoint> Days);

    public record CashPoint(string Month, decimal Balance);
    public record CashItem(string Concept, decimal Amount);
    public record Cash(decimal Current, List<CashPoint> Forecast, List<CashItem> Items, List<string> Assumptions);

    const int History = 90, Horizon = 14, Cover = 30;
    const decimal Alpha = 0.1m;

    static decimal Round(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    /// Demanda diaria = promedio móvil exponencial (α = 0.1) de las salidas de los últimos 90 días, con días sin venta en cero.
    // ponytail: sin estacionalidad semanal ni tendencia; pasar a Holt-Winters cuando haya un año de historia.
    public static async Task<List<Demand>> DemandAsync(AppDb db, Guid? productId = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var since = today.AddDays(-History + 1).ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var products = await db.Products.Where(p => p.Active && (productId == null || p.Id == productId)).OrderBy(p => p.Sku)
            .Select(p => new { p.Id, p.Sku, p.Name, p.Unit, p.MinStock, Stock = db.InventoryMovements.Where(m => m.ProductId == p.Id).Sum(m => m.Quantity) })
            .ToListAsync();
        var ids = products.Select(p => p.Id).ToList();
        var outs = (await db.InventoryMovements.Where(m => ids.Contains(m.ProductId) && m.Kind == "salida" && m.CreatedAt >= since)
                .Select(m => new { m.ProductId, m.CreatedAt, m.Quantity }).ToListAsync())
            .GroupBy(m => (m.ProductId, Day: DateOnly.FromDateTime(m.CreatedAt.ToLocalTime())))
            .ToDictionary(g => g.Key, g => -g.Sum(m => m.Quantity));

        return products.Select(p =>
        {
            var series = Enumerable.Range(0, History).Select(i => outs.GetValueOrDefault((p.Id, today.AddDays(-History + 1 + i)))).ToList();
            var ewma = series.Take(14).Average();
            foreach (var x in series.Skip(14)) ewma = Alpha * x + (1 - Alpha) * ewma;
            var demand = Round(ewma);
            int? daysToMin = demand > 0 ? (int)Math.Max(0, Math.Floor((p.Stock - p.MinStock) / demand) + 1) : null;
            int? daysToOut = demand > 0 ? (int)Math.Ceiling(p.Stock / demand) : null;
            if (p.Stock < p.MinStock) daysToMin = 0;
            return new Demand(p.Id, p.Sku, p.Name, p.Unit, p.Stock, p.MinStock, demand, daysToMin, daysToOut,
                Math.Max(0, Math.Ceiling(demand * Cover + p.MinStock - p.Stock)),
                Enumerable.Range(1, Horizon).Select(k => new DayPoint(today.AddDays(k), Round(p.Stock - demand * k))).ToList());
        }).ToList();
    }

    /// Saldo de caja y bancos al cierre de los próximos 3 meses: lo ya comprometido más promedios recientes.
    public static async Task<Cash> CashAsync(AppDb db)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var ends = Enumerable.Range(1, 3).Select(k => monthStart.AddMonths(k).AddDays(-1)).ToList();
        var horizon = ends[^1];
        var events = new List<(DateOnly Date, string Concept, decimal Amount)>();

        var cashIds = await db.Accounts.Where(a => a.Code == Contabilidad.Caja || a.Code == Contabilidad.Bancos).Select(a => a.Id).ToListAsync();
        var current = await db.JournalLines.Where(l => cashIds.Contains(l.AccountId))
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { l.Debit, l.Credit, e.Date })
            .Where(x => x.Date <= today).SumAsync(x => x.Debit - x.Credit);

        // Cobranza de facturas emitidas: al vencimiento; las vencidas, dentro de 30 días.
        foreach (var i in await db.Invoices.Where(i => i.Status == "emitida").Select(i => new { i.DueDate, Total = i.Total - i.Paid - i.Credited }).ToListAsync())
            events.Add((i.DueDate < today ? today.AddDays(30) : i.DueDate ?? today, "Cobranza de facturas emitidas", i.Total));

        // Pagos a proveedores: recibidas al vencer su crédito; enviadas, una semana para recibir más el crédito.
        var credit = await db.Suppliers.ToDictionaryAsync(s => s.Id, s => s.CreditDays);
        foreach (var o in await db.PurchaseOrders.Where(o => o.Status == "recibida" || o.Status == "enviada")
                     .Select(o => new { o.Status, o.SupplierId, o.ReceivedAt, o.SentAt, Total = o.Total - o.Paid }).ToListAsync())
        {
            var start = DateOnly.FromDateTime((o.ReceivedAt ?? (o.SentAt ?? DateTime.UtcNow).AddDays(7)).ToLocalTime());
            var due = start.AddDays(credit.GetValueOrDefault(o.SupplierId));
            events.Add((due < today ? today : due, "Pagos a proveedores", -o.Total));
        }

        // Nómina: el neto promedio de las últimas pagadas, cada quincena que aún no se paga.
        var paid = await db.PayrollRuns.Where(r => r.Status == "pagada").OrderByDescending(r => r.EndDate).Take(2).ToListAsync();
        var lastPaidEnd = paid.Count > 0 ? paid[0].EndDate : today;
        if (paid.Count > 0)
        {
            var net = Round(paid.Average(r => r.Net));
            for (var m = monthStart; m <= horizon; m = m.AddMonths(1))
                foreach (var payday in new[] { m.AddDays(14), m.AddMonths(1).AddDays(-1) })
                    if (payday > lastPaidEnd && payday <= horizon) events.Add((payday < today ? today : payday, "Nómina", -net));
        }

        // Promedios de los últimos 3 meses completos: gastos fijos (6xx sin sueldos) y facturación nueva.
        var from3 = monthStart.AddMonths(-3);
        var fixedIds = await db.Accounts.Where(a => a.Type == "gasto" && a.Code.StartsWith("6") && a.Code != Contabilidad.Sueldos).Select(a => a.Id).ToListAsync();
        var fixedAvg = Round(await db.JournalLines.Where(l => fixedIds.Contains(l.AccountId))
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { l.Debit, l.Credit, e.Date })
            .Where(x => x.Date >= from3 && x.Date < monthStart).SumAsync(x => x.Debit - x.Credit) / 3);
        var fromUtc = from3.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtc = monthStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var billedAvg = Round(await db.Invoices.Where(i => (i.Status == "emitida" || i.Status == "pagada") && i.IssuedAt >= fromUtc && i.IssuedAt < toUtc)
            .SumAsync(i => i.Total) / 3);
        for (var m = monthStart.AddMonths(1); m <= horizon; m = m.AddMonths(1))
        {
            if (fixedAvg > 0) events.Add((m.AddDays(4), "Gastos fijos (promedio)", -fixedAvg));
            // Lo que se facture de hoy en adelante se cobra al mes siguiente.
            if (billedAvg > 0) events.Add((m.AddDays(14), "Ventas futuras (promedio)", billedAvg));
        }

        events = events.Where(e => e.Date <= horizon).ToList();
        return new Cash(current,
            ends.Select(end => new CashPoint($"{end:yyyy-MM}", current + events.Where(e => e.Date <= end).Sum(e => e.Amount))).ToList(),
            events.GroupBy(e => e.Concept).Select(g => new CashItem(g.Key, g.Sum(e => e.Amount))).OrderByDescending(x => x.Amount).ToList(),
            [
                "Facturas emitidas se cobran al vencer; las vencidas, dentro de 30 días.",
                "Órdenes recibidas se pagan al vencer el crédito del proveedor; las enviadas, una semana después más el crédito.",
                "Nómina: neto promedio de las dos últimas quincenas pagadas.",
                $"Gastos fijos y ventas nuevas: promedio mensual de {from3:MMM yyyy} a {monthStart.AddDays(-1):MMM yyyy}.",
                "No incluye compras nuevas no capturadas ni pagos de impuestos.",
            ]);
    }

    public static void MapPronosticos(this WebApplication app)
    {
        var g = app.MapGroup("/api/forecast");

        // Inventario y Compras usan el pronóstico de demanda.
        g.MapGet("/demand", (AppDb db) => DemandAsync(db))
            .RequireAuthorization(p => p.RequireClaim(Modules.ClaimType, "inventario", "compras"));
        g.MapGet("/demand/{id:guid}", async (Guid id, AppDb db) =>
                (await DemandAsync(db, id)).SingleOrDefault() is { } d ? Results.Ok(d) : Results.NotFound())
            .RequireAuthorization(p => p.RequireClaim(Modules.ClaimType, "inventario", "compras"));
        g.MapGet("/cash", (AppDb db) => CashAsync(db)).RequireAuthorization("contabilidad");
    }
}
