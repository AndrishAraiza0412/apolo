using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public record ObligationPayRequest(DateOnly Date, decimal? Amount = null);
public record CoefficientRequest(decimal ProfitCoefficient);

/// Obligations to remit: withholdings (ISR and employee contributions), employer IMSS/RCV/INFONAVIT contributions and net VAT.
/// Paying them generates the disbursement journal entry that settles the liability against bank.
// VAT is taken on an accrual basis (208 when invoicing, 118 when receiving purchases); the SAT requires it on a cash basis.
// Separating it would need pending-collection and pending-payment VAT accounts, transferred on each collection and payment.
public static class Impuestos
{
    public record Obligation(string Kind, string Label, decimal Balance, decimal Credit, decimal Due);

    public static async Task<List<Obligation>> ObligationsAsync(AppDb db, DateOnly at)
    {
        var b = (await Contabilidad.BalancesAsync(db, DateOnly.MinValue, at)).ToDictionary(x => x.Code, x => x.Closing);
        decimal Of(string code) => b.GetValueOrDefault(code);
        var (iva, acreditable) = (Of(Contabilidad.IvaTrasladado), Of(Contabilidad.IvaAcreditable) + Of(Contabilidad.IvaRetenidoClientes));
        var isr = await ProvisionalAsync(db, at);
        return
        [
            new("retenciones", "ISR retenido y cuotas obreras", Of(Contabilidad.RetencionesPorPagar), 0, Of(Contabilidad.RetencionesPorPagar)),
            new("cuotas", "Cuotas patronales IMSS, RCV e INFONAVIT", Of(Contabilidad.CuotasPorPagar), 0, Of(Contabilidad.CuotasPorPagar)),
            new("iva", "IVA trasladado menos acreditable y retenido", iva, acreditable, Math.Max(0, iva - acreditable)),
            new("isr", $"ISR provisional {at:yyyy-MM}", isr.Due, 0, isr.Due),
        ];
    }

    public record Provisional(bool Applies, decimal Income, decimal Coefficient, decimal Profit, decimal Isr, decimal Paid, decimal Withheld, decimal Due);

