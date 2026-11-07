namespace Apolo.Api;

/// Parámetros de nómina de una empresa. Los valores por omisión son los legales de 2026; cada empresa puede subirlos
/// (aguinaldo, prima vacacional, pago de horas extra) y todos se actualizan cada enero con el DOF sin tocar código.
/// Fuentes 2026: tarifa mensual del Anexo 8 RMF 2026 (DOF 28-12-2025), UMA 117.31 (desde el 1 de febrero), subsidio 15.02%
/// de la UMA mensual hasta $11,492.66 (DOF 31-12-2025), salario mínimo general $315.04 (DOF 09-12-2025), cesantía y vejez
/// patronal con la tabla 2026 de la reforma de 2020.
public record PayrollParams
{
    public record Bracket(decimal Lower, decimal Fixed, decimal Rate);
    public record CesantiaStep(decimal UpToUma, decimal Rate);

    public decimal Uma { get; init; } = 117.31m;
    public decimal MinWage { get; init; } = 315.04m;
    public decimal DaysPerMonth { get; init; } = 30.4m;
    public decimal SubsidyRate { get; init; } = 0.1502m;
    public decimal SubsidyLimit { get; init; } = 11_492.66m;
    /// Tarifa mensual del art. 96: límite inferior, cuota fija y tasa sobre el excedente. La anual (art. 152) es esta × 12.
    public List<Bracket> Tariff { get; init; } =
    [
        new(0.01m, 0m, 0.0192m), new(844.60m, 16.22m, 0.0640m), new(7_168.52m, 420.95m, 0.1088m), new(12_598.03m, 1_011.68m, 0.16m),
        new(14_644.65m, 1_339.14m, 0.1792m), new(17_533.65m, 1_856.84m, 0.2136m), new(35_362.84m, 5_665.16m, 0.2352m),
        new(55_736.69m, 10_457.09m, 0.30m), new(106_410.51m, 25_659.23m, 0.32m), new(141_880.67m, 37_009.69m, 0.34m), new(425_642.00m, 133_488.54m, 0.35m),
    ];

    // Prestaciones (mínimos de ley; la empresa puede dar más).
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

    // IMSS: tope del SBC y cuotas obreras.
    public decimal SbcCapUma { get; init; } = 25;
    public decimal WorkerExcessRate { get; init; } = 0.004m;
    /// Prestaciones en dinero 0.25%, pensionados 0.375%, invalidez y vida 0.625%, cesantía y vejez 1.125%.
    public decimal WorkerRate { get; init; } = 0.0025m + 0.00375m + 0.00625m + 0.01125m;
    /// Art. 36 LSS: en salario mínimo el patrón cubre la cuota obrera.
    public bool EmployerPaysMinWageQuota { get; init; } = true;

    // Cuotas patronales.
    public decimal ImssFixedRate { get; init; } = 0.204m;
    public decimal ImssExcessRate { get; init; } = 0.011m;
    /// Prestaciones en dinero 0.70%, pensionados 1.05%, invalidez y vida 1.75%, guarderías 1%.
    public decimal ImssRate { get; init; } = 0.007m + 0.0105m + 0.0175m + 0.01m;
    public decimal RetirementRate { get; init; } = 0.02m;
    public decimal InfonavitRate { get; init; } = 0.05m;
    /// Cesantía y vejez por SBC en veces la UMA; hasta un salario mínimo aplica MinWageCesantiaRate.
    public decimal MinWageCesantiaRate { get; init; } = 0.0315m;
    public List<CesantiaStep> Cesantia { get; init; } =
    [
        new(1.50m, 0.03676m), new(2.00m, 0.04851m), new(2.50m, 0.05556m), new(3.00m, 0.06026m),
        new(3.50m, 0.06361m), new(4.00m, 0.06613m), new(9_999m, 0.07513m),
    ];

    /// Prima de riesgo de trabajo; vive en Company.RiskPremium y se copia aquí al cargar.
    public decimal RiskPremium { get; init; } = 0.0054355m;

    public static readonly PayrollParams Default = new();

    /// Errores de captura; vacío si los parámetros son usables.
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

/// Cálculo fiscal de la nómina: ISR (art. 96 LISR) con subsidio para el empleo, cuotas obreras y patronales del IMSS,
/// horas extra, aguinaldo, PTU, prima vacacional, finiquito y ajuste anual. Todo con los parámetros de la empresa.
// ponytail: faltas e incapacidades descuentan los días cotizados completos (el art. 31 LSS mantiene enfermedad y maternidad
// hasta 7 faltas por mes); la opción del art. 174 RLISR para pagos extraordinarios no se modela.
public static class Nomina
{
    // Atajos con los parámetros 2026 (pruebas y referencias rápidas).
    public static decimal Uma => PayrollParams.Default.Uma;
    public static decimal MinWage => PayrollParams.Default.MinWage;

