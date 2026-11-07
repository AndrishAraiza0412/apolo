using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public record AccountRequest(string Code, string Name, string Type, bool Active = true, string? SatCode = null);
public record JournalLineRequest(Guid AccountId, decimal Debit, decimal Credit, string? Description);
public record JournalRequest(DateOnly Date, string Kind, string Concept, List<JournalLineRequest> Lines);

/// Pólizas automáticas de cada operación y pólizas manuales. Saldos = suma de partidas, como el inventario (DESIGN.md §9).
public static class Contabilidad
{
    public const string Caja = "101", Bancos = "102", Clientes = "105", Inventarios = "115", IvaAcreditable = "118",
        Proveedores = "201", IvaTrasladado = "208", RetencionesPorPagar = "213", Capital = "301",
        Ventas = "401", CostoVentas = "501", GastosGenerales = "601", Sueldos = "602",
        CuotasPorPagar = "214", CuotasPatronales = "603", Ptu = "604", DevolucionesVentas = "402",
        PagosProvisionales = "117", IsrRetenidoClientes = "119", IvaRetenidoClientes = "120", ResultadoEjercicio = "305";

    /// Código agrupador del SAT por omisión para las cuentas del catálogo mínimo; las demás se capturan en cada cuenta.
    public static readonly Dictionary<string, string> SatCodes = new()
    {
        [Caja] = "101.01", [Bancos] = "102.01", [Clientes] = "105.01", [Inventarios] = "115.01", [IvaAcreditable] = "118.01",
        [Proveedores] = "201.01", [IvaTrasladado] = "208.01", [RetencionesPorPagar] = "216.01", [CuotasPorPagar] = "211.01",
        [Capital] = "301.01", [Ventas] = "401.01", [DevolucionesVentas] = "402.01", [CostoVentas] = "501.01",
        [GastosGenerales] = "601.84", [Sueldos] = "601.01", [CuotasPatronales] = "601.26",
    };

    public static string? SatCodeOf(Account a) => a.SatCode ?? SatCodes.GetValueOrDefault(a.Code);

    // Catálogo mínimo que se crea la primera vez que una empresa contabiliza algo.
    static readonly (string Code, string Name, string Type)[] Catalog =
    [
        (Caja, "Caja", "activo"), (Bancos, "Bancos", "activo"), (Clientes, "Clientes", "activo"),
        (Inventarios, "Inventarios", "activo"), (IvaAcreditable, "IVA acreditable", "activo"),
        (Proveedores, "Proveedores", "pasivo"), (IvaTrasladado, "IVA trasladado", "pasivo"),
        (RetencionesPorPagar, "Impuestos y cuotas por pagar", "pasivo"), (Capital, "Capital social", "capital"),
        (Ventas, "Ventas", "ingreso"), (CostoVentas, "Costo de ventas", "gasto"),
        (GastosGenerales, "Gastos generales", "gasto"), (Sueldos, "Sueldos y salarios", "gasto"),
        (CuotasPorPagar, "IMSS, RCV e INFONAVIT por pagar", "pasivo"), (CuotasPatronales, "Cuotas patronales", "gasto"),
        (Ptu, "Participación de los trabajadores en las utilidades", "gasto"),
        (DevolucionesVentas, "Devoluciones y descuentos sobre ventas", "ingreso"),
        (PagosProvisionales, "Pagos provisionales de ISR", "activo"), (IsrRetenidoClientes, "ISR retenido por clientes", "activo"),
        (IvaRetenidoClientes, "IVA retenido por clientes", "activo"), (ResultadoEjercicio, "Resultado del ejercicio", "capital"),
    ];

    public static string FolioText(int folio) => $"P-{folio:00000}";

    /// Activo y gasto crecen con cargos; pasivo, capital e ingreso con abonos.
    public static decimal Balance(string type, decimal debit, decimal credit) =>
        type is "activo" or "gasto" ? debit - credit : credit - debit;

    static async Task<Dictionary<string, Guid>> AccountsAsync(AppDb db, Guid companyId)
    {
        var saved = await db.Accounts.IgnoreQueryFilters().Where(a => a.CompanyId == companyId).ToListAsync();
        var all = saved.Concat(db.Accounts.Local.Where(a => a.CompanyId == companyId)).DistinctBy(a => a.Code).ToDictionary(a => a.Code, a => a.Id);
        foreach (var (code, name, type) in Catalog.Where(c => !all.ContainsKey(c.Code)))
        {
            var a = new Account { CompanyId = companyId, Code = code, Name = name, Type = type };
            db.Accounts.Add(a);
            all[code] = a.Id;
        }
        return all;
    }

