namespace Apolo.Api;

/// Payroll parameters for a company. The defaults are the 2026 legal values; each company can raise them
/// (year-end bonus, vacation premium, overtime pay) and all are updated every January from the DOF (Official Gazette) without touching code.
/// 2026 sources: monthly tax table from Annex 8 RMF 2026 (DOF 28-12-2025), UMA (unit of measure and update) 117.31 (since February 1), subsidy 15.02%
/// of the monthly UMA up to $11,492.66 (DOF 31-12-2025), general minimum wage $315.04 (DOF 09-12-2025), employer old-age and
/// unemployment contributions (cesantía y vejez) from the 2026 table of the 2020 reform.
public record PayrollParams
{
    public record Bracket(decimal Lower, decimal Fixed, decimal Rate);
    public record CesantiaStep(decimal UpToUma, decimal Rate);

    public decimal Uma { get; init; } = 117.31m;
    public decimal MinWage { get; init; } = 315.04m;
    public decimal DaysPerMonth { get; init; } = 30.4m;
    public decimal SubsidyRate { get; init; } = 0.1502m;
    public decimal SubsidyLimit { get; init; } = 11_492.66m;
    /// Monthly table from art. 96: lower limit, fixed fee and rate on the excess. The annual one (art. 152) is this × 12.
    public List<Bracket> Tariff { get; init; } =
    [
        new(0.01m, 0m, 0.0192m), new(844.60m, 16.22m, 0.0640m), new(7_168.52m, 420.95m, 0.1088m), new(12_598.03m, 1_011.68m, 0.16m),
        new(14_644.65m, 1_339.14m, 0.1792m), new(17_533.65m, 1_856.84m, 0.2136m), new(35_362.84m, 5_665.16m, 0.2352m),
        new(55_736.69m, 10_457.09m, 0.30m), new(106_410.51m, 25_659.23m, 0.32m), new(141_880.67m, 37_009.69m, 0.34m), new(425_642.00m, 133_488.54m, 0.35m),
    ];

    // Benefits (legal minimums; the company may give more).
    public decimal AguinaldoDays { get; init; } = 15;
    public decimal VacationPremium { get; init; } = 0.25m;
    public int ExtraVacationDays { get; init; }
    public decimal HoursPerDay { get; init; } = 8;
    public decimal DoubleFactor { get; init; } = 2;
    public decimal TripleFactor { get; init; } = 3;
    public decimal PtuCapDays { get; init; } = 90;

    // Exenciones de ISR (art. 93 LISR), en UMA.
    public decimal OvertimeExemptShare { get; init; } = 0.5m;
    public decimal OvertimeExemptUmaPerWeek { get; init; } = 5;
    public decimal AguinaldoExemptUma { get; init; } = 30;
    public decimal PtuExemptUma { get; init; } = 15;
    public decimal VacationPremiumExemptUma { get; init; } = 15;
    public decimal SeveranceExemptUmaPerYear { get; init; } = 90;

    // IMSS: SBC cap and employee contributions.
    public decimal SbcCapUma { get; init; } = 25;
    public decimal WorkerExcessRate { get; init; } = 0.004m;
    /// Cash benefits 0.25%, pensioners 0.375%, disability and life 0.625%, old age and unemployment 1.125%.
    public decimal WorkerRate { get; init; } = 0.0025m + 0.00375m + 0.00625m + 0.01125m;
    /// Art. 36 LSS: at minimum wage the employer covers the employee contribution.
    public bool EmployerPaysMinWageQuota { get; init; } = true;

    // Cuotas patronales.
    public decimal ImssFixedRate { get; init; } = 0.204m;
    public decimal ImssExcessRate { get; init; } = 0.011m;
    /// Cash benefits 0.70%, pensioners 1.05%, disability and life 1.75%, daycare 1%.
    public decimal ImssRate { get; init; } = 0.007m + 0.0105m + 0.0175m + 0.01m;
    public decimal RetirementRate { get; init; } = 0.02m;
    public decimal InfonavitRate { get; init; } = 0.05m;
    /// Old age and unemployment by SBC in multiples of the UMA; up to one minimum wage MinWageCesantiaRate applies.
    public decimal MinWageCesantiaRate { get; init; } = 0.0315m;
    public List<CesantiaStep> Cesantia { get; init; } =
    [
        new(1.50m, 0.03676m), new(2.00m, 0.04851m), new(2.50m, 0.05556m), new(3.00m, 0.06026m),
        new(3.50m, 0.06361m), new(4.00m, 0.06613m), new(9_999m, 0.07513m),
    ];

    /// Occupational risk premium; it lives in Company.RiskPremium and is copied here on load.
    public decimal RiskPremium { get; init; } = 0.0054355m;

