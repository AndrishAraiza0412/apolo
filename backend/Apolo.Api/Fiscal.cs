using System.Globalization;
using System.Xml.Linq;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

/// Contabilidad electrónica (Anexo 24: catálogo y balanza 1.3) y cierre del ejercicio.
// ponytail: se generan los XML sin sellar; el envío al buzón tributario lo hace el contador con su e.firma.
public static class Fiscal
{
    static readonly XNamespace Cat = "http://www.sat.gob.mx/esquemas/ContabilidadE/1_3/CatalogoCuentas";
    static readonly XNamespace Bce = "http://www.sat.gob.mx/esquemas/ContabilidadE/1_3/BalanzaComprobacion";
    static string Money(decimal x) => x.ToString("0.00", CultureInfo.InvariantCulture);

    /// Cuentas con movimiento (o saldo) en el periodo que aún no tienen código agrupador.
    public static List<string> Missing(IEnumerable<Account> accounts, IEnumerable<Contabilidad.AccountBalance> balances)
    {
        var used = balances.Where(b => b.Opening != 0 || b.Debit != 0 || b.Credit != 0).Select(b => b.Id).ToHashSet();
        return accounts.Where(a => used.Contains(a.Id) && Contabilidad.SatCodeOf(a) is null).Select(a => $"{a.Code} {a.Name}").ToList();
    }

    public static XDocument Catalog(Company c, int year, int month, IEnumerable<Account> accounts) =>
        new(new XDeclaration("1.0", "utf-8", null), new XElement(Cat + "Catalogo",
            new XAttribute(XNamespace.Xmlns + "catalogocuentas", Cat), new XAttribute("Version", "1.3"),
            new XAttribute("RFC", c.Rfc ?? ""), new XAttribute("Mes", month.ToString("00")), new XAttribute("Anio", year),
            accounts.Where(a => Contabilidad.SatCodeOf(a) is not null).OrderBy(a => a.Code).Select(a => new XElement(Cat + "Ctas",
                new XAttribute("CodAgrup", Contabilidad.SatCodeOf(a)!), new XAttribute("NumCta", a.Code), new XAttribute("Desc", a.Name),
                new XAttribute("Nivel", 1), new XAttribute("Natur", a.Type is "activo" or "gasto" ? "D" : "A")))));

    public static XDocument Balance(Company c, int year, int month, IEnumerable<Contabilidad.AccountBalance> balances) =>
        new(new XDeclaration("1.0", "utf-8", null), new XElement(Bce + "Balanza",
            new XAttribute(XNamespace.Xmlns + "BCE", Bce), new XAttribute("Version", "1.3"),
            new XAttribute("RFC", c.Rfc ?? ""), new XAttribute("Mes", month.ToString("00")), new XAttribute("Anio", year), new XAttribute("TipoEnvio", "N"),
            balances.Where(b => b.Opening != 0 || b.Debit != 0 || b.Credit != 0).Select(b => new XElement(Bce + "Ctas",
                new XAttribute("NumCta", b.Code), new XAttribute("SaldoIni", Money(b.Opening)), new XAttribute("Debe", Money(b.Debit)),
                new XAttribute("Haber", Money(b.Credit)), new XAttribute("SaldoFin", Money(b.Closing))))));

    /// Póliza de cierre al 31 de diciembre: salda ingresos y gastos contra el resultado del ejercicio y bloquea el periodo.
    public static async Task<string?> CloseAsync(AppDb db, Guid companyId, int year)
    {
        var company = await db.Companies.SingleAsync(c => c.Id == companyId);
        var end = new DateOnly(year, 12, 31);
        if (company.ClosedThrough >= end) return $"El ejercicio {year} ya está cerrado.";
        if (end >= DateOnly.FromDateTime(DateTime.Today)) return "Solo se cierran ejercicios terminados.";
        var results = (await Contabilidad.BalancesAsync(db, new DateOnly(year, 1, 1), end))
            .Where(b => b.Type is "ingreso" or "gasto" && b.Closing != 0).ToList();
        // Cada cuenta de resultados se lleva a cero; la diferencia (utilidad o pérdida) va al capital.
        var lines = results.Select(b => (b.Code, Debit: b.Type == "ingreso" ? b.Closing : 0, Credit: b.Type == "gasto" ? b.Closing : 0)).ToList();
        var profit = lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);
        lines.Add((Contabilidad.ResultadoEjercicio, Math.Max(0, -profit), Math.Max(0, profit)));
        // Una cuenta con saldo contrario a su naturaleza queda negativa: se invierte el lado.
        var fixedLines = lines.Select(l => l.Debit < 0 || l.Credit < 0 ? (l.Code, -l.Credit, -l.Debit) : l).ToArray();
        await Contabilidad.PostAsync(db, companyId, end, "diario", $"Cierre del ejercicio {year}", "cierre", null, fixedLines);
        company.ClosedThrough = end;
        await db.SaveChangesAsync();
        return null;
    }

    /// Reabre el último ejercicio cerrado: borra su póliza de cierre.
    public static async Task<string?> ReopenAsync(AppDb db, Guid companyId, int year)
    {
        var company = await db.Companies.SingleAsync(c => c.Id == companyId);
        var end = new DateOnly(year, 12, 31);
        if (company.ClosedThrough != end) return "Solo se reabre el último ejercicio cerrado.";
        var entries = await db.JournalEntries.Include(e => e.Lines).Where(e => e.Source == "cierre" && e.Date == end).ToListAsync();
        db.JournalEntries.RemoveRange(entries);
        company.ClosedThrough = await db.JournalEntries.AnyAsync(e => e.Source == "cierre" && e.Date == end.AddYears(-1)) ? end.AddYears(-1) : null;
        await db.SaveChangesAsync();
        return null;
    }

    public static void MapFiscal(this WebApplication app)
    {
        var g = app.MapGroup("/api/accounting").RequireAuthorization("contabilidad");

        g.MapGet("/sat/{doc}", async (string doc, int year, int month, AppDb db, CurrentUser me) =>
        {
            if (doc is not ("catalogo" or "balanza") || month is < 1 or > 12) return Results.NotFound();
            var company = await db.Companies.SingleAsync(c => c.Id == me.CompanyId);
            var from = new DateOnly(year, month, 1);
            var accounts = await db.Accounts.ToListAsync();
            var balances = await Contabilidad.BalancesAsync(db, from, from.AddMonths(1).AddDays(-1));
            if (Missing(accounts, balances) is { Count: > 0 } missing)
                return Results.Problem($"Asigna código agrupador a: {string.Join(", ", missing)}.", statusCode: 422);
            var xml = doc == "catalogo" ? Catalog(company, year, month, accounts) : Balance(company, year, month, balances);
            var name = $"{company.Rfc ?? "SINRFC"}{year}{month:00}{(doc == "catalogo" ? "CT" : "BN")}.xml";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(Cfdi.Text(xml)), "application/xml", name);
        });

        g.MapGet("/close", async (AppDb db, CurrentUser me) =>
            new { ClosedThrough = await db.Companies.Where(c => c.Id == me.CompanyId).Select(c => c.ClosedThrough).SingleAsync() });

        g.MapPost("/close/{year:int}", async (int year, AppDb db, CurrentUser me) =>
            await CloseAsync(db, me.CompanyId!.Value, year) is { } error ? Results.Problem(error, statusCode: 422) : Results.NoContent());

        g.MapDelete("/close/{year:int}", async (int year, AppDb db, CurrentUser me) =>
            await ReopenAsync(db, me.CompanyId!.Value, year) is { } error ? Results.Problem(error, statusCode: 422) : Results.NoContent());
    }
}