    /// Agrega la póliza al contexto; la guarda quien llama, en la misma transacción que la operación que la origina.
    public static async Task PostAsync(AppDb db, Guid companyId, DateOnly date, string kind, string concept, string source, Guid? sourceId,
        params (string Code, decimal Debit, decimal Credit)[] lines)
    {
        var live = lines.Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        if (live.Sum(l => l.Debit) != live.Sum(l => l.Credit)) throw new InvalidOperationException($"Póliza descuadrada: {concept}");
        if (live.Count == 0) return;
        var accounts = await AccountsAsync(db, companyId);
        db.JournalEntries.Add(new JournalEntry
        {
            CompanyId = companyId, Folio = await Facturacion.NextFolioAsync(db, companyId, "poliza"), Date = date, Kind = kind,
            Concept = concept, Source = source, SourceId = sourceId,
            Lines = live.Select(l => new JournalLine { CompanyId = companyId, AccountId = accounts[l.Code], Debit = l.Debit, Credit = l.Credit }).ToList(),
        });
    }

    /// Revierte todas las pólizas vigentes de un documento (p. ej. al cancelar una factura emitida).
    public static async Task ReverseSourceAsync(AppDb db, string source, Guid sourceId, DateOnly date, string concept)
    {
        var entries = await db.JournalEntries.Include(e => e.Lines)
            .Where(e => e.Source == source && e.SourceId == sourceId && e.ReversalOf == null
                && !db.JournalEntries.Any(r => r.ReversalOf == e.Id)).ToListAsync();
        foreach (var e in entries) await ReverseAsync(db, e, date, concept);
    }

    public static async Task<JournalEntry> ReverseAsync(AppDb db, JournalEntry e, DateOnly date, string concept)
    {
        var r = new JournalEntry
        {
            CompanyId = e.CompanyId, Folio = await Facturacion.NextFolioAsync(db, e.CompanyId, "poliza"), Date = date, Kind = "diario",
            Concept = concept, Source = e.Source, SourceId = e.SourceId, ReversalOf = e.Id,
            Lines = e.Lines.Select(l => new JournalLine { CompanyId = e.CompanyId, AccountId = l.AccountId, Debit = l.Credit, Credit = l.Debit, Description = l.Description }).ToList(),
        };
        db.JournalEntries.Add(r);
        return r;
    }

    // Pólizas de cada operación. Fecha = día local de la operación, igual que vencimientos y reportes (DateTime.Today).
    static DateOnly Day(DateTime? at) => DateOnly.FromDateTime((at ?? DateTime.UtcNow).ToLocalTime());

