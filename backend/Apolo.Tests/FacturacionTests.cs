using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class FacturacionTests
{
    readonly AppDb db = new(
        new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new CurrentUser { CompanyId = Guid.NewGuid(), UserId = Guid.NewGuid() });

    async Task<(Customer c, Product p)> Setup(decimal stock)
    {
        var c = new Customer { Name = "Cliente", CreditDays = 30 };
        var p = new Product { Sku = "A-1", Name = "Producto", Price = 100 };
        db.AddRange(c, p);
        await db.SaveChangesAsync();
        if (stock > 0) await Inventario.RegisterAsync(db, p.Id, "entrada", stock, null);
        return (c, p);
    }

    async Task<Invoice> Draft(Customer c, params InvoiceLine[] lines)
    {
        var inv = new Invoice { CustomerId = c.Id, Lines = [.. lines] };
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();
        return inv;
    }

    static InvoiceLine Line(Product? p, decimal qty, decimal price = 100) =>
        new() { ProductId = p?.Id, Description = p?.Name ?? "Servicio", Quantity = qty, UnitPrice = price, TaxRate = 0.16m };

    [Fact]
    public void Totals_round_per_line()
    {
        var (sub, tax, total) = Facturacion.Totals([Line(null, 3, 0.335m), Line(null, 1, 10)]);
        Assert.Equal(11.01m, sub);  // 1.005 → 1.01, + 10
        Assert.Equal(1.76m, tax);   // 0.16 + 1.60
        Assert.Equal(12.77m, total);
    }

    [Fact]
    public async Task Emit_assigns_consecutive_folios_takes_stock_and_sets_due_date()
    {
        var (c, p) = await Setup(10);
        var a = await Draft(c, Line(p, 4));
        var b = await Draft(c, Line(p, 1), Line(null, 1));

        Assert.Null(await Facturacion.EmitAsync(db, a.Id));
        Assert.Null(await Facturacion.EmitAsync(db, b.Id));

        Assert.Equal((1, 2), (a.Folio, b.Folio));
        Assert.Equal("emitida", a.Status);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today).AddDays(30), a.DueDate);
        Assert.Equal(5, await Inventario.Stock(db, p.Id));
        Assert.NotNull(await Facturacion.EmitAsync(db, a.Id)); // no se emite dos veces
    }

    [Fact]
    public async Task Emit_without_stock_leaves_the_draft_untouched()
    {
        var (c, p) = await Setup(2);
        var inv = await Draft(c, Line(p, 5));

        Assert.Contains("Existencia insuficiente", await Facturacion.EmitAsync(db, inv.Id));
        Assert.Equal("borrador", inv.Status);
        Assert.Null(inv.Folio);
        Assert.Equal(2, await Inventario.Stock(db, p.Id));
    }

    [Fact]
    public async Task Cancel_returns_stock_and_paid_invoices_cannot_be_cancelled()
    {
        var (c, p) = await Setup(10);
        var inv = await Draft(c, Line(p, 4));
        await Facturacion.EmitAsync(db, inv.Id);

        Assert.Null(await Facturacion.CancelAsync(db, inv.Id));
        Assert.Equal("cancelada", inv.Status);
        Assert.Equal(10, await Inventario.Stock(db, p.Id));

        var paid = await Draft(c, Line(p, 1));
        await Facturacion.EmitAsync(db, paid.Id);
        paid.Status = "pagada";
        await db.SaveChangesAsync();
        Assert.NotNull(await Facturacion.CancelAsync(db, paid.Id));
    }
}
