using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class ObligacionesTests
{
    readonly Guid companyId = Guid.NewGuid();
    readonly AppDb db;

    public ObligacionesTests() => db = new(new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new CurrentUser { CompanyId = companyId, UserId = Guid.NewGuid() });

    async Task<decimal> Closing(string code) =>
        (await Contabilidad.BalancesAsync(db, DateOnly.MinValue, DateOnly.MaxValue)).Single(b => b.Code == code).Closing;

    [Fact]
    public async Task Withholdings_pay_partially_and_vat_offsets_creditable_before_paying()
    {
        var day = new DateOnly(2026, 10, 31);
        await Contabilidad.PostAsync(db, companyId, day, "egreso", "Nómina", "manual", null,
            (Contabilidad.Sueldos, 1_000, 0), (Contabilidad.Bancos, 0, 800), (Contabilidad.RetencionesPorPagar, 0, 200));
        await Contabilidad.PostAsync(db, companyId, day, "diario", "Venta", "manual", null,
            (Contabilidad.Clientes, 1_160, 0), (Contabilidad.Ventas, 0, 1_000), (Contabilidad.IvaTrasladado, 0, 160));
        await Contabilidad.PostAsync(db, companyId, day, "diario", "Compra", "manual", null,
            (Contabilidad.Inventarios, 500, 0), (Contabilidad.IvaAcreditable, 80, 0), (Contabilidad.Proveedores, 0, 580));
        await db.SaveChangesAsync();

        var due = (await Impuestos.ObligationsAsync(db, day)).ToDictionary(o => o.Kind, o => o.Due);
        Assert.Equal((200m, 0m, 80m), (due["retenciones"], due["cuotas"], due["iva"]));

        Assert.NotNull(await Impuestos.PayAsync(db, companyId, "retenciones", new(day, 250)));   // más que el saldo
        Assert.Null(await Impuestos.PayAsync(db, companyId, "retenciones", new(day, 150)));
        Assert.Null(await Impuestos.PayAsync(db, companyId, "iva", new(day)));
        Assert.NotNull(await Impuestos.PayAsync(db, companyId, "cuotas", new(day)));              // sin saldo

        Assert.Equal((50m, 0m, 0m), (await Closing(Contabilidad.RetencionesPorPagar), await Closing(Contabilidad.IvaTrasladado), await Closing(Contabilidad.IvaAcreditable)));
        Assert.Equal(-800m - 150 - 80, await Closing(Contabilidad.Bancos));
    }

    [Fact]
    public async Task Supplier_partial_payments_settle_the_order_at_the_end()
    {
        var supplier = new Supplier { Name = "Proveedor" };
        db.Suppliers.Add(supplier);
        var po = new PurchaseOrder { SupplierId = supplier.Id, Folio = 1, Status = "recibida", Subtotal = 1_000, Tax = 160, Total = 1_160 };
        db.PurchaseOrders.Add(po);
        await db.SaveChangesAsync();

        Assert.NotNull(await Compras.PayAsync(db, po.Id, null, 2_000));
        Assert.Null(await Compras.PayAsync(db, po.Id, null, 500));
        Assert.Equal(("recibida", 500m), (po.Status, po.Paid));
        Assert.Null(await Compras.PayAsync(db, po.Id));
        Assert.Equal(("pagada", 1_160m), (po.Status, po.Paid));
        Assert.Equal(1_160m, -await Closing(Contabilidad.Proveedores));   // dos abonos que suman el total, como cargos a proveedores
    }
}
