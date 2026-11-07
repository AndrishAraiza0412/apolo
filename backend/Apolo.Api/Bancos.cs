using System.Globalization;
using System.Security.Claims;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public record BankImportRequest(string Csv);
public record BankMatchRequest(string Kind, Guid Id);

/// Conciliación bancaria (DESIGN.md §12): se importa el estado de cuenta en CSV y cada movimiento se liga a un cobro o pago.
// ponytail: importación manual de CSV; la conexión directa con el banco (API o SFTP) reemplaza solo ImportAsync.
public static class Bancos
{
    public record Row(DateOnly Date, string Description, string? Reference, decimal Amount);
    public record Suggestion(string Kind, Guid Id, string Label, decimal Amount, bool AlreadyPaid);

    static readonly Dictionary<string, string> KindModule = new() { ["factura"] = "facturacion", ["compra"] = "compras", ["nomina"] = "rrhh" };

    /// Campos separados por coma con comillas opcionales ("1,234.50").
    static List<string> Split(string line)
    {
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
            else if (ch == '"') quoted = !quoted;
            else if (ch == ',' && !quoted) { fields.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(ch);
        }
        fields.Add(cur.ToString().Trim());
        return fields;
    }

    static decimal? Amount(string s)
    {
        s = s.Replace("$", "").Replace(",", "").Replace(" ", "");
        return s.Length == 0 ? 0 : decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    static readonly string[] DateFormats = ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy"];

    /// Encabezados aceptados: fecha, descripción/concepto, referencia (opcional) y monto, o bien cargo/retiro y abono/depósito.
    public static (List<Row> Rows, List<string> Errors) Parse(string csv)
    {
        var lines = csv.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return ([], ["El archivo no tiene movimientos."]);
        var head = Split(lines[0]).Select(h => h.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD))
            .Select(h => new string(h.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())).ToList();
        int Col(params string[] names) => head.FindIndex(h => names.Any(h.Contains));
        int date = Col("fecha"), desc = Col("descripcion", "concepto"), refc = Col("referencia"),
            amount = Col("monto", "importe"), charge = Col("cargo", "retiro"), credit = Col("abono", "deposito");
        if (date < 0 || desc < 0 || (amount < 0 && (charge < 0 || credit < 0)))
            return ([], ["Encabezados requeridos: Fecha, Descripción y Monto (o Cargo y Abono)."]);

        var rows = new List<Row>();
        var errors = new List<string>();
        foreach (var (line, n) in lines.Skip(1).Select((l, i) => (l, i + 2)))
        {
            var f = Split(line);
            string At(int i) => i >= 0 && i < f.Count ? f[i] : "";
            if (!DateOnly.TryParseExact(At(date), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { errors.Add($"Renglón {n}: fecha inválida."); continue; }
            var value = amount >= 0 ? Amount(At(amount)) : Amount(At(credit)) - Amount(At(charge));
            if (value is not { } v || v == 0) { errors.Add($"Renglón {n}: monto inválido."); continue; }
            if (At(desc).Length == 0) { errors.Add($"Renglón {n}: sin descripción."); continue; }
            rows.Add(new(d, At(desc)[..Math.Min(200, At(desc).Length)], Ventas.Clean(At(refc)), v));
        }
        return (rows, errors);
    }

    /// Guarda los movimientos nuevos; uno con la misma fecha, monto y descripción ya importado se omite.
    public static async Task<(int Added, int Skipped, List<string> Errors)> ImportAsync(AppDb db, string csv)
    {
        var (rows, errors) = Parse(csv);
        var existing = (await db.BankTransactions.Select(t => new { t.Date, t.Amount, t.Description }).ToListAsync())
            .Select(t => (t.Date, t.Amount, t.Description)).ToHashSet();
        var added = 0;
        foreach (var r in rows)
        {
            if (!existing.Add((r.Date, r.Amount, r.Description))) continue;
            db.BankTransactions.Add(new BankTransaction { Date = r.Date, Description = r.Description, Reference = r.Reference, Amount = r.Amount });
            added++;
        }
        await db.SaveChangesAsync();
        return (added, rows.Count - added, errors);
    }

    /// Mismo importe: depósitos contra facturas, retiros contra órdenes de compra y nóminas. El folio en la descripción o referencia va primero.
    public static async Task<List<Suggestion>> SuggestAsync(AppDb db, BankTransaction t)
    {
        var linked = await db.BankTransactions.Where(x => x.MatchId != null).Select(x => x.MatchId!.Value).ToListAsync();
        var text = $"{t.Description} {t.Reference}".ToUpperInvariant();
        var list = new List<Suggestion>();
        if (t.Amount > 0)
        {
            // Por cobrar: el saldo completo, o un abono menor cuando el folio viene en el texto. Pagada: el total, para ligarla sin volver a cobrar.
            foreach (var i in await db.Invoices.Where(i => i.Status == "emitida" && t.Amount <= i.Total - i.Paid - i.Credited
                             || i.Status == "pagada" && i.Total == t.Amount && !linked.Contains(i.Id))
                         .Select(i => new { i.Id, i.Folio, i.Status, Balance = i.Total - i.Paid - i.Credited, Customer = db.Customers.Where(c => c.Id == i.CustomerId).Select(c => c.Name).First() }).ToListAsync())
            {
                if (i.Status == "emitida" && t.Amount < i.Balance && !text.Contains(Facturacion.FolioText(i.Folio))) continue;
                list.Add(new("factura", i.Id, $"Factura {Facturacion.FolioText(i.Folio)} · {i.Customer}" + (t.Amount < i.Balance ? $" · abono, saldo {i.Balance:C}" : ""), t.Amount, i.Status == "pagada"));
            }
        }
        else
        {
            // Igual que en facturas: saldo completo, o abono menor cuando el folio viene en el texto.
            foreach (var o in await db.PurchaseOrders.Where(o => o.Status == "recibida" && -t.Amount <= o.Total - o.Paid
                             || o.Status == "pagada" && o.Total == -t.Amount && !linked.Contains(o.Id))
                         .Select(o => new { o.Id, o.Folio, o.Status, Balance = o.Total - o.Paid, Supplier = db.Suppliers.Where(s => s.Id == o.SupplierId).Select(s => s.Name).First() }).ToListAsync())
            {
                if (o.Status == "recibida" && -t.Amount < o.Balance && !text.Contains(Compras.FolioText(o.Folio))) continue;
                list.Add(new("compra", o.Id, $"Orden {Compras.FolioText(o.Folio)} · {o.Supplier}" + (-t.Amount < o.Balance ? $" · abono, saldo {o.Balance:C}" : ""), -t.Amount, o.Status == "pagada"));
            }
            foreach (var r in await db.PayrollRuns.Where(r => r.Net == -t.Amount && !linked.Contains(r.Id)).ToListAsync())
                list.Add(new("nomina", r.Id, $"Nómina {r.StartDate:dd/MM}–{r.EndDate:dd/MM}{(r.Folio is null ? "" : $" · {Rrhh.FolioText(r.Folio)}")}", r.Net, r.Status == "pagada"));
        }
        return list.OrderByDescending(s => text.Contains(s.Label.Split(' ')[1].ToUpperInvariant())).ThenBy(s => s.AlreadyPaid).ToList();
    }

    /// Liga el movimiento. Si el documento sigue pendiente, registra su cobro o pago con la fecha del banco (y su póliza).
    public static async Task<string?> MatchAsync(AppDb db, ClaimsPrincipal user, Guid id, BankMatchRequest req)
    {
        if (!KindModule.TryGetValue(req.Kind, out var module) || !user.HasClaim(Modules.ClaimType, module)) return "Tu rol no puede conciliar ese tipo de documento.";
        var t = await db.BankTransactions.SingleOrDefaultAsync(x => x.Id == id);
        if (t is null) return "Movimiento no encontrado.";
        if (t.Status != "pendiente") return "Este movimiento ya se concilió o se ignoró.";
        var s = (await SuggestAsync(db, t)).FirstOrDefault(x => x.Kind == req.Kind && x.Id == req.Id);
        if (s is null) return "El documento no coincide con el importe del movimiento o ya está conciliado.";

        if (!s.AlreadyPaid)
        {
            var paidAt = t.Date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Local).ToUniversalTime();
            var error = req.Kind switch
            {
                "factura" => await Facturacion.PayAsync(db, req.Id, paidAt, t.Amount),
                "compra" => await Compras.PayAsync(db, req.Id, paidAt, -t.Amount),
                _ => await Rrhh.PayAsync(db, req.Id, paidAt),
            };
            if (error is not null) return error;
        }
        (t.Status, t.MatchKind, t.MatchId) = ("conciliado", req.Kind, req.Id);
        await db.SaveChangesAsync();
        return null;
    }

    public static void MapBancos(this WebApplication app)
    {
        var g = app.MapGroup("/api/bank").RequireAuthorization("contabilidad");

        g.MapGet("/", async (AppDb db, string? status) =>
        {
            var q = db.BankTransactions.AsQueryable();
            if (!string.IsNullOrEmpty(status)) q = q.Where(t => t.Status == status);
            var rows = await q.OrderByDescending(t => t.Date).ThenBy(t => t.Description).Take(500).ToListAsync();
            var result = new List<object>();
            foreach (var t in rows)
                result.Add(new { t.Id, t.Date, t.Description, t.Reference, t.Amount, t.Status, t.MatchKind, t.MatchId,
                    Suggestions = t.Status == "pendiente" ? await SuggestAsync(db, t) : [] });
            return result;
        });

        g.MapPost("/import", async (BankImportRequest req, AppDb db) =>
        {
            if (req.Csv.Length > 2_000_000) return Results.Problem("El archivo pasa de 2 MB.", statusCode: 413);
            var (added, skipped, errors) = await ImportAsync(db, req.Csv);
            return Results.Ok(new { added, skipped, errors });
        });

        g.MapPost("/{id:guid}/match", async (Guid id, BankMatchRequest req, AppDb db, ClaimsPrincipal user) =>
            await MatchAsync(db, user, id, req) is { } error ? Results.Problem(error, statusCode: 422) : Results.NoContent());

        // Cargos sin documento (comisiones, intereses): póliza de egreso a gastos generales contra bancos.
        g.MapPost("/{id:guid}/expense", async (Guid id, AppDb db) =>
        {
            var t = await db.BankTransactions.SingleOrDefaultAsync(x => x.Id == id && x.Status == "pendiente" && x.Amount < 0);
            if (t is null) return Results.NotFound();
            await Contabilidad.PostAsync(db, t.CompanyId, t.Date, "egreso", t.Description, "banco", t.Id,
                (Contabilidad.GastosGenerales, -t.Amount, 0), (Contabilidad.Bancos, 0, -t.Amount));
            (t.Status, t.MatchKind) = ("conciliado", "gasto");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/{id:guid}/ignore", async (Guid id, AppDb db) =>
        {
            var t = await db.BankTransactions.SingleOrDefaultAsync(x => x.Id == id && x.Status == "pendiente");
            if (t is null) return Results.NotFound();
            t.Status = "ignorado";
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
