using System.Security.Claims;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public static class Dashboard
{
    /// Cada sección sale solo si el rol tiene permiso sobre su módulo: el dashboard no filtra datos que el módulo ocultaría.
    public static void MapDashboard(this WebApplication app)
    {
        app.MapGet("/api/dashboard", async (AppDb db, ClaimsPrincipal user) =>
        {
            bool Can(string module) => user.HasClaim(Modules.ClaimType, module);
            var today = DateOnly.FromDateTime(DateTime.Today);
            // ponytail: meses en UTC; si el corte de mes a medianoche local importa, convertir con la zona de la empresa.
            var monthStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var sixMonthsAgo = monthStart.AddMonths(-5);

            object? billing = null, sales = null, inventory = null, purchases = null, agent = null, cash = null;

            if (Can("facturacion"))
            {
                var issued = db.Invoices.Where(i => i.Status == "emitida" || i.Status == "pagada");
                var byMonth = await issued.Where(i => i.IssuedAt >= sixMonthsAgo)
                    .GroupBy(i => new { i.IssuedAt!.Value.Year, i.IssuedAt.Value.Month })
                    .Select(g => new { g.Key.Year, g.Key.Month, Amount = g.Sum(i => i.Subtotal) })
                    .ToListAsync();
                billing = new
                {
                    MonthSales = await issued.Where(i => i.IssuedAt >= monthStart).SumAsync(i => i.Subtotal),
                    Paid = await issued.Where(i => i.Status == "pagada").SumAsync(i => i.Total),
                    Current = await issued.Where(i => i.Status == "emitida" && i.DueDate >= today).SumAsync(i => i.Total - i.Paid - i.Credited),
                    Overdue = await issued.Where(i => i.Status == "emitida" && i.DueDate < today).SumAsync(i => i.Total - i.Paid - i.Credited),
                    OverdueCount = await issued.CountAsync(i => i.Status == "emitida" && i.DueDate < today),
                    Drafts = await db.Invoices.CountAsync(i => i.Status == "borrador"),
                    // Los 6 meses siempre, con 0 donde no hubo ventas: una barra ausente se lee como dato faltante.
                    Monthly = Enumerable.Range(0, 6).Select(k => sixMonthsAgo.AddMonths(k)).Select(m => new
                    {
                        Month = $"{m:yyyy-MM}",
                        Amount = byMonth.FirstOrDefault(x => x.Year == m.Year && x.Month == m.Month)?.Amount ?? 0,
                    }),
                };
            }

            if (Can("ventas"))
            {
                var open = db.Opportunities.Where(o => o.Stage != "ganada" && o.Stage != "perdida");
                var weekEnd = today.AddDays(7);
                sales = new
                {
                    Pipeline = await open.SumAsync(o => o.Amount),
                    OpenCount = await open.CountAsync(),
                    ClosingSoon = await open.Where(o => o.ExpectedClose != null && o.ExpectedClose <= weekEnd)
                        .OrderBy(o => o.ExpectedClose)
                        .Select(o => new { o.Title, o.Amount, o.ExpectedClose }).Take(5).ToListAsync(),
                };
            }

            if (Can("inventario"))
            {
                var low = await db.Products.Where(p => p.Active)
                    .Select(p => new { p.Sku, p.Name, p.MinStock, Stock = db.InventoryMovements.Where(m => m.ProductId == p.Id).Sum(m => m.Quantity) })
                    .Where(p => p.Stock < p.MinStock)
                    .OrderBy(p => p.Stock / (p.MinStock == 0 ? 1 : p.MinStock))
                    .ToListAsync();
                inventory = new { LowCount = low.Count, Low = low.Take(5) };
            }

            if (Can("compras"))
            {
                var pending = db.PurchaseOrders.Where(o => o.Status == "enviada");
                purchases = new
                {
                    PendingCount = await pending.CountAsync(),
                    PendingAmount = await pending.SumAsync(o => o.Total),
                    Drafts = await db.PurchaseOrders.CountAsync(o => o.Status == "borrador"),
                };
            }

            if (Can("ia"))
            {
                // Solo propuestas que este rol puede aprobar.
                var kinds = new List<string>();
                if (Can("facturacion")) kinds.Add("factura");
                if (Can("compras")) kinds.Add("orden_compra");
                agent = new
                {
                    Pending = await db.AgentActions.Where(a => a.Status == "pendiente" && kinds.Contains(a.Kind))
                        .OrderByDescending(a => a.CreatedAt).Select(a => new { a.Id, a.Kind, a.Summary, a.CreatedAt }).Take(5).ToListAsync(),
                };
            }

            if (Can("contabilidad"))
            {
                // Saldo de caja y bancos al cierre de cada uno de los últimos 6 meses.
                var cashIds = await db.Accounts.Where(a => a.Code == Contabilidad.Caja || a.Code == Contabilidad.Bancos).Select(a => a.Id).ToListAsync();
                var byMonth = await db.JournalLines.Where(l => cashIds.Contains(l.AccountId))
                    .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { e.Date.Year, e.Date.Month, Delta = l.Debit - l.Credit })
                    .GroupBy(x => new { x.Year, x.Month }).Select(g => new { g.Key.Year, g.Key.Month, Delta = g.Sum(x => x.Delta) }).ToListAsync();
                cash = new
                {
                    Monthly = Enumerable.Range(0, 6).Select(k => sixMonthsAgo.AddMonths(k)).Select(m => new
                    {
                        Month = $"{m:yyyy-MM}",
                        Balance = byMonth.Where(x => x.Year < m.Year || (x.Year == m.Year && x.Month <= m.Month)).Sum(x => x.Delta),
                    }),
                    (await Pronosticos.CashAsync(db)).Forecast,
                };
            }

            return new { billing, sales, inventory, purchases, agent, cash };
        }).RequireAuthorization("dashboard");
    }
}
