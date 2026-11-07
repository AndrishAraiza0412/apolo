using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class ContabilidadTests
{
    readonly AppDb db = new(
        new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new CurrentUser { CompanyId = Guid.NewGuid(), UserId = Guid.NewGuid() });

    async Task<Dictionary<string, decimal>> Balances()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return (await Contabilidad.BalancesAsync(db, today.AddYears(-1), today.AddDays(1))).ToDictionary(a => a.Code, a => a.Closing);
    }

    [Fact]
    public async Task Invoice_lifecycle_posts_balanced_entries_and_cancel_reverses_them()
    {
        var c = new Customer { Name = "Cliente", CreditDays = 30 };
        var p = new Product { Sku = "A-1", Name = "Producto", Cost = 60, Price = 100 };
        db.AddRange(c, p);
        await db.SaveChangesAsync();
        await Inventario.RegisterAsync(db, p.Id, "entrada", 10, null);
        var inv = new Invoice { CustomerId = c.Id, Lines = [new() { ProductId = p.Id, Description = "Producto", Quantity = 2, UnitPrice = 100, TaxRate = 0.16m }] };
        (inv.Subtotal, inv.Tax, inv.Total) = Facturacion.Totals(inv.Lines);
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();

        Assert.Null(await Facturacion.EmitAsync(db, inv.Id));
        var b = await Balances();
        Assert.Equal((232m, 200m, 32m, 120m, -120m), (b["105"], b["401"], b["208"], b["501"], b["115"]));

        var cancel = await Facturacion.CancelAsync(db, inv.Id);
        Assert.Null(cancel);
        b = await Balances();
        Assert.All(new[] { "105", "401", "208", "501", "115" }, code => Assert.Equal(0, b[code]));
        Assert.All(await db.JournalEntries.Include(e => e.Lines).ToListAsync(),
            e => Assert.Equal(e.Lines.Sum(l => l.Debit), e.Lines.Sum(l => l.Credit)));
    }

    [Fact]
    public async Task Payment_moves_receivable_to_bank_once()
    {
        var c = new Customer { Name = "Cliente" };
        db.Customers.Add(c);
        var inv = new Invoice { CustomerId = c.Id, Lines = [new() { Description = "Servicio", Quantity = 1, UnitPrice = 1000, TaxRate = 0.16m }] };
        (inv.Subtotal, inv.Tax, inv.Total) = Facturacion.Totals(inv.Lines);
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();

        Assert.Null(await Facturacion.EmitAsync(db, inv.Id));
        Assert.Null(await Facturacion.PayAsync(db, inv.Id));
        Assert.NotNull(await Facturacion.PayAsync(db, inv.Id));
        var b = await Balances();
        Assert.Equal((1160m, 0m), (b["102"], b["105"]));
    }

    [Fact]
    public async Task Payroll_pays_once_and_posts_salary_expense()
    {
        db.Employees.AddRange(new Employee { Name = "Uno", DailySalary = 500 }, new Employee { Name = "Dos", DailySalary = 300, Active = false });
        await db.SaveChangesAsync();
        var (run, error) = await Rrhh.CreateAsync(db, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15));
        Assert.Null(error);
        Assert.Single(run!.Lines); // solo empleados activos
        run.Lines[0].Other = 100;
        Rrhh.Recalculate(run, 0.0054355m);
        await db.SaveChangesAsync();
        var withheld = run.Deductions;

        Assert.Equal((7500m, 7500m - withheld), (run.Gross, run.Net));
        Assert.NotNull((await Rrhh.CreateAsync(db, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 20))).Error); // se cruza
        Assert.Null(await Rrhh.PayAsync(db, run.Id, new DateTime(2026, 10, 15, 18, 0, 0, DateTimeKind.Utc)));
        Assert.NotNull(await Rrhh.PayAsync(db, run.Id));

        var b = (await Contabilidad.BalancesAsync(db, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))).ToDictionary(a => a.Code, a => a.Closing);
        Assert.Equal((7500m, -(7500m - withheld), withheld), (b["602"], b["102"], b["213"]));
    }
}