    public static readonly PayrollParams Default = new();

    /// Input errors; empty if the parameters are usable.
    public List<string> Validate()
    {
        var e = new List<string>();
        if (Uma <= 0 || MinWage <= 0 || DaysPerMonth <= 0) e.Add("UMA, salario mínimo y días por mes deben ser mayores a cero.");
        if (Tariff.Count == 0 || Tariff.Zip(Tariff.Skip(1)).Any(t => t.Second.Lower <= t.First.Lower) || Tariff.Any(t => t.Rate is < 0 or > 1 || t.Fixed < 0))
            e.Add("La tarifa de ISR necesita renglones con límite inferior creciente y tasa entre 0 y 100%.");
        if (AguinaldoDays < 15) e.Add("El aguinaldo no puede ser menor a 15 días (art. 87 LFT).");
        if (VacationPremium < 0.25m) e.Add("La prima vacacional no puede ser menor al 25% (art. 80 LFT).");
        if (ExtraVacationDays < 0) e.Add("Los días extra de vacaciones no pueden ser negativos.");
        if (DoubleFactor < 2 || TripleFactor < 3) e.Add("Las horas extra se pagan al menos al doble y al triple (art. 67-68 LFT).");
        if (HoursPerDay is <= 0 or > 8) e.Add("La jornada diaria va de 1 a 8 horas.");
        if (PtuCapDays < 90) e.Add("El tope de PTU no puede ser menor a tres meses de salario (90 días).");
        if (Cesantia.Count == 0 || Cesantia.Zip(Cesantia.Skip(1)).Any(c => c.Second.UpToUma <= c.First.UpToUma))
            e.Add("La tabla de cesantía y vejez necesita límites crecientes.");
        if (new[] { SubsidyRate, OvertimeExemptShare, WorkerExcessRate, WorkerRate, ImssFixedRate, ImssExcessRate, ImssRate, RetirementRate, InfonavitRate, MinWageCesantiaRate }
            .Concat(Cesantia.Select(c => c.Rate)).Any(r => r is < 0 or > 1))
            e.Add("Las tasas van de 0 a 100%.");
        if (RiskPremium is < 0.005m or > 0.15m) e.Add("La prima de riesgo va de 0.5% a 15%.");
        return e;
    }
}

/// Payroll tax calculation: ISR (art. 96 LISR) with employment subsidy, IMSS employee and employer contributions,
/// overtime, year-end bonus, PTU, vacation premium, severance and annual adjustment. All using the company's parameters.
// absences and sick leave deduct the full contributed days (art. 31 LSS keeps sickness and maternity
// up to 7 absences per month); the art. 174 RLISR option for extraordinary payments is not modeled.
public static class Nomina
{
    // Shortcuts with the 2026 parameters (tests and quick references).
    public static decimal Uma => PayrollParams.Default.Uma;
    public static decimal MinWage => PayrollParams.Default.MinWage;