    public static async Task PostInvoiceEmittedAsync(AppDb db, Invoice inv)
    {
        var productIds = inv.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).ToList();
        var costs = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Cost);
        var cost = Math.Round(inv.Lines.Where(l => l.ProductId is not null).Sum(l => l.Quantity * costs[l.ProductId!.Value]), 2, MidpointRounding.AwayFromZero);
        await PostAsync(db, inv.CompanyId, Day(inv.IssuedAt), "diario", $"Factura {Facturacion.FolioText(inv.Folio)}", "factura", inv.Id,
            (Clientes, inv.Total, 0), (IsrRetenidoClientes, inv.IsrWithheld, 0), (IvaRetenidoClientes, inv.IvaWithheld, 0),
            (Ventas, 0, inv.Subtotal), (IvaTrasladado, 0, inv.Tax),
            (CostoVentas, cost, 0), (Inventarios, 0, cost));
    }

    /// Cobro de una factura o de una parcialidad.
    public static Task PostInvoicePaidAsync(AppDb db, Invoice inv, decimal? amount = null, DateTime? at = null, int installment = 1) =>
        PostAsync(db, inv.CompanyId, Day(at ?? inv.PaidAt), "ingreso",
            $"Cobro factura {Facturacion.FolioText(inv.Folio)}" + (amount is not null && amount != inv.Total ? $" (parcialidad {installment})" : ""), "factura", inv.Id,
            (Bancos, amount ?? inv.Total, 0), (Clientes, 0, amount ?? inv.Total));

    public static Task PostPurchaseReceivedAsync(AppDb db, PurchaseOrder po) =>
        PostAsync(db, po.CompanyId, Day(po.ReceivedAt), "diario", $"Compra {Compras.FolioText(po.Folio)}", "compra", po.Id,
            (Inventarios, po.Subtotal, 0), (IvaAcreditable, po.Tax, 0), (Proveedores, 0, po.Total));

    /// Nota de crédito: disminuye ventas e IVA trasladado contra la cuenta del cliente.
    public static Task PostCreditNoteAsync(AppDb db, Invoice inv, CreditNote n) =>
        PostAsync(db, inv.CompanyId, Day(n.IssuedAt), "diario", $"Nota de crédito NC-{n.Folio:0000} a factura {Facturacion.FolioText(inv.Folio)}", "factura", inv.Id,
            (DevolucionesVentas, n.Subtotal, 0), (IvaTrasladado, n.Tax, 0), (Clientes, 0, n.Total));

    /// Pago o abono a un proveedor.
    public static Task PostPurchasePaidAsync(AppDb db, PurchaseOrder po, decimal? amount = null, DateTime? at = null) =>
        PostAsync(db, po.CompanyId, Day(at ?? po.PaidAt), "egreso",
            $"Pago compra {Compras.FolioText(po.Folio)}" + (amount is not null && amount != po.Total ? " (abono)" : ""), "compra", po.Id,
            (Proveedores, amount ?? po.Total, 0), (Bancos, 0, amount ?? po.Total));

    /// Percepciones al gasto (PTU a su cuenta), neto contra bancos, retenciones por enterar; las cuotas patronales se provisionan
    /// como gasto contra el pasivo que se paga al IMSS e INFONAVIT. Las partidas en cero se omiten.
    public static Task PostPayrollPaidAsync(AppDb db, PayrollRun run) =>
        PostAsync(db, run.CompanyId, Day(run.PaidAt), "egreso",
            $"Nómina {Rrhh.FolioText(run.Folio)}{(run.Kind == "ordinaria" ? "" : $" {run.Kind}")} ({run.StartDate:dd/MM}–{run.EndDate:dd/MM})", "nomina", run.Id,
            (run.Kind == "ptu" ? Ptu : Sueldos, run.Gross, 0), (Bancos, 0, run.Net), (RetencionesPorPagar, 0, run.Deductions),
            (CuotasPatronales, run.EmployerCost, 0), (CuotasPorPagar, 0, run.EmployerCost));

    /// Saldo inicial, cargos, abonos y saldo final por cuenta en el periodo.
    public static async Task<List<AccountBalance>> BalancesAsync(AppDb db, DateOnly from, DateOnly to)
    {
        var accounts = await db.Accounts.OrderBy(a => a.Code).ToListAsync();
        var lines = await db.JournalLines
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { l.AccountId, l.Debit, l.Credit, e.Date })
            .Where(x => x.Date <= to)
            .GroupBy(x => new { x.AccountId, Before = x.Date < from })
            .Select(g => new { g.Key.AccountId, g.Key.Before, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) })
            .ToListAsync();
        return accounts.Select(a =>
        {
            var before = lines.FirstOrDefault(x => x.AccountId == a.Id && x.Before);
            var during = lines.FirstOrDefault(x => x.AccountId == a.Id && !x.Before);
            var opening = before is null ? 0 : Balance(a.Type, before.Debit, before.Credit);
            decimal debit = during?.Debit ?? 0, credit = during?.Credit ?? 0;
            return new AccountBalance(a.Id, a.Code, a.Name, a.Type, opening, debit, credit, opening + Balance(a.Type, debit, credit));
        }).ToList();
    }

    public record AccountBalance(Guid Id, string Code, string Name, string Type, decimal Opening, decimal Debit, decimal Credit, decimal Closing);

    static async Task<Dictionary<string, string[]>> ValidateAsync(AppDb db, JournalRequest r)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.Concept)) e["concept"] = ["Concepto requerido."];
        if (!JournalEntry.Kinds.Contains(r.Kind)) e["kind"] = ["Tipo inválido."];
        if (r.Lines is not { Count: >= 2 and <= 100 }) e["lines"] = ["La póliza necesita entre 2 y 100 partidas."];
        else
        {
            if (r.Lines.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit > 0) == (l.Credit > 0)))
                e["lines"] = ["Cada partida lleva cargo o abono, no ambos ni ninguno."];
            else if (r.Lines.Sum(l => l.Debit) != r.Lines.Sum(l => l.Credit))
                e["lines"] = [$"La póliza no cuadra: cargos {r.Lines.Sum(l => l.Debit):N2}, abonos {r.Lines.Sum(l => l.Credit):N2}."];
            var ids = r.Lines.Select(l => l.AccountId).Distinct().ToList();
            if (await db.Accounts.CountAsync(a => ids.Contains(a.Id) && a.Active) != ids.Count) e["accountId"] = ["Cuenta inválida o inactiva."];
        }
        return e;
    }

    static IResult? SatCodeError(string? code) =>
        string.IsNullOrWhiteSpace(code) || System.Text.RegularExpressions.Regex.IsMatch(code.Trim(), @"^\d{3}(\.\d{2})?$") ? null
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["satCode"] = ["Código agrupador: tres dígitos, opcionalmente punto y dos más (102.01)."] });

    public static void MapContabilidad(this WebApplication app)
    {
        var g = app.MapGroup("/api/accounting").RequireAuthorization("contabilidad");

        g.MapGet("/accounts", async (AppDb db, CurrentUser me) =>
        {
            await AccountsAsync(db, me.CompanyId!.Value);
            await db.SaveChangesAsync(); // crea el catálogo base la primera vez
            return (await db.Accounts.OrderBy(a => a.Code).ToListAsync()).Select(a => new { a.Id, a.Code, a.Name, a.Type, a.Active, SatCode = SatCodeOf(a) });
        });

        g.MapPost("/accounts", async (AccountRequest req, AppDb db) =>
        {
            var code = req.Code?.Trim() ?? "";
            if (code.Length is 0 or > 20 || string.IsNullOrWhiteSpace(req.Name) || !Account.Types.Contains(req.Type))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Código, nombre y tipo válidos son requeridos."] });
            if (SatCodeError(req.SatCode) is { } bad) return bad;
            if (await db.Accounts.AnyAsync(a => a.Code == code))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Ya existe una cuenta con ese código."] });
            var a = new Account { Code = code, Name = req.Name.Trim(), Type = req.Type, SatCode = Api.Ventas.Clean(req.SatCode) };
            db.Accounts.Add(a);
            await db.SaveChangesAsync();
            return Results.Created($"/api/accounting/accounts/{a.Id}", new { a.Id });
        });

        g.MapPut("/accounts/{id:guid}", async (Guid id, AccountRequest req, AppDb db) =>
        {
            var a = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Nombre requerido."] });
            if (SatCodeError(req.SatCode) is { } bad) return bad;
            a.SatCode = Api.Ventas.Clean(req.SatCode);
            // Código y tipo no cambian: reclasificarían movimientos ya registrados.
            a.Name = req.Name.Trim();
            a.Active = req.Active;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/entries", async (AppDb db, DateOnly? from, DateOnly? to) =>
        {
            var q = db.JournalEntries.AsQueryable();
            if (from is { } f) q = q.Where(e => e.Date >= f);
            if (to is { } t) q = q.Where(e => e.Date <= t);
            var rows = await q.OrderByDescending(e => e.Date).ThenByDescending(e => e.Folio).Take(500).Select(e => new
            {
                e.Id, e.Folio, e.Date, e.Kind, e.Concept, e.Source, e.ReversalOf,
                Total = e.Lines.Sum(l => l.Debit),
                Reversed = db.JournalEntries.Any(r => r.ReversalOf == e.Id),
            }).ToListAsync();
            return rows.Select(e => new { e.Id, FolioText = FolioText(e.Folio), e.Date, e.Kind, e.Concept, e.Source, e.Total, e.Reversed, IsReversal = e.ReversalOf != null });
        });

        g.MapGet("/entries/{id:guid}", async (Guid id, AppDb db) =>
        {
            var e = await db.JournalEntries.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            var accounts = await db.Accounts.ToDictionaryAsync(a => a.Id);
            return Results.Ok(new
            {
                e.Id, FolioText = FolioText(e.Folio), e.Date, e.Kind, e.Concept, e.Source,
                Lines = e.Lines.OrderByDescending(l => l.Debit).Select(l => new
                {
                    accounts[l.AccountId].Code, Account = accounts[l.AccountId].Name, l.Debit, l.Credit, l.Description,
                }),
            });
        });

        g.MapPost("/entries", async (JournalRequest req, AppDb db, CurrentUser me) =>
        {
            var errors = await ValidateAsync(db, req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var e = new JournalEntry
            {
                Folio = await Facturacion.NextFolioAsync(db, me.CompanyId!.Value, "poliza"), Date = req.Date, Kind = req.Kind, Concept = req.Concept.Trim(),
                Lines = req.Lines.Select(l => new JournalLine { AccountId = l.AccountId, Debit = l.Debit, Credit = l.Credit, Description = Api.Ventas.Clean(l.Description) }).ToList(),
            };
            db.JournalEntries.Add(e);
            await db.SaveChangesAsync();
            return Results.Created($"/api/accounting/entries/{e.Id}", new { e.Id });
        });

        g.MapPost("/entries/{id:guid}/reverse", async (Guid id, AppDb db) =>
        {
            var e = await db.JournalEntries.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (e.ReversalOf is not null || await db.JournalEntries.AnyAsync(r => r.ReversalOf == id))
                return Results.Problem("Esta póliza ya es una reversa o ya fue revertida.", statusCode: 409);
            if (e.Source != "manual")
                return Results.Problem("Las pólizas automáticas se revierten cancelando el documento que las originó.", statusCode: 409);
            var r = await ReverseAsync(db, e, DateOnly.FromDateTime(DateTime.Today), $"Reversa de {FolioText(e.Folio)}: {e.Concept}");
            await db.SaveChangesAsync();
            return Results.Ok(new { r.Id });
        });

        g.MapGet("/balances", async (AppDb db, DateOnly? from, DateOnly? to) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            return await BalancesAsync(db, from ?? new DateOnly(today.Year, 1, 1), to ?? today);
        });
    }
}