    static decimal Round(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    /// ISR del periodo: la tarifa mensual se ajusta a los días pagados (días / días por mes).
    public static decimal Isr(decimal gross, decimal days, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        if (gross <= 0 || days <= 0) return 0;
        var k = days / p.DaysPerMonth;
        var row = p.Tariff.LastOrDefault(t => gross >= t.Lower * k) ?? p.Tariff[0];
        return Round(Math.Max(0, (gross - row.Lower * k) * row.Rate + row.Fixed * k));
    }

    /// Subsidio para el empleo del periodo, si el ingreso mensualizado no pasa del límite. Nunca excede el ISR (no se paga en efectivo).
    public static decimal Subsidy(decimal gross, decimal days, decimal isr, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        if (gross <= 0 || days <= 0 || gross / days * p.DaysPerMonth > p.SubsidyLimit) return 0;
        return Math.Min(isr, Round(p.SubsidyRate * p.Uma * days));
    }

    /// Días de vacaciones por año de servicio (art. 76 LFT, reforma 2023) más los extra que dé la empresa.
    public static int VacationDays(int year, PayrollParams? p = null) =>
        (year <= 5 ? 10 + 2 * year : 20 + 2 * (int)Math.Ceiling((year - 5) / 5m)) + (p ?? PayrollParams.Default).ExtraVacationDays;

    /// Año de servicio en curso (1 durante el primer año).
    public static int ServiceYear(DateOnly? hireDate, DateOnly at) =>
        hireDate is DateOnly h ? Math.Max(1, (at.DayNumber - h.DayNumber) / 365 + 1) : 1;

    /// Salario base de cotización: sueldo diario × factor de integración (aguinaldo y prima vacacional), con tope en UMA.
    public static decimal Sbc(decimal dailySalary, DateOnly? hireDate, DateOnly at, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        var vacation = VacationDays(ServiceYear(hireDate, at), p);
        return Round(Math.Min(dailySalary * (1 + (p.AguinaldoDays + vacation * p.VacationPremium) / 365), p.SbcCapUma * p.Uma));
    }

    static bool MinWageQuota(decimal dailySalary, PayrollParams p) => p.EmployerPaysMinWageQuota && dailySalary > 0 && dailySalary <= p.MinWage;

    static decimal WorkerQuota(decimal sbc, decimal days, PayrollParams p) =>
        Round((Math.Max(0, sbc - 3 * p.Uma) * p.WorkerExcessRate + sbc * p.WorkerRate) * days);

    /// Cuotas obreras: excedente de 3 UMA más la tasa sobre el SBC. En salario mínimo las cubre el patrón (van en Employer).
    public static decimal Imss(decimal sbc, decimal days, PayrollParams? p = null, decimal dailySalary = 0)
    {
        p ??= PayrollParams.Default;
        if (sbc <= 0 || days <= 0 || MinWageQuota(dailySalary, p)) return 0;
        return WorkerQuota(sbc, days, p);
    }

    /// Horas extra (art. 67-68 LFT) sobre la hora de sueldo diario / jornada. Exento (art. 93-I LISR): una parte de las dobles hasta
    /// un tope en UMA por semana del periodo; en salario mínimo las dobles son exentas completas. Las triples gravan.
    public static (decimal Pay, decimal Exempt) Overtime(decimal dailySalary, decimal days, decimal doubleHours, decimal tripleHours, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        var hour = dailySalary / p.HoursPerDay;
        var doubles = Round(hour * p.DoubleFactor * doubleHours);
        var pay = doubles + Round(hour * p.TripleFactor * tripleHours);
        var weeks = Math.Max(1, Math.Ceiling(days / 7));
        return (pay, dailySalary <= p.MinWage ? doubles : Math.Min(Round(doubles * p.OvertimeExemptShare), p.OvertimeExemptUmaPerWeek * p.Uma * weeks));
    }

    /// Prima vacacional de los días de vacaciones gozados; exenta hasta el tope en UMA.
    public static (decimal Pay, decimal Exempt) VacationPremiumPay(decimal dailySalary, decimal vacationDays, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        var pay = Round(dailySalary * vacationDays * p.VacationPremium);
        return (pay, Math.Min(pay, p.VacationPremiumExemptUma * p.Uma));
    }

    /// ISR de un pago extraordinario (aguinaldo, PTU): la parte gravada se suma al sueldo mensual y se toma la diferencia de la tarifa.
    public static decimal IsrExtraordinary(decimal taxable, decimal dailySalary, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        if (taxable <= 0) return 0;
        var monthly = Round(dailySalary * p.DaysPerMonth);
        return Isr(monthly + taxable, p.DaysPerMonth, p) - Isr(monthly, p.DaysPerMonth, p);
    }

    /// Días trabajados en el año calendario entre ingreso y baja (o la fecha de corte).
    public static int DaysWorked(DateOnly? hire, DateOnly? termination, int year, DateOnly? until = null)
    {
        var start = hire is DateOnly h && h.Year == year ? h : new DateOnly(year, 1, 1);
        var end = new[] { new DateOnly(year, 12, 31), termination ?? DateOnly.MaxValue, until ?? DateOnly.MaxValue }.Min();
        if (hire > end) return 0;
        return Math.Max(0, end.DayNumber - start.DayNumber + 1);
    }

    /// Días de aguinaldo proporcionales (art. 87 LFT) por los días trabajados en el año calendario.
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

    /// Cuotas patronales del periodo: IMSS (cuota fija sobre la UMA, excedente de 3 UMA, tasas sobre el SBC y riesgo de trabajo),
    /// RCV (retiro y cesantía por tabla) e INFONAVIT. En salario mínimo el IMSS incluye la cuota obrera que absorbe el patrón.
    public static EmployerQuotas Employer(decimal sbc, decimal days, decimal riskRate, PayrollParams? p = null, decimal dailySalary = 0)
    {
        p ??= PayrollParams.Default;
        if (sbc <= 0 || days <= 0) return new(0, 0, 0);
        var imss = p.ImssFixedRate * p.Uma + Math.Max(0, sbc - 3 * p.Uma) * p.ImssExcessRate + sbc * (p.ImssRate + riskRate);
        var absorbed = MinWageQuota(dailySalary, p) ? WorkerQuota(sbc, days, p) : 0;
        return new(Round(imss * days) + absorbed, Round(sbc * (p.RetirementRate + CesantiaRate(sbc, p)) * days), Round(sbc * p.InfonavitRate * days));
    }

    /// Un concepto del finiquito: importe y parte exenta; Separation marca los pagos por separación (indemnización, antigüedad).
    public record Concept(string Name, decimal Amount, decimal Exempt, bool Separation = false);

    /// Finiquito (renuncia) y liquidación (despido injustificado, art. 48-50 y 162 LFT): salario pendiente, aguinaldo y vacaciones
    /// proporcionales con su prima; en liquidación, 3 meses, 20 días por año y prima de antigüedad (12 días por año con salario
    /// topado al doble del mínimo). En renuncia la prima de antigüedad solo con 15 años o más.
    // ponytail: vacaciones proporcionales del año de servicio en curso; saldos de años anteriores se capturan como días pendientes.
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
        // Pagos por separación: exentos hasta 90 UMA por año de servicio (art. 93-XIII LISR), repartido en proporción.
        var separation = list.Where(c => c.Separation).Sum(c => c.Amount);
        var cap = p.SeveranceExemptUmaPerYear * p.Uma * Math.Ceiling(years);
        return list.Where(c => c.Amount > 0)
            .Select(c => c.Separation ? c with { Exempt = Round(Math.Min(separation, cap) * c.Amount / separation) } : c).ToList();
    }

