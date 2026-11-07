using System.Text.Json;
using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class NominaCompletaTests
{
    readonly Guid companyId = Guid.NewGuid();
    readonly AppDb db;

    public NominaCompletaTests()
    {
        db = new(new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new CurrentUser { CompanyId = companyId, UserId = Guid.NewGuid() });
        db.Companies.Add(new Company { Id = companyId, Name = "Demo" });
        db.SaveChanges();
    }

    async Task UseParams(PayrollParams p)
    {
        (await db.Companies.SingleAsync()).PayrollJson = JsonSerializer.Serialize(p, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Company_parameters_change_aguinaldo_days_and_overtime_factor()
    {
        await UseParams(PayrollParams.Default with { AguinaldoDays = 30, DoubleFactor = 3 });
        var p = await Rrhh.ParamsAsync(db);
        Assert.Equal((30m, 3m, 0.0054355m), (p.AguinaldoDays, p.DoubleFactor, p.RiskPremium));
        Assert.Equal(1_125m * 1.5m, Nomina.Overtime(500m, 15m, 9m, 0m, p).Pay);           // 9 horas × 62.50 × 3
        Assert.Equal(30m, Nomina.AguinaldoDays(null, 2026, p));
        Assert.NotEmpty((PayrollParams.Default with { AguinaldoDays = 10, VacationPremium = 0.1m }).Validate());   // abajo de la ley
    }

    [Fact]
    public async Task Absences_sick_days_and_vacation_premium_change_pay_and_quotas()
    {
        db.Employees.Add(new Employee { Name = "Ana", DailySalary = 500, HireDate = new DateOnly(2026, 1, 1) });
        await db.SaveChangesAsync();
        var (run, _) = await Rrhh.CreateAsync(db, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15));
        var l = run!.Lines.Single();
        (l.Absences, l.SickDays, l.VacationDays) = (2, 1, 5);
        Rrhh.Recalculate(run, PayrollParams.Default);
        // 12 días pagados (6,000) + prima vacacional 5 × 500 × 25% = 625, exenta hasta 15 UMA.
        Assert.Equal((6_625m, 625m, 625m), (l.Gross, l.VacationPremium, l.Exempt));
        Assert.Equal(Nomina.Imss(l.Sbc, 12m), l.Imss);                                      // cotizan 12 días
        Assert.Equal(Nomina.Employer(l.Sbc, 12m, PayrollParams.Default.RiskPremium).Total, l.Employer);
    }

    [Fact]
    public void Minimum_wage_worker_quota_is_absorbed_by_the_employer()
    {
        var sbc = Nomina.Sbc(Nomina.MinWage, null, new DateOnly(2026, 11, 15));
        Assert.Equal(0m, Nomina.Imss(sbc, 15m, null, Nomina.MinWage));
        var withQuota = Nomina.Employer(sbc, 15m, 0.0054355m, null, Nomina.MinWage).Imss;
        Assert.Equal(Nomina.Employer(sbc, 15m, 0.0054355m).Imss + Nomina.Imss(sbc, 15m), withQuota);
    }

    [Fact]
    public async Task Unjustified_dismissal_settlement_pays_severance_and_terminates_the_employee()
    {
        var ana = new Employee { Name = "Ana", DailySalary = 500, HireDate = new DateOnly(2023, 3, 1) };
        db.Employees.Add(ana);
        await db.SaveChangesAsync();
        var (run, error) = await Rrhh.CreateAsync(db, default, new DateOnly(2026, 6, 30), "finiquito", null, ana.Id, "despido");
        Assert.Null(error);
        var l = run!.Lines.Single();
        l.Days = 10;   // salario pendiente
        l.Concepts = JsonSerializer.Serialize(Nomina.Settlement(500, ana.HireDate, run.EndDate, true, 10, 0), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Rrhh.Recalculate(run, PayrollParams.Default);

        // Año de servicio 4 (18 días): 6.02 días proporcionales; aguinaldo 7.44 días; 3 meses 45,000; 20 días × 3.337 años; antigüedad 12 × 500 × 3.337.
        Assert.Equal(
            [("Salario pendiente", 5_000m), ("Aguinaldo proporcional", 3_720m), ("Vacaciones (6.02 días)", 3_010m), ("Prima vacacional", 752.50m),
             ("Indemnización de 3 meses", 45_000m), ("20 días por año", 33_369.86m), ("Prima de antigüedad", 20_021.92m)],
            Rrhh.Concepts(l).Select(x => (x.Name, x.Amount)));
        Assert.Equal(110_874.28m, l.Gross);
        Assert.Equal(6_989.01m, l.Isr);   // ordinario 8,210.70 como extraordinario + separación gravada 56,160.18 a la tasa efectiva

        await db.SaveChangesAsync();
        Assert.Null(await Rrhh.PayAsync(db, run.Id));
        Assert.Equal((false, new DateOnly(2026, 6, 30)), (ana.Active, ana.TerminationDate));
    }

    [Fact]
    public async Task Ptu_includes_employees_who_left_during_the_prior_year_and_caps_by_history()
    {
        var ana = new Employee { Name = "Ana", DailySalary = 500 };
        var beto = new Employee { Name = "Beto", DailySalary = 500, Active = false, TerminationDate = new DateOnly(2025, 6, 30) };
        db.Employees.AddRange(ana, beto);
        // Ana recibió 150,000 de PTU en 2023-2025: su tope es el mayor entre 90 días (45,000) y el promedio (50,000).
        foreach (var y in new[] { 2023, 2024, 2025 })
            db.PayrollRuns.Add(new PayrollRun { Kind = "ptu", Status = "pagada", StartDate = new DateOnly(y, 5, 29), EndDate = new DateOnly(y, 5, 29),
                Lines = [new() { EmployeeId = ana.Id, Gross = 50_000 }] });
        await db.SaveChangesAsync();
        var (run, error) = await Rrhh.CreateAsync(db, default, new DateOnly(2026, 5, 29), "ptu", 300_000m);
        Assert.Null(error);
        Assert.Equal(2, run!.Lines.Count);
        Assert.Equal(181, run.Lines.Single(l => l.EmployeeId == beto.Id).Days);              // 1 de enero al 30 de junio de 2025
        Assert.Equal(45_000m, run.Lines.Single(l => l.EmployeeId == beto.Id).Gross);        // tope de 90 días de salario
        Assert.Equal(50_000m, run.Lines.Single(l => l.EmployeeId == ana.Id).Gross);         // tope por promedio de tres años
    }

    [Fact]
    public async Task Annual_adjustment_compares_annual_tariff_with_isr_withheld()
    {
        var ana = new Employee { Name = "Ana", DailySalary = 500, HireDate = new DateOnly(2020, 1, 1) };
        var late = new Employee { Name = "Luis", DailySalary = 500, HireDate = new DateOnly(2026, 3, 1) };
        db.Employees.AddRange(ana, late);
        db.PayrollRuns.Add(new PayrollRun
        {
            Kind = "ordinaria", Status = "pagada", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31),
            Lines =
            [
                new() { EmployeeId = ana.Id, Gross = 185_000, Exempt = 5_000, Isr = 20_000 },
                new() { EmployeeId = late.Id, Gross = 100_000, Isr = 8_000 },
            ],
        });
        await db.SaveChangesAsync();
        var rows = (await Rrhh.AnnualAsync(db, 2026)).ToDictionary(r => r.Employee);
        // Gravado 180,000: ISR anual 16,833.82 − retenido 20,000 = −3,166.18 a favor.
        Assert.Equal((true, 16_833.82m, -3_166.18m), (rows["Ana"].Eligible, rows["Ana"].Annual, rows["Ana"].Adjustment));
        Assert.Equal((false, 0m), (rows["Luis"].Eligible, rows["Luis"].Adjustment));
    }
}
