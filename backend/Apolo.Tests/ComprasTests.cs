using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class ComprasTests
{
    readonly AppDb db = new(
        new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new CurrentUser { CompanyId = Guid.NewGuid(), UserId = Guid.NewGuid() });

    async Task<(PurchaseOrder po, Product p)> Draft()
    {
        var s = new Supplier { Name = "Proveedor" };
        var p = new Product { Sku = "A-1", Name = "Producto", Cost = 50 };
        db.AddRange(s, p);
        var po = new PurchaseOrder { SupplierId = s.Id, Lines = [new() { ProductId = p.Id, Quantity = 8, UnitCost = 62.5m, TaxRate = 0.16m }] };
        db.PurchaseOrders.Add(po);
        await db.SaveChangesAsync();
        return (po, p);
    }

    [Fact]
    public async Task Receive_adds_stock_and_updates_cost_only_after_sending()
    {
        var (po, p) = await Draft();
        Assert.NotNull(await Compras.ReceiveAsync(db, po.Id)); // un borrador no se recibe
        Assert.Equal(0, await Inventario.Stock(db, p.Id));

        Assert.Null(await Compras.SendAsync(db, po.Id));
        Assert.Equal("OC-0001", Compras.FolioText(po.Folio));
        Assert.Null(await Compras.ReceiveAsync(db, po.Id));

        Assert.Equal(8, await Inventario.Stock(db, p.Id));
        Assert.Equal(62.5m, p.Cost);
        Assert.NotNull(await Compras.ReceiveAsync(db, po.Id)); // no se recibe dos veces
        Assert.NotNull(await Compras.CancelAsync(db, po.Id));  // recibida ya no se cancela
    }

    [Fact]
    public async Task Rejects_foreign_or_inactive_supplier()
    {
        var (_, p) = await Draft();
        var inactive = new Supplier { Name = "Viejo", Active = false };
        db.Suppliers.Add(inactive);
        await db.SaveChangesAsync();
        foreach (var supplierId in new[] { inactive.Id, Guid.NewGuid() })
        {
            var errors = await Compras.ValidateAsync(db, new PurchaseRequest(supplierId, null, [new(p.Id, 1, 10, 0.16m)]));
            Assert.Contains("supplierId", errors.Keys);
        }
    }
}
