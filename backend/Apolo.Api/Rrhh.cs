using System.Text.Json;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public record EmployeeRequest(string Name, string? Rfc, string? Position, string? Department, DateOnly? HireDate, decimal DailySalary, bool Active = true,
    DateOnly? TerminationDate = null);
public record PayrollRequest(DateOnly StartDate, DateOnly EndDate, string Kind = "ordinaria", decimal? PtuAmount = null,
    Guid? EmployeeId = null, string? Cause = null);
public record PayrollLineRequest(Guid EmployeeId, decimal Days, decimal Other, decimal DoubleHours = 0, decimal TripleHours = 0,
    decimal VacationDays = 0, decimal Absences = 0, decimal SickDays = 0);

public static class Rrhh
{
    public static string FolioText(int? folio) => folio is int f ? $"N-{f:0000}" : "Borrador";

    static decimal Round(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// Annual income above which the worker files their own return (art. 98-III-e LISR).
    public const decimal AnnualDeclarationLimit = 400_000m;

    static Dictionary<string, string[]> Validate(EmployeeRequest r)
    {
        var e = Ventas.ValidateContact(r.Name, r.Rfc, 0);
        if (r.DailySalary <= 0) e["dailySalary"] = ["El sueldo diario debe ser mayor a cero."];
        if (r.TerminationDate < r.HireDate) e["terminationDate"] = ["La baja no puede ser anterior al ingreso."];
        return e;
    }

    static void Apply(Employee x, EmployeeRequest r)
    {
        x.Name = r.Name.Trim();
        x.Rfc = Ventas.Clean(r.Rfc)?.ToUpperInvariant();
        x.Position = Ventas.Clean(r.Position);
        x.Department = Ventas.Clean(r.Department);
        x.HireDate = r.HireDate;
        x.DailySalary = r.DailySalary;
        x.Active = r.Active && r.TerminationDate is null;
        x.TerminationDate = r.TerminationDate;
    }

    /// Parameters of the session's company with its risk premium; without a company (tests), the current legal ones.
    public static async Task<PayrollParams> ParamsAsync(AppDb db)
    {
        var c = await db.Companies.Where(x => x.Id == db.CompanyId).Select(x => new { x.PayrollJson, x.RiskPremium }).SingleOrDefaultAsync();
        if (c is null) return PayrollParams.Default;
        var p = c.PayrollJson is null ? PayrollParams.Default : JsonSerializer.Deserialize<PayrollParams>(c.PayrollJson, Json) ?? PayrollParams.Default;
        return p with { RiskPremium = c.RiskPremium };
    }

    public static void Recalculate(PayrollRun run, decimal riskRate) => Recalculate(run, PayrollParams.Default with { RiskPremium = riskRate });

    public static void Recalculate(PayrollRun run, PayrollParams p)
    {
        foreach (var l in run.Lines)
        {
            (l.Overtime, l.VacationPremium, l.Subsidy, l.Imss, l.Employer) = (0, 0, 0, 0, 0);
            switch (run.Kind)
            {
                case "ordinaria":
                    var worked = Math.Max(0, l.Days - l.Absences - l.SickDays);
                    (l.Overtime, var overtimeExempt) = Nomina.Overtime(l.DailySalary, l.Days, l.DoubleHours, l.TripleHours, p);
                    (l.VacationPremium, var premiumExempt) = Nomina.VacationPremiumPay(l.DailySalary, l.VacationDays, p);
                    l.Gross = Round(worked * l.DailySalary) + l.Overtime + l.VacationPremium;
                    l.Exempt = overtimeExempt + premiumExempt;
                    var periodDays = worked > 0 ? worked : l.Days;
                    l.Isr = Nomina.Isr(l.Gross - l.Exempt, periodDays, p);
                    l.Subsidy = Nomina.Subsidy(l.Gross - l.Exempt, periodDays, l.Isr, p);
                    l.Imss = Nomina.Imss(l.Sbc, worked, p, l.DailySalary);
                    l.Employer = Nomina.Employer(l.Sbc, worked, p.RiskPremium, p, l.DailySalary).Total;
                    break;
                case "finiquito":
                    var concepts = Concepts(l);
                    l.Gross = concepts.Sum(c => c.Amount);
                    l.Exempt = concepts.Sum(c => c.Exempt);
                    l.Isr = Nomina.SettlementIsr(concepts, l.DailySalary, p);
                    break;
                default:
                    // PTU is distributed when the payroll run is created; the year-end bonus is days × daily wage.
                    if (run.Kind == "aguinaldo") l.Gross = Round(l.Days * l.DailySalary);
                    l.Exempt = Nomina.ExtraordinaryExempt(run.Kind, l.Gross, p);
                    l.Isr = Nomina.IsrExtraordinary(l.Gross - l.Exempt, l.DailySalary, p);
                    break;
            }
            l.Deductions = l.Isr - l.Subsidy + l.Imss + l.Other + l.IsrAdjustment;
            l.Net = l.Gross - l.Deductions;
        }
        run.Gross = run.Lines.Sum(l => l.Gross);
        run.Deductions = run.Lines.Sum(l => l.Deductions);
        run.Net = run.Lines.Sum(l => l.Net);
        run.EmployerCost = run.Lines.Sum(l => l.Employer);
    }

    public static List<Nomina.Concept> Concepts(PayrollLine l) =>
        l.Concepts is null ? [] : JsonSerializer.Deserialize<List<Nomina.Concept>>(l.Concepts, Json) ?? [];

    /// Recalculates the severance concepts from the termination date (EndDate), pending salary days (Days) and pending vacation
    /// from previous years (VacationDays).
    static void Settle(PayrollRun run, PayrollLine l, DateOnly? hire, PayrollParams p) =>
        l.Concepts = JsonSerializer.Serialize(Nomina.Settlement(l.DailySalary, hire, run.EndDate, run.Cause == "despido", l.Days, l.VacationDays, p), Json);

    /// Payroll draft. Daily wage and SBC are frozen when it is created.
    /// Ordinary: active employees with the days of the period. Year-end bonus: days prorated to the year of the payment date (EndDate).
    /// PTU: the amount is split 50% by days and 50% by the previous year's salaries, including that year's terminations; capped at the larger of
    /// the configured salary days and the average PTU of the last three years (art. 127-VIII LFT).
    /// Severance: one employee, with the cause and the termination date.
    // the remainder left over by the PTU cap is not redistributed.
    public static async Task<(PayrollRun? Run, string? Error)> CreateAsync(AppDb db, DateOnly start, DateOnly end, string kind = "ordinaria",
        decimal? ptuAmount = null, Guid? employeeId = null, string? cause = null)
    {
        if (!PayrollRun.Kinds.Contains(kind)) return (null, "Tipo de nómina inválido.");
        var p = await ParamsAsync(db);
        var employees = await db.Employees.Where(e => e.Active).OrderBy(e => e.Name).ToListAsync();
        var run = new PayrollRun { Kind = kind, StartDate = start, EndDate = end };
        switch (kind)
        {
            case "ordinaria":
                if (end < start || end.DayNumber - start.DayNumber > 31) return (null, "El periodo debe ser de 1 a 31 días.");
                if (await db.PayrollRuns.AnyAsync(r => r.Kind == "ordinaria" && r.StartDate <= end && r.EndDate >= start))
                    return (null, "Ya existe una nómina que se cruza con ese periodo.");
                var days = end.DayNumber - start.DayNumber + 1;
                run.Lines = employees.Select(e => new PayrollLine { EmployeeId = e.Id, Days = days, DailySalary = e.DailySalary, Sbc = Nomina.Sbc(e.DailySalary, e.HireDate, end, p) }).ToList();
                break;
            case "finiquito":
                if (cause is not ("renuncia" or "despido")) return (null, "Indica la causa: renuncia o despido.");
                var emp = employees.SingleOrDefault(e => e.Id == employeeId);
                if (emp is null) return (null, "Elige un empleado activo.");
                if (end < emp.HireDate) return (null, "La baja no puede ser anterior al ingreso.");
                if (await db.PayrollRuns.AnyAsync(r => r.Kind == "finiquito" && r.Status == "borrador" && r.Lines.Any(l => l.EmployeeId == emp.Id)))
                    return (null, "Ese empleado ya tiene un finiquito en borrador.");
                (run.StartDate, run.Cause) = (end, cause);
                var line = new PayrollLine { EmployeeId = emp.Id, DailySalary = emp.DailySalary, Sbc = Nomina.Sbc(emp.DailySalary, emp.HireDate, end, p) };
                run.Lines = [line];
                Settle(run, line, emp.HireDate, p);
                break;
            default:
                run.StartDate = end;
                if (await db.PayrollRuns.AnyAsync(r => r.Kind == kind && r.EndDate.Year == end.Year))
                    return (null, $"Ya existe una nómina de {(kind == "ptu" ? "PTU" : "aguinaldo")} en {end.Year}.");
                if (kind == "aguinaldo")
                    run.Lines = employees.Select(e => new PayrollLine { EmployeeId = e.Id, Days = Nomina.AguinaldoDays(e.HireDate, end.Year, p), DailySalary = e.DailySalary })
                        .Where(l => l.Days > 0).ToList();
                else
                {
                    if (ptuAmount is not > 0) return (null, "Indica el monto de PTU a repartir (10% de la renta gravable).");
                    var year = end.Year - 1;
                    var yearStart = new DateOnly(year, 1, 1);
                    var all = await db.Employees.Where(e => e.Active || e.TerminationDate >= yearStart).OrderBy(e => e.Name).ToListAsync();
                    var worked = all.Select(e => (e, Days: Nomina.DaysWorked(e.HireDate, e.TerminationDate, year))).Where(x => x.Days > 0).ToList();
                    if (worked.Count == 0) return (null, "Nadie trabajó en el año anterior.");
                    var history = await db.PayrollRuns.Where(r => r.Kind == "ptu" && r.Status == "pagada" && r.EndDate.Year >= end.Year - 3 && r.EndDate.Year < end.Year)
                        .SelectMany(r => r.Lines).GroupBy(l => l.EmployeeId).Select(g => new { g.Key, Total = g.Sum(l => l.Gross) }).ToDictionaryAsync(x => x.Key, x => x.Total);
                    var totalDays = worked.Sum(x => (decimal)x.Days);
                    var totalSalary = worked.Sum(x => x.Days * x.e.DailySalary);
                    run.Lines = worked.Select(x => new PayrollLine
                    {
                        EmployeeId = x.e.Id, Days = x.Days, DailySalary = x.e.DailySalary,
                        Gross = Round(Math.Min(ptuAmount.Value / 2 * (x.Days / totalDays + x.Days * x.e.DailySalary / totalSalary),
                            Math.Max(p.PtuCapDays * x.e.DailySalary, history.GetValueOrDefault(x.e.Id) / 3))),
                    }).ToList();
                }
                break;
        }
        if (run.Lines.Count == 0) return (null, "No hay empleados con derecho en esa nómina.");
        Recalculate(run, p);
        db.PayrollRuns.Add(run);
        return (run, null);
    }

    /// Pays the payroll run and generates its journal entry; a severance also terminates the employee.
    public static async Task<string?> PayAsync(AppDb db, Guid id, DateTime? paidAt = null)
    {
        var relational = db.Database.IsRelational();
        await using var tx = relational ? await db.Database.BeginTransactionAsync() : null;
        if (relational) await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"PayrollRuns\" WHERE \"Id\" = {id} FOR UPDATE");

        var run = await db.PayrollRuns.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == id);
        if (run is null) return "Nómina no encontrada.";
        if (run.Status != "borrador") return "Esta nómina ya se pagó.";
        if (run.Net <= 0) return "El neto a pagar debe ser mayor a cero.";
        run.Folio = await Facturacion.NextFolioAsync(db, run.CompanyId, "nomina");
        run.Status = "pagada";
        run.PaidAt = paidAt ?? DateTime.UtcNow;
        if (run.Kind == "finiquito")
            foreach (var e in await db.Employees.Where(e => run.Lines.Select(l => l.EmployeeId).Contains(e.Id)).ToListAsync())
                (e.Active, e.TerminationDate) = (false, run.EndDate);
        await Contabilidad.PostPayrollPaidAsync(db, run);
        await db.SaveChangesAsync();
        if (tx is not null) await tx.CommitAsync();
        return null;
    }

    public record AnnualRow(Guid EmployeeId, string Employee, bool Eligible, string? Reason, decimal Taxable, decimal Isr, decimal Annual, decimal Adjustment);

    /// Annual calculation for each employee with the year's paid payroll runs (art. 97 LISR). It does not apply to anyone who did not work the whole year
    /// (hired after January 1 or left before December 1) or whose income exceeded $400,000: they file on their own.
    public static async Task<List<AnnualRow>> AnnualAsync(AppDb db, int year)
    {
        var p = await ParamsAsync(db);
        var lines = await db.PayrollRuns.Where(r => r.Status == "pagada" && r.EndDate.Year == year && r.Kind != "finiquito")
            .SelectMany(r => r.Lines).GroupBy(l => l.EmployeeId)
            .Select(g => new { g.Key, Taxable = g.Sum(l => l.Gross - l.Exempt), Isr = g.Sum(l => l.Isr), Adjusted = g.Sum(l => l.IsrAdjustment) }).ToListAsync();
        var employees = await db.Employees.ToDictionaryAsync(e => e.Id);
        return lines.Where(l => employees.ContainsKey(l.Key)).Select(l =>
        {
            var e = employees[l.Key];
            var reason = e.HireDate > new DateOnly(year, 1, 1) ? "Ingresó después del 1 de enero"
                : e.TerminationDate < new DateOnly(year, 12, 1) ? "Salió antes del 1 de diciembre"
                : l.Taxable > AnnualDeclarationLimit ? "Ingreso mayor a $400,000: presenta su declaración"
                : null;
            var annual = Nomina.Isr(l.Taxable, p.DaysPerMonth * 12, p);
            var adjustment = reason is null ? annual - l.Isr - l.Adjusted : 0;
            return new AnnualRow(l.Key, e.Name, reason is null, reason, l.Taxable, l.Isr, annual, adjustment);
        }).OrderBy(r => r.Employee).ToList();
    }

    public static void MapRrhh(this WebApplication app)
    {
        var e = app.MapGroup("/api/employees").RequireAuthorization("rrhh");

        e.MapGet("/", (AppDb db) => db.Employees.OrderBy(x => !x.Active).ThenBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.Rfc, x.Position, x.Department, x.HireDate, x.TerminationDate, x.DailySalary, x.Active }).ToListAsync());

        e.MapPost("/", async (EmployeeRequest req, AppDb db) =>
        {
            var errors = Validate(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var x = new Employee { Name = "" };
            Apply(x, req);
            db.Employees.Add(x);
            await db.SaveChangesAsync();
            return Results.Created($"/api/employees/{x.Id}", new { x.Id });
        });

        e.MapPut("/{id:guid}", async (Guid id, EmployeeRequest req, AppDb db) =>
        {
            var errors = Validate(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var x = await db.Employees.SingleOrDefaultAsync(z => z.Id == id);
            if (x is null) return Results.NotFound();
            Apply(x, req);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        var p = app.MapGroup("/api/payroll").RequireAuthorization("rrhh");

        // Company payroll parameters: GET returns the current ones (or the legal ones), PUT validates and saves them; DELETE goes back to the legal ones.
        p.MapGet("/settings", async (AppDb db) => new { Params = await ParamsAsync(db), Defaults = PayrollParams.Default });

        p.MapPut("/settings", async (PayrollParams req, AppDb db, CurrentUser me) =>
        {
            var errors = req.Validate();
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["params"] = errors.ToArray() });
            var c = await db.Companies.SingleAsync(x => x.Id == me.CompanyId);
            c.RiskPremium = Math.Round(req.RiskPremium, 7);
            c.PayrollJson = JsonSerializer.Serialize(req with { RiskPremium = 0 }, Json);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        p.MapDelete("/settings", async (AppDb db, CurrentUser me) =>
        {
            (await db.Companies.SingleAsync(x => x.Id == me.CompanyId)).PayrollJson = null;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        p.MapGet("/", async (AppDb db) =>
        {
            var rows = await db.PayrollRuns.OrderByDescending(r => r.StartDate)
                .Select(r => new { r.Id, r.Folio, r.Kind, r.StartDate, r.EndDate, r.Status, r.PaidAt, r.Gross, r.Deductions, r.Net, r.EmployerCost, Employees = r.Lines.Count })
                .ToListAsync();
            return rows.Select(r => new { r.Id, FolioText = FolioText(r.Folio), r.Kind, r.StartDate, r.EndDate, r.Status, r.PaidAt, r.Gross, r.Deductions, r.Net, r.EmployerCost, r.Employees });
        });

        p.MapGet("/annual/{year:int}", (int year, AppDb db) => AnnualAsync(db, year));

        // Carries the annual calculation difference into a draft ordinary payroll run (normally the last of the year).
        p.MapPost("/annual/{year:int}/apply/{runId:guid}", async (int year, Guid runId, AppDb db) =>
        {
            var run = await db.PayrollRuns.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == runId);
            if (run is not { Status: "borrador", Kind: "ordinaria" }) return Results.Problem("Elige una nómina ordinaria en borrador.", statusCode: 422);
            if (run.EndDate.Year != year) return Results.Problem($"La nómina debe ser de {year}.", statusCode: 422);
            var rows = (await AnnualAsync(db, year)).Where(r => r.Eligible).ToDictionary(r => r.EmployeeId);
            foreach (var l in run.Lines) l.IsrAdjustment = rows.GetValueOrDefault(l.EmployeeId)?.Adjustment ?? 0;
            Recalculate(run, await ParamsAsync(db));
            if (run.Lines.Any(l => l.Net < 0)) return Results.Problem("El ajuste deja un neto negativo; aplícalo en otra nómina o en varias.", statusCode: 422);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        p.MapGet("/{id:guid}", async (Guid id, AppDb db) =>
        {
            var r = await db.PayrollRuns.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id);
            if (r is null) return Results.NotFound();
            var names = await db.Employees.ToDictionaryAsync(x => x.Id, x => x.Name);
            var prm = await ParamsAsync(db);
            var quotas = r.Lines.Select(l => r.Kind == "ordinaria"
                ? Nomina.Employer(l.Sbc, Math.Max(0, l.Days - l.Absences - l.SickDays), prm.RiskPremium, prm, l.DailySalary) : new(0, 0, 0)).ToList();
            return Results.Ok(new
            {
                r.Id, FolioText = FolioText(r.Folio), r.Kind, r.Cause, r.StartDate, r.EndDate, r.Status, r.PaidAt, r.Gross, r.Deductions, r.Net, r.EmployerCost,
                // Breakdown with the current parameters; in a paid payroll run the frozen total is EmployerCost.
                Employer = new { Imss = quotas.Sum(q => q.Imss), Rcv = quotas.Sum(q => q.Rcv), Infonavit = quotas.Sum(q => q.Infonavit), prm.RiskPremium },
                Lines = r.Lines.OrderBy(l => names[l.EmployeeId]).Select(l => new
                {
                    l.EmployeeId, Employee = names[l.EmployeeId], l.Days, l.DailySalary, l.Sbc, l.DoubleHours, l.TripleHours, l.Overtime,
                    l.VacationDays, l.VacationPremium, l.Absences, l.SickDays, l.Exempt,
                    l.Gross, l.Isr, l.Subsidy, l.Imss, l.Other, l.IsrAdjustment, l.Deductions, l.Net, l.Employer, Concepts = Concepts(l),
                }),
            });
        });

        p.MapPost("/", async (PayrollRequest req, AppDb db) =>
        {
            var (run, error) = await CreateAsync(db, req.StartDate, req.EndDate, req.Kind, req.PtuAmount, req.EmployeeId, req.Cause);
            if (error is not null) return Results.Problem(error, statusCode: 422);
            await db.SaveChangesAsync();
            return Results.Created($"/api/payroll/{run!.Id}", new { run.Id });
        });

        p.MapPut("/{id:guid}/lines", async (Guid id, List<PayrollLineRequest> req, AppDb db) =>
        {
            var run = await db.PayrollRuns.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();
            if (run.Status != "borrador") return Results.Problem("Solo se editan nóminas en borrador.", statusCode: 409);
            var prm = await ParamsAsync(db);
            var maxDays = run.Kind switch { "ordinaria" => 31, "aguinaldo" => 90, _ => 366 };
            foreach (var r in req)
            {
                var line = run.Lines.SingleOrDefault(l => l.EmployeeId == r.EmployeeId);
                if (line is null || r.Days < 0 || r.Days > maxDays || new[] { r.Other, r.DoubleHours, r.TripleHours, r.VacationDays, r.Absences, r.SickDays }.Any(x => x < 0)
                    || r.DoubleHours + r.TripleHours > 100 || r.Absences + r.SickDays > r.Days || r.VacationDays > 60)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines"] = ["Revisa los días: no negativos, faltas e incapacidades no mayores a los días del periodo, horas extra hasta 100."] });
                if (run.Kind != "ptu") line.Days = r.Days;
                var ordinary = run.Kind == "ordinaria";
                (line.DoubleHours, line.TripleHours, line.Absences, line.SickDays) = ordinary ? (r.DoubleHours, r.TripleHours, r.Absences, r.SickDays) : (0, 0, 0, 0);
                line.VacationDays = ordinary || run.Kind == "finiquito" ? r.VacationDays : 0;
                line.Other = Round(r.Other);
                if (run.Kind == "finiquito")
                    Settle(run, line, (await db.Employees.SingleAsync(x => x.Id == line.EmployeeId)).HireDate, prm);
            }
            Recalculate(run, prm);
            if (run.Lines.Any(l => l.Net < 0))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines"] = ["Las deducciones no pueden superar el bruto."] });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        p.MapPost("/{id:guid}/pay", async (Guid id, AppDb db) =>
            await PayAsync(db, id) is { } error ? Results.Problem(error, statusCode: 422) : Results.NoContent());

        p.MapDelete("/{id:guid}", async (Guid id, AppDb db) =>
        {
            var run = await db.PayrollRuns.SingleOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();
            if (run.Status != "borrador") return Results.Problem("Una nómina pagada no se borra.", statusCode: 409);
            db.PayrollRuns.Remove(run);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
