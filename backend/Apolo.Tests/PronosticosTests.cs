using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class PronosticosTests
{
    readonly AppDb db = new(
        new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new CurrentUser { CompanyId = Guid.NewGuid(), UserId = Guid.NewGuid() });

    [Fact]
    public async Task Constant_demand_projects_stockout_and_suggests_order()
    {
        var p = new Product { Sku = "A-1", Name = "Producto", MinStock = 50 };
        db.Products.Add(p);
        db.InventoryMovements.Add(new InventoryMovement { ProductId = p.Id, Kind = "entrada", Quantity = 600 });
        var outs = Enumerable.Range(0, 90).Select(_ => new InventoryMovement { ProductId = p.Id, Kind = "salida", Quantity = -5 }).ToList();
        db.InventoryMovements.AddRange(outs);
        await db.SaveChangesAsync();
        // Una salida de 5 por día durante los últimos 90 días.
        for (var i = 0; i < outs.Count; i++) outs[i].CreatedAt = DateTime.Today.AddDays(-i).AddHours(12).ToUniversalTime();
        await db.SaveChangesAsync();

        var d = Assert.Single(await Pronosticos.DemandAsync(db));
        Assert.Equal((150m, 5m, 21, 30, 50m), (d.Stock, d.DailyDemand, d.DaysToMinimum, d.DaysToStockout, d.SuggestedOrder));
        Assert.Equal(14, d.Days.Count);
        Assert.Equal(145m, d.Days[0].Stock);
    }

    [Fact]
    public async Task Reconciliation_passes_when_books_match_operations()
    {
        var s = new Supplier { Name = "Proveedor" };
        var c = new Customer { Name = "Cliente" };
        var p = new Product { Sku = "A-1", Name = "Producto", Cost = 60, Price = 100 };
        db.AddRange(s, c, p);
        var po = new PurchaseOrder { SupplierId = s.Id, Lines = [new() { ProductId = p.Id, Quantity = 10, UnitCost = 60, TaxRate = 0.16m }] };
        (po.Subtotal, po.Tax, po.Total) = Facturacion.Totals(po.Lines.Select(l => (l.Quantity, l.UnitCost, l.TaxRate)));
        db.PurchaseOrders.Add(po);
        await db.SaveChangesAsync();
        Assert.Null(await Compras.SendAsync(db, po.Id));
        Assert.Null(await Compras.ReceiveAsync(db, po.Id));

        var inv = new Invoice { CustomerId = c.Id, Lines = [new() { ProductId = p.Id, Description = "Producto", Quantity = 3, UnitPrice = 100, TaxRate = 0.16m }] };
        (inv.Subtotal, inv.Tax, inv.Total) = Facturacion.Totals(inv.Lines);
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();
        Assert.Null(await Facturacion.EmitAsync(db, inv.Id));

        var steps = new List<int>();
        var checks = await Tareas.ReconcileAsync(db, (pct, _) => { steps.Add(pct); return Task.CompletedTask; });
        Assert.Equal(7, checks.Count);
        Assert.All(checks, ch => Assert.True(ch.Ok, $"{ch.Name}: {ch.Detail}"));
        Assert.Equal(100, steps[^1]);
        Assert.True(steps.SequenceEqual(steps.Order()));

        // Un movimiento sin póliza descuadra inventarios contra valuación.
        await Inventario.RegisterAsync(db, p.Id, "entrada", 50, null);
        checks = await Tareas.ReconcileAsync(db, (_, _) => Task.CompletedTask);
        Assert.False(checks.Single(ch => ch.Name == "Inventarios contra valuación").Ok);
    }
}