    /// Provisional ISR for a legal entity (LISR 14): accumulated nominal income × profit coefficient × 30%,
    /// minus the year's provisional payments and the ISR that customers withheld.
    // individuals (art. 106 table) and RESICO are not calculated; tax loss and PTU paid are not subtracted.
    // Payments count in the year they are made: December's paid in January is deducted from the following year.
    public static async Task<Provisional> ProvisionalAsync(AppDb db, DateOnly at)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == db.CompanyId);
        if (company?.Rfc is not { Length: 12 }) return new(false, 0, 0, 0, 0, 0, 0, 0);
        var from = new DateOnly(at.Year, 1, 1);
        var to = new DateOnly(at.Year, at.Month, 1).AddMonths(1).AddDays(-1);
        var rows = await db.JournalLines
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { l.AccountId, l.Debit, l.Credit, e.Date, e.Source, e.Concept })
            .Join(db.Accounts, x => x.AccountId, a => a.Id, (x, a) => new { x.Debit, x.Credit, x.Date, x.Source, x.Concept, a.Code, a.Type })
            .Where(x => x.Source != "cierre" && x.Date >= from && x.Date <= to)
            .ToListAsync();
        var income = rows.Where(x => x.Type == "ingreso").Sum(x => x.Credit - x.Debit);
        var withheld = rows.Where(x => x.Code == Contabilidad.IsrRetenidoClientes).Sum(x => x.Debit - x.Credit);
        var paid = await db.JournalEntries.Where(e => e.Source == "impuesto" && e.Concept.StartsWith($"Pago provisional ISR {at.Year}-"))
            .SelectMany(e => e.Lines).Join(db.Accounts.Where(a => a.Code == Contabilidad.PagosProvisionales), l => l.AccountId, a => a.Id, (l, _) => l.Debit)
            .SumAsync();
        var profit = Math.Round(Math.Max(0, income) * company.ProfitCoefficient, 2);
        var isr = Math.Round(profit * 0.30m, 2);
        return new(true, income, company.ProfitCoefficient, profit, isr, paid, withheld, Math.Max(0, isr - paid - withheld));
    }

    /// Partial or full payment of withholdings and contributions. VAT is settled in full: creditable VAT is offset against VAT charged
    /// and the difference is paid; if creditable VAT is larger, the remainder stays as a balance in favor in account 118.
    public static async Task<string?> PayAsync(AppDb db, Guid companyId, string kind, ObligationPayRequest req)
    {
        var o = (await ObligationsAsync(db, req.Date)).FirstOrDefault(x => x.Kind == kind);
        if (o is null) return "Obligación desconocida.";
        if (o.Balance <= 0) return "No hay saldo por pagar a esa fecha.";
        var concept = $"Pago {o.Label.ToLowerInvariant()}";
        if (kind == "iva")
        {
            // It is offset first against the VAT withheld by customers and then against creditable VAT.
            var b = (await Contabilidad.BalancesAsync(db, DateOnly.MinValue, req.Date)).ToDictionary(x => x.Code, x => x.Closing);
            var retained = Math.Min(o.Balance, Math.Max(0, b.GetValueOrDefault(Contabilidad.IvaRetenidoClientes)));
            var offset = Math.Min(o.Balance - retained, Math.Max(0, b.GetValueOrDefault(Contabilidad.IvaAcreditable)));
            await Contabilidad.PostAsync(db, companyId, req.Date, "egreso", concept, "impuesto", null,
                (Contabilidad.IvaTrasladado, o.Balance, 0), (Contabilidad.IvaRetenidoClientes, 0, retained), (Contabilidad.IvaAcreditable, 0, offset),
                (Contabilidad.Bancos, 0, o.Balance - retained - offset));
        }
        else if (kind == "isr")
        {
            var amount = Math.Round(req.Amount ?? o.Due, 2);
            if (amount <= 0 || amount > o.Due) return $"El pago debe ser mayor a cero y no mayor al ISR a cargo ({o.Due:C}).";
            await Contabilidad.PostAsync(db, companyId, req.Date, "egreso", $"Pago provisional ISR {req.Date:yyyy-MM}", "impuesto", null,
                (Contabilidad.PagosProvisionales, amount, 0), (Contabilidad.Bancos, 0, amount));
        }
        else
        {
            var amount = Math.Round(req.Amount ?? o.Balance, 2);
            if (amount <= 0 || amount > o.Balance) return $"El pago debe ser mayor a cero y no mayor al saldo ({o.Balance:C}).";
            var account = kind == "retenciones" ? Contabilidad.RetencionesPorPagar : Contabilidad.CuotasPorPagar;
            await Contabilidad.PostAsync(db, companyId, req.Date, "egreso", concept, "impuesto", null, (account, amount, 0), (Contabilidad.Bancos, 0, amount));
        }
        await db.SaveChangesAsync();
        return null;
    }

    public static void MapImpuestos(this WebApplication app)
    {
        var g = app.MapGroup("/api/taxes").RequireAuthorization("contabilidad");

        g.MapGet("/obligations", (AppDb db, DateOnly? at) => ObligationsAsync(db, at ?? DateOnly.FromDateTime(DateTime.Today)));

        g.MapGet("/provisional", (AppDb db, DateOnly? at) => ProvisionalAsync(db, at ?? DateOnly.FromDateTime(DateTime.Today)));

        g.MapPut("/coefficient", async (CoefficientRequest req, AppDb db, CurrentUser me) =>
        {
            if (req.ProfitCoefficient is < 0 or >= 1)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["profitCoefficient"] = ["El coeficiente va de 0 a 0.9999."] });
            (await db.Companies.SingleAsync(c => c.Id == me.CompanyId)).ProfitCoefficient = Math.Round(req.ProfitCoefficient, 4);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/obligations/{kind}/pay", async (string kind, ObligationPayRequest req, AppDb db, CurrentUser me) =>
            await PayAsync(db, me.CompanyId!.Value, kind, req) is { } error ? Results.Problem(error, statusCode: 422) : Results.NoContent());
    }
}
