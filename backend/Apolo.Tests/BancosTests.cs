using System.Security.Claims;
using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class BancosTests
{
    readonly AppDb db = new(
        new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new CurrentUser { CompanyId = Guid.NewGuid(), UserId = Guid.NewGuid() });

    static ClaimsPrincipal Role(params string[] modules) => new(new ClaimsIdentity(modules.Select(m => new Claim(Modules.ClaimType, m)), "test"));

    [Fact]
    public void Parses_charge_and_credit_columns_with_quoted_thousands()
    {
        var (rows, errors) = Bancos.Parse("Fecha,Descripción,Referencia,Cargo,Abono\n02/11/2026,SPEI RECIBIDO,A-0001,,\"1,160.00\"\n2026-11-03,\"COMISION, IVA\",,250,\nmal,X,,1,\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal((new DateOnly(2026, 11, 2), 1160m, "A-0001"), (rows[0].Date, rows[0].Amount, rows[0].Reference));
        Assert.Equal(("COMISION, IVA", -250m), (rows[1].Description, rows[1].Amount));
        Assert.Single(errors);
    }

    [Fact]
    public async Task Deposit_matches_open_invoice_pays_it_on_bank_date_and_reimport_skips_duplicates()
    {
        var c = new Customer { Name = "Cliente" };
        db.Customers.Add(c);
        var inv = new Invoice { CustomerId = c.Id, Lines = [new() { Description = "Servicio", Quantity = 1, UnitPrice = 1000, TaxRate = 0.16m }] };
        (inv.Subtotal, inv.Tax, inv.Total) = Facturacion.Totals(inv.Lines);
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();
        Assert.Null(await Facturacion.EmitAsync(db, inv.Id));

        const string csv = "Fecha,Concepto,Monto\n01/10/2026,DEPOSITO A-0001,1160.00\n01/10/2026,COMISION,-50\n";
        Assert.Equal((2, 0), (await Bancos.ImportAsync(db, csv)) is var r ? (r.Added, r.Skipped) : default);
        Assert.Equal((0, 2), (await Bancos.ImportAsync(db, csv)) is var r2 ? (r2.Added, r2.Skipped) : default);

        var deposit = await db.BankTransactions.SingleAsync(t => t.Amount > 0);
        var s = Assert.Single(await Bancos.SuggestAsync(db, deposit));
        Assert.Equal(("factura", inv.Id, false), (s.Kind, s.Id, s.AlreadyPaid));

        Assert.NotNull(await Bancos.MatchAsync(db, Role("contabilidad"), deposit.Id, new("factura", inv.Id))); // sin permiso de facturación
        Assert.Null(await Bancos.MatchAsync(db, Role("contabilidad", "facturacion"), deposit.Id, new("factura", inv.Id)));
        Assert.Equal(("pagada", new DateOnly(2026, 10, 1)), (inv.Status, DateOnly.FromDateTime(inv.PaidAt!.Value.ToLocalTime())));
        Assert.Equal("conciliado", deposit.Status);
        Assert.NotNull(await Bancos.MatchAsync(db, Role("contabilidad", "facturacion"), deposit.Id, new("factura", inv.Id))); // una sola vez

        var fee = await db.BankTransactions.SingleAsync(t => t.Amount < 0);
        Assert.Empty(await Bancos.SuggestAsync(db, fee));
    }
}