    static decimal Round(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    /// ISR for the period: the monthly table is adjusted to the days paid (days / days per month).
    public static decimal Isr(decimal gross, decimal days, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        if (gross <= 0 || days <= 0) return 0;
        var k = days / p.DaysPerMonth;
        var row = p.Tariff.LastOrDefault(t => gross >= t.Lower * k) ?? p.Tariff[0];
        return Round(Math.Max(0, (gross - row.Lower * k) * row.Rate + row.Fixed * k));
    }

    /// Employment subsidy for the period, if the monthly-equivalent income does not exceed the limit. It never exceeds the ISR (it is not paid in cash).
    public static decimal Subsidy(decimal gross, decimal days, decimal isr, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        if (gross <= 0 || days <= 0 || gross / days * p.DaysPerMonth > p.SubsidyLimit) return 0;
        return Math.Min(isr, Round(p.SubsidyRate * p.Uma * days));
    }

    /// Vacation days per year of service (art. 76 LFT, 2023 reform) plus any extra the company gives.
    public static int VacationDays(int year, PayrollParams? p = null) =>
        (year <= 5 ? 10 + 2 * year : 20 + 2 * (int)Math.Ceiling((year - 5) / 5m)) + (p ?? PayrollParams.Default).ExtraVacationDays;

    /// Current year of service (1 during the first year).
    public static int ServiceYear(DateOnly? hireDate, DateOnly at) =>
        hireDate is DateOnly h ? Math.Max(1, (at.DayNumber - h.DayNumber) / 365 + 1) : 1;

    /// Base contribution salary (SBC): daily wage × integration factor (year-end bonus and vacation premium), capped in UMA.
    public static decimal Sbc(decimal dailySalary, DateOnly? hireDate, DateOnly at, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        var vacation = VacationDays(ServiceYear(hireDate, at), p);
        return Round(Math.Min(dailySalary * (1 + (p.AguinaldoDays + vacation * p.VacationPremium) / 365), p.SbcCapUma * p.Uma));
    }

    static bool MinWageQuota(decimal dailySalary, PayrollParams p) => p.EmployerPaysMinWageQuota && dailySalary > 0 && dailySalary <= p.MinWage;

    static decimal WorkerQuota(decimal sbc, decimal days, PayrollParams p) =>
        Round((Math.Max(0, sbc - 3 * p.Uma) * p.WorkerExcessRate + sbc * p.WorkerRate) * days);

    /// Employee contributions: excess over 3 UMA plus the rate on the SBC. At minimum wage the employer covers them (they go in Employer).
    public static decimal Imss(decimal sbc, decimal days, PayrollParams? p = null, decimal dailySalary = 0)
    {
        p ??= PayrollParams.Default;
        if (sbc <= 0 || days <= 0 || MinWageQuota(dailySalary, p)) return 0;
        return WorkerQuota(sbc, days, p);
    }

    /// Overtime (art. 67-68 LFT) on the hourly rate of daily wage / working day. Exempt (art. 93-I LISR): part of the double-time hours up to
    /// a cap in UMA per week of the period; at minimum wage double-time hours are fully exempt. Triple-time hours are taxed.
    public static (decimal Pay, decimal Exempt) Overtime(decimal dailySalary, decimal days, decimal doubleHours, decimal tripleHours, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        var hour = dailySalary / p.HoursPerDay;
        var doubles = Round(hour * p.DoubleFactor * doubleHours);
        var pay = doubles + Round(hour * p.TripleFactor * tripleHours);
        var weeks = Math.Max(1, Math.Ceiling(days / 7));
        return (pay, dailySalary <= p.MinWage ? doubles : Math.Min(Round(doubles * p.OvertimeExemptShare), p.OvertimeExemptUmaPerWeek * p.Uma * weeks));
    }

    /// Vacation premium on the vacation days taken; exempt up to the cap in UMA.
    public static (decimal Pay, decimal Exempt) VacationPremiumPay(decimal dailySalary, decimal vacationDays, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        var pay = Round(dailySalary * vacationDays * p.VacationPremium);
        return (pay, Math.Min(pay, p.VacationPremiumExemptUma * p.Uma));
    }

    /// ISR on an extraordinary payment (year-end bonus, PTU): the taxable portion is added to the monthly wage and the difference in the table is taken.
    public static decimal IsrExtraordinary(decimal taxable, decimal dailySalary, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        if (taxable <= 0) return 0;
        var monthly = Round(dailySalary * p.DaysPerMonth);
        return Isr(monthly + taxable, p.DaysPerMonth, p) - Isr(monthly, p.DaysPerMonth, p);
    }

    /// Days worked in the calendar year between hire and termination (or the cut-off date).
    public static int DaysWorked(DateOnly? hire, DateOnly? termination, int year, DateOnly? until = null)
    {
        var start = hire is DateOnly h && h.Year == year ? h : new DateOnly(year, 1, 1);
        var end = new[] { new DateOnly(year, 12, 31), termination ?? DateOnly.MaxValue, until ?? DateOnly.MaxValue }.Min();
        if (hire > end) return 0;
        return Math.Max(0, end.DayNumber - start.DayNumber + 1);
    }

    /// Prorated year-end bonus days (art. 87 LFT) for the days worked in the calendar year.
    public static decimal AguinaldoDays(DateOnly? hireDate, int year, PayrollParams? p = null, DateOnly? until = null)
    {
        p ??= PayrollParams.Default;
        return Round(p.AguinaldoDays * DaysWorked(hireDate, null, year, until) / (DateTime.IsLeapYear(year) ? 366 : 365));
    }

    /// Exenciones de aguinaldo y PTU.
    public static decimal ExtraordinaryExempt(string kind, decimal gross, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        return Math.Min(gross, (kind == "aguinaldo" ? p.AguinaldoExemptUma : p.PtuExemptUma) * p.Uma);
    }

    static decimal CesantiaRate(decimal sbc, PayrollParams p) =>
        sbc <= p.MinWage ? p.MinWageCesantiaRate : (p.Cesantia.FirstOrDefault(c => sbc / p.Uma <= c.UpToUma) ?? p.Cesantia[^1]).Rate;

    public record EmployerQuotas(decimal Imss, decimal Rcv, decimal Infonavit)
    {
        public decimal Total => Imss + Rcv + Infonavit;
    }

    /// Employer contributions for the period: IMSS (fixed fee on the UMA, excess over 3 UMA, rates on the SBC and occupational risk),
    /// RCV (retirement and unemployment by table) and INFONAVIT. At minimum wage the IMSS includes the employee contribution the employer absorbs.
    public static EmployerQuotas Employer(decimal sbc, decimal days, decimal riskRate, PayrollParams? p = null, decimal dailySalary = 0)
    {
        p ??= PayrollParams.Default;
        if (sbc <= 0 || days <= 0) return new(0, 0, 0);
        var imss = p.ImssFixedRate * p.Uma + Math.Max(0, sbc - 3 * p.Uma) * p.ImssExcessRate + sbc * (p.ImssRate + riskRate);
        var absorbed = MinWageQuota(dailySalary, p) ? WorkerQuota(sbc, days, p) : 0;
        return new(Round(imss * days) + absorbed, Round(sbc * (p.RetirementRate + CesantiaRate(sbc, p)) * days), Round(sbc * p.InfonavitRate * days));
    }

    /// A severance concept: amount and exempt portion; Separation marks separation payments (indemnity, seniority).
    public record Concept(string Name, decimal Amount, decimal Exempt, bool Separation = false);

    /// Severance (resignation) and settlement (unjustified dismissal, art. 48-50 and 162 LFT): pending salary, prorated year-end bonus and vacation
    /// with their premium; for a settlement, 3 months, 20 days per year of service and seniority premium (12 days per year with salary
    /// capped at twice the minimum). On resignation the seniority premium applies only with 15 years or more.
    // prorated vacation for the current year of service; balances from previous years are entered as pending days.
    public static List<Concept> Settlement(decimal daily, DateOnly? hire, DateOnly end, bool unjustified, decimal pendingDays,
        decimal pendingVacation, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        var years = hire is DateOnly h ? (end.DayNumber - h.DayNumber + 1) / 365m : 0;
        var serviceYear = ServiceYear(hire, end);
        var anniversary = hire is DateOnly hd ? hd.AddYears(serviceYear - 1) : new DateOnly(end.Year, 1, 1);
        var vacation = Round(VacationDays(serviceYear, p) * (end.DayNumber - anniversary.DayNumber + 1) / 365m) + pendingVacation;
        var aguinaldo = Round(AguinaldoDays(hire, end.Year, p, end) * daily);
        var (premium, premiumExempt) = VacationPremiumPay(daily, vacation, p);
        var list = new List<Concept>
        {
            new("Salario pendiente", Round(pendingDays * daily), 0),
            new("Aguinaldo proporcional", aguinaldo, Math.Min(aguinaldo, p.AguinaldoExemptUma * p.Uma)),
            new($"Vacaciones ({vacation:0.##} días)", Round(vacation * daily), 0),
            new("Prima vacacional", premium, premiumExempt),
        };
        if (unjustified)
        {
            list.Add(new("Indemnización de 3 meses", Round(90 * daily), 0, true));
            list.Add(new("20 días por año", Round(20 * daily * years), 0, true));
        }
        if (unjustified || years >= 15) list.Add(new("Prima de antigüedad", Round(12 * Math.Min(daily, 2 * p.MinWage) * years), 0, true));
        // Separation payments: exempt up to 90 UMA per year of service (art. 93-XIII LISR), distributed proportionally.
        var separation = list.Where(c => c.Separation).Sum(c => c.Amount);
        var cap = p.SeveranceExemptUmaPerYear * p.Uma * Math.Ceiling(years);
        return list.Where(c => c.Amount > 0)
            .Select(c => c.Separation ? c with { Exempt = Round(Math.Min(separation, cap) * c.Amount / separation) } : c).ToList();
    }

    /// ISR on severance: the taxable ordinary part as an extraordinary payment; the taxable separation part at the effective rate of the last
    /// ordinary monthly wage (art. 96, penultimate paragraph LISR).
    public static decimal SettlementIsr(List<Concept> concepts, decimal daily, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        var ordinary = concepts.Where(c => !c.Separation).Sum(c => c.Amount - c.Exempt);
        var separation = concepts.Where(c => c.Separation).Sum(c => c.Amount - c.Exempt);
        var monthly = Round(daily * p.DaysPerMonth);
        var rate = monthly > 0 ? Isr(monthly, p.DaysPerMonth, p) / monthly : 0;
        return IsrExtraordinary(ordinary, daily, p) + Round(separation * rate);
    }

    /// Annual calculation (art. 97 and 152 LISR): ISR from the annual table on the year's taxable income minus the ISR from the payroll runs.
    /// Positive: the difference is withheld; negative: balance in favor of the worker.
    // each payroll run's ISR is taken before the subsidy (the credited subsidy counts as ISR paid); confirm with the accountant.
    public static decimal AnnualAdjustment(decimal taxableYear, decimal isrYear, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        return Isr(taxableYear, p.DaysPerMonth * 12, p) - isrYear;
    }
}
