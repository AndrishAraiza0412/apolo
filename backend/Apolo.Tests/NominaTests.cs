using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class NominaTests
{
    [Fact]
    public void Isr_follows_the_monthly_tariff_and_subsidy_applies_below_the_limit()
    {
        // 10,000 mensuales (tarifa 2026): (10,000 − 7,168.52) × 10.88% + 420.95 = 729.02; subsidio 15.02% × 117.31 × 30.4 = 535.65.
        var isr = Nomina.Isr(10_000m, 30.4m);
        Assert.Equal((729.02m, 535.65m), (isr, Nomina.Subsidy(10_000m, 30.4m, isr)));
        // Arriba de 11,492.66 mensuales ya no hay subsidio.
        Assert.Equal(0, Nomina.Subsidy(12_000m, 30.4m, Nomina.Isr(12_000m, 30.4m)));
        // El subsidio nunca excede el ISR.
        var low = Nomina.Isr(2_000m, 15m);
        Assert.Equal(low, Nomina.Subsidy(2_000m, 15m, low));
        // Quincena: la tarifa se ajusta a 15/30.4 y da la mitad aproximada del ISR mensual.
        Assert.Equal(364.51m, Nomina.Isr(5_000m, 15.2m));
    }

    [Fact]
    public void Sbc_integrates_aguinaldo_and_vacation_by_seniority()
    {
        var at = new DateOnly(2026, 11, 15);
        Assert.Equal(524.66m, Nomina.Sbc(500m, at.AddMonths(-3), at));      // año 1: 12 días de vacaciones
        Assert.Equal(528.08m, Nomina.Sbc(500m, at.AddYears(-6), at));       // año 7: 22 días
        Assert.Equal(25 * Nomina.Uma, Nomina.Sbc(10_000m, null, at));      // tope de 25 UMA
        Assert.Equal(197.27m, Nomina.Imss(524.66m, 15m));                   // 2.375% × SBC × días + 0.40% del excedente de 3 UMA
    }

    [Fact]
    public async Task Payroll_line_net_is_gross_minus_isr_imss_and_other()
    {
        var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new CurrentUser { CompanyId = Guid.NewGuid(), UserId = Guid.NewGuid() });
        db.Employees.Add(new Employee { Name = "Ana", DailySalary = 500, HireDate = new DateOnly(2026, 1, 1) });
        await db.SaveChangesAsync();
        var (run, error) = await Rrhh.CreateAsync(db, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15));
        Assert.Null(error);
        var l = run!.Lines.Single();
        l.Other = 100;
        Rrhh.Recalculate(run, 0.0054355m);
        Assert.Equal(7_500m, l.Gross);
        Assert.Equal(l.Isr - l.Subsidy + l.Imss + 100, l.Deductions);
        Assert.Equal(l.Gross - l.Deductions, l.Net);
        Assert.True(l.Isr > 0 && l.Imss > 0 && l.Subsidy == 0);
    }

    [Fact]
    public void Overtime_pays_double_and_triple_and_exempts_half_of_doubles()
    {
        // Hora = 500 / 8 = 62.50. 9 dobles = 1,125; 3 triples = 562.50. Exento: la mitad de las dobles (tope 5 UMA × 3 semanas).
        Assert.Equal((1_687.50m, 562.50m), Nomina.Overtime(500m, 15m, 9m, 3m));
        // En salario mínimo las dobles son exentas completas.
        Assert.Equal((315.04m, 315.04m), Nomina.Overtime(Nomina.MinWage, 15m, 4m, 0m));
    }

    [Fact]
    public void Employer_quotas_follow_2026_imss_rcv_and_infonavit_rates()
    {
        // SBC 524.66 (4.47 UMA, cesantía 7.513%), 15 días, prima de riesgo 0.54355%.
        // IMSS: (20.40% × 117.31 + 1.10% × (524.66 − 351.93) + 5.04355% × 524.66) × 15 = 784.39.
        // RCV: 524.66 × (2% + 7.513%) × 15 = 748.66. INFONAVIT: 524.66 × 5% × 15 = 393.50.
        Assert.Equal(new Nomina.EmployerQuotas(784.39m, 748.66m, 393.50m), Nomina.Employer(524.66m, 15m, 0.0054355m));
        // Hasta un salario mínimo, cesantía y vejez es 3.150%: 315.04 × 5.15% = 16.22.
        Assert.Equal(16.22m, Nomina.Employer(Nomina.MinWage, 1m, 0.0054355m).Rcv);
    }

    static AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new CurrentUser { CompanyId = Guid.NewGuid(), UserId = Guid.NewGuid() });

    [Fact]
    public async Task Aguinaldo_is_prorated_exempts_30_uma_and_taxes_the_rest_over_the_monthly_salary()
    {
        var db = Db();
        db.Employees.AddRange(new Employee { Name = "Ana", DailySalary = 500 }, new Employee { Name = "Beto", DailySalary = 500, HireDate = new DateOnly(2026, 7, 1) });
        await db.SaveChangesAsync();
        var (run, error) = await Rrhh.CreateAsync(db, default, new DateOnly(2026, 12, 15), "aguinaldo");
        Assert.Null(error);
        var ana = run!.Lines.Single(l => l.Days == 15);
        Assert.Equal(7.56m, run.Lines.Single(l => l != ana).Days);                 // 15 × 184 / 365
        // 7,500 − 30 UMA (3,519.30) = 3,980.70 gravado; ISR(15,200 + 3,980.70) − ISR(15,200) = 769.99. Sin IMSS ni cuotas patronales.
        Assert.Equal((7_500m, 3_519.30m, 769.99m, 0m, 0m), (ana.Gross, ana.Exempt, ana.Isr, ana.Imss, ana.Employer));
        await db.SaveChangesAsync();
        Assert.Contains("Ya existe", (await Rrhh.CreateAsync(db, default, new DateOnly(2026, 12, 20), "aguinaldo")).Error); // uno por año
    }

    [Fact]
    public async Task Ptu_splits_half_by_days_half_by_salary_of_the_prior_year()
    {
        var db = Db();
        db.Employees.AddRange(new Employee { Name = "Ana", DailySalary = 500 }, new Employee { Name = "Beto", DailySalary = 1_000, HireDate = new DateOnly(2025, 7, 1) });
        await db.SaveChangesAsync();
        Assert.NotNull((await Rrhh.CreateAsync(db, default, new DateOnly(2026, 5, 29), "ptu")).Error); // sin monto
        var (run, _) = await Rrhh.CreateAsync(db, default, new DateOnly(2026, 5, 29), "ptu", 20_000m);
        // Días 365 y 184 (549); salarios 182,500 y 184,000 (366,500).
        Assert.Equal([11_627.99m, 8_372.01m], run!.Lines.OrderBy(l => l.DailySalary).Select(l => l.Gross));
        var ana = run.Lines.Single(l => l.DailySalary == 500);
        Assert.Equal((1_759.65m, 2_027.59m), (ana.Exempt, ana.Isr));                // 15 UMA exentas
    }

    [Fact]
    public async Task Paying_payroll_books_employer_quotas_as_expense_and_liability()
    {
        var db = Db();
        db.Employees.Add(new Employee { Name = "Ana", DailySalary = 500, HireDate = new DateOnly(2026, 1, 1) });
        await db.SaveChangesAsync();
        var (run, _) = await Rrhh.CreateAsync(db, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15));
        run!.Lines[0].DoubleHours = 4;
        Rrhh.Recalculate(run, 0.0054355m);
        await db.SaveChangesAsync();
        Assert.Equal(7_500m + 500m, run.Gross);                                   // 4 dobles × 62.50 × 2
        Assert.Null(await Rrhh.PayAsync(db, run.Id));

        var lines = await db.JournalLines.Join(db.Accounts, l => l.AccountId, a => a.Id, (l, a) => new { a.Code, l.Debit, l.Credit }).ToListAsync();
        Assert.True(run.EmployerCost > 0);
        Assert.Equal(run.EmployerCost, lines.Single(l => l.Code == Contabilidad.CuotasPatronales).Debit);
        Assert.Equal(run.EmployerCost, lines.Single(l => l.Code == Contabilidad.CuotasPorPagar).Credit);
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }
}
