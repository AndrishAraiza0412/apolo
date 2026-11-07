using System.Xml.Linq;
using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class FiscalTests
{
    static readonly XNamespace C = "http://www.sat.gob.mx/cfd/4";
    readonly Guid companyId = Guid.NewGuid();
    readonly AppDb db;
    readonly Company company;

    public FiscalTests()
    {
        db = new(new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new CurrentUser { CompanyId = companyId, UserId = Guid.NewGuid() });
        company = new Company { Id = companyId, Name = "Demo", Rfc = "EKU9003173C9", LegalName = "Escuela Kemper Urgate", TaxRegime = "601", ZipCode = "42501" };
        db.Companies.Add(company);
        db.SaveChanges();
    }

    async Task<Invoice> Emit(decimal price, string customerRfc = "CUN120315AB1")
    {
        var c = new Customer { Name = "Cliente", Rfc = customerRfc, TaxRegime = "601", ZipCode = "76000", CreditDays = 30 };
        db.Customers.Add(c);
        var inv = new Invoice { CustomerId = c.Id, Lines = [new() { Description = "Consultoría", Quantity = 1, UnitPrice = price, TaxRate = 0.16m }] };
        (inv.Subtotal, inv.Tax, inv.Total) = Facturacion.Totals(inv.Lines);
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();
        Assert.Null(await Facturacion.EmitAsync(db, inv.Id));
        return inv;
    }

    async Task<Dictionary<string, decimal>> Balances(DateOnly to) =>
        (await Contabilidad.BalancesAsync(db, DateOnly.MinValue, to)).ToDictionary(b => b.Code, b => b.Closing);

    [Fact]
    public async Task Professional_billing_a_company_withholds_isr_and_two_thirds_of_iva()
    {
        (company.Rfc, company.TaxRegime) = ("GODE561231GR8", "612");   // persona física, honorarios
        var inv = await Emit(10_000);

        Assert.Equal((1_000m, 1_066.67m, 9_533.33m), (inv.IsrWithheld, inv.IvaWithheld, inv.Total));
        var x = XDocument.Parse(inv.Xml!).Root!;
        Assert.Equal(("2066.67", "9533.33"), (x.Element(C + "Impuestos")!.Attribute("TotalImpuestosRetenidos")!.Value, x.Attribute("Total")!.Value));
        Assert.Equal(["001", "002"], x.Element(C + "Conceptos")!.Descendants(C + "Retencion").Select(r => r.Attribute("Impuesto")!.Value));
        var b = await Balances(DateOnly.MaxValue);
        Assert.Equal((9_533.33m, 1_000m, 1_066.67m), (b[Contabilidad.Clientes], b[Contabilidad.IsrRetenidoClientes], b[Contabilidad.IvaRetenidoClientes]));

        // El IVA retenido se compensa contra el trasladado: solo se paga 1,600 − 1,066.67.
        var iva = (await Impuestos.ObligationsAsync(db, DateOnly.MaxValue)).Single(o => o.Kind == "iva");
        Assert.Equal(533.33m, iva.Due);
        var (_, error) = await Facturacion.CreditAsync(db, inv.Id, 100, "descuento", "Ajuste");
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Company_to_company_invoice_has_no_withholdings()
    {
        var inv = await Emit(10_000);
        Assert.Equal((0m, 0m, 11_600m), (inv.IsrWithheld, inv.IvaWithheld, inv.Total));
        Assert.Null(XDocument.Parse(inv.Xml!).Root!.Element(C + "Impuestos")!.Attribute("TotalImpuestosRetenidos"));
    }

    [Fact]
    public async Task Provisional_isr_applies_profit_coefficient_and_subtracts_prior_payments()
    {
        company.ProfitCoefficient = 0.2m;
        await Emit(100_000);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var p = await Impuestos.ProvisionalAsync(db, today);
        Assert.Equal((100_000m, 20_000m, 6_000m, 6_000m), (p.Income, p.Profit, p.Isr, p.Due));

        Assert.Null(await Impuestos.PayAsync(db, companyId, "isr", new(today, 4_000)));
        Assert.Equal(2_000m, (await Impuestos.ProvisionalAsync(db, today)).Due);
        Assert.Equal(4_000m, (await Balances(today))[Contabilidad.PagosProvisionales]);
    }

    [Fact]
    public async Task Electronic_accounting_xml_lists_accounts_with_grouping_codes_and_balances()
    {
        await Emit(1_000);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var from = new DateOnly(today.Year, today.Month, 1);
        var accounts = await db.Accounts.ToListAsync();
        var balances = await Contabilidad.BalancesAsync(db, from, from.AddMonths(1).AddDays(-1));
        Assert.Empty(Fiscal.Missing(accounts, balances));

        var cat = Fiscal.Catalog(company, today.Year, today.Month, accounts).Root!;
        var bank = cat.Elements().Single(e => e.Attribute("NumCta")!.Value == "102");
        Assert.Equal(("102.01", "D"), (bank.Attribute("CodAgrup")!.Value, bank.Attribute("Natur")!.Value));
        var bal = Fiscal.Balance(company, today.Year, today.Month, balances).Root!;
        var sales = bal.Elements().Single(e => e.Attribute("NumCta")!.Value == "401");
        Assert.Equal(("0.00", "1000.00", "1000.00"), (sales.Attribute("Debe")!.Value, sales.Attribute("Haber")!.Value, sales.Attribute("SaldoFin")!.Value));

        // Una cuenta nueva con movimientos y sin código agrupador impide generar el XML.
        db.Accounts.Add(new Account { Code = "610", Name = "Publicidad", Type = "gasto" });
        await db.SaveChangesAsync();
        var ad = await db.Accounts.SingleAsync(a => a.Code == "610");
        balances = [.. balances, new(ad.Id, "610", "Publicidad", "gasto", 0, 50, 0, 50)];
        Assert.Equal(["610 Publicidad"], Fiscal.Missing(await db.Accounts.ToListAsync(), balances));
    }

    [Fact]
    public async Task Closing_the_year_moves_results_to_equity_and_locks_the_period()
    {
        var year = DateTime.Today.Year - 1;
        var day = new DateOnly(year, 6, 1);
        await Contabilidad.PostAsync(db, companyId, day, "diario", "Venta", "manual", null, (Contabilidad.Bancos, 5_000, 0), (Contabilidad.Ventas, 0, 5_000));
        await Contabilidad.PostAsync(db, companyId, day, "diario", "Renta", "manual", null, (Contabilidad.GastosGenerales, 2_000, 0), (Contabilidad.Bancos, 0, 2_000));
        await db.SaveChangesAsync();

        Assert.Null(await Fiscal.CloseAsync(db, companyId, year));
        var b = await Balances(new DateOnly(year, 12, 31));
        Assert.Equal((0m, 0m, 3_000m), (b[Contabilidad.Ventas], b[Contabilidad.GastosGenerales], b[Contabilidad.ResultadoEjercicio]));
        Assert.NotNull(await Fiscal.CloseAsync(db, companyId, year));

        await Contabilidad.PostAsync(db, companyId, day, "diario", "Tardía", "manual", null, (Contabilidad.Bancos, 1, 0), (Contabilidad.Ventas, 0, 1));
        await Assert.ThrowsAsync<PeriodClosedException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        Assert.Null(await Fiscal.ReopenAsync(db, companyId, year));
        Assert.Null((await db.Companies.SingleAsync()).ClosedThrough);
        Assert.Equal(5_000m, (await Balances(new DateOnly(year, 12, 31)))[Contabilidad.Ventas]);
    }
}
