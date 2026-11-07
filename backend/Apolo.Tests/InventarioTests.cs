using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class InventarioTests
{
    readonly AppDb db = new(
        new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new CurrentUser { CompanyId = Guid.NewGuid(), UserId = Guid.NewGuid() });

    async Task<Guid> NewProduct()
    {
        var p = new Product { Sku = "A-1", Name = "Producto" };
        db.Products.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task Stock_is_the_sum_of_movements()
    {
        var id = await NewProduct();
        Assert.Null(await Inventario.RegisterAsync(db, id, "entrada", 10, null));
        Assert.Null(await Inventario.RegisterAsync(db, id, "salida", 3, "venta"));
        Assert.Null(await Inventario.RegisterAsync(db, id, "ajuste", -2, "merma"));
        Assert.Equal(5, await Inventario.Stock(db, id));
    }

    [Fact]
    public async Task Stock_never_goes_negative()
    {
        var id = await NewProduct();
        await Inventario.RegisterAsync(db, id, "entrada", 4, null);
        Assert.NotNull(await Inventario.RegisterAsync(db, id, "salida", 5, null));
        Assert.NotNull(await Inventario.RegisterAsync(db, id, "ajuste", -5, null));
        Assert.Equal(4, await Inventario.Stock(db, id));
    }

    [Theory]
    [InlineData("entrada", 0)]
    [InlineData("entrada", -1)]
    [InlineData("salida", -1)]
    [InlineData("ajuste", 0)]
    [InlineData("robo", 1)]
    public async Task Rejects_invalid_kind_or_quantity(string kind, decimal qty)
    {
        var id = await NewProduct();
        Assert.NotNull(await Inventario.RegisterAsync(db, id, kind, qty, null));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
    }
}