    /// ISR del finiquito: lo ordinario gravado como pago extraordinario; la separación gravada a la tasa efectiva del último
    /// sueldo mensual ordinario (art. 96, penúltimo párrafo LISR).
    public static decimal SettlementIsr(List<Concept> concepts, decimal daily, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        var ordinary = concepts.Where(c => !c.Separation).Sum(c => c.Amount - c.Exempt);
        var separation = concepts.Where(c => c.Separation).Sum(c => c.Amount - c.Exempt);
        var monthly = Round(daily * p.DaysPerMonth);
        var rate = monthly > 0 ? Isr(monthly, p.DaysPerMonth, p) / monthly : 0;
        return IsrExtraordinary(ordinary, daily, p) + Round(separation * rate);
    }

    /// Cálculo anual (art. 97 y 152 LISR): ISR de la tarifa anual sobre el ingreso gravado del año menos el ISR de las nóminas.
    /// Positivo: se retiene la diferencia; negativo: saldo a favor del trabajador.
    // ponytail: el ISR de cada nómina se toma antes del subsidio (el subsidio acreditado cuenta como ISR cubierto); confirmar con el contador.
    public static decimal AnnualAdjustment(decimal taxableYear, decimal isrYear, PayrollParams? p = null)
    {
        p ??= PayrollParams.Default;
        return Isr(taxableYear, p.DaysPerMonth * 12, p) - isrYear;
    }
}
