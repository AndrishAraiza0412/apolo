using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public record InvoiceLineRequest(Guid? ProductId, string Description, decimal Quantity, decimal UnitPrice, decimal TaxRate);
public record InvoiceRequest(Guid CustomerId, string? Notes, List<InvoiceLineRequest> Lines);
public record CreditNoteRequest(decimal Amount, string Kind, string? Reason);

public static class Facturacion
{
    public static readonly decimal[] TaxRates = [0.16m, 0.08m, 0m];

    public static string FolioText(int? folio) => folio is int f ? $"A-{f:0000}" : "Borrador";

    static decimal Round(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    /// Importe e IVA redondeados por partida, luego se suman.
    public static (decimal Subtotal, decimal Tax, decimal Total) Totals(IEnumerable<InvoiceLine> lines) =>
        Totals(lines.Select(l => (l.Quantity, l.UnitPrice, l.TaxRate)));

    public static (decimal Subtotal, decimal Tax, decimal Total) Totals(IEnumerable<(decimal Quantity, decimal Price, decimal TaxRate)> lines)
    {
        decimal sub = 0, tax = 0;
        foreach (var l in lines)
        {
            var amount = Round(l.Quantity * l.Price);
            sub += amount;
            tax += Round(amount * l.TaxRate);
        }
        return (sub, tax, sub + tax);
    }

    public static async Task<Dictionary<string, string[]>> ValidateAsync(AppDb db, InvoiceRequest r)
    {
        var e = new Dictionary<string, string[]>();
        if (!await db.Customers.AnyAsync(c => c.Id == r.CustomerId && c.Active)) e["customerId"] = ["Cliente inválido."];
        if (r.Lines is not { Count: > 0 and <= 200 }) e["lines"] = ["La factura necesita entre 1 y 200 partidas."];
        else
        {
            var productIds = r.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).Distinct().ToList();
            var found = await db.Products.CountAsync(p => productIds.Contains(p.Id) && p.Active);
            if (found != productIds.Count) e["productId"] = ["Producto inválido o inactivo."];
            if (r.Lines.Any(l => string.IsNullOrWhiteSpace(l.Description))) e["description"] = ["Cada partida necesita descripción."];
            if (r.Lines.Any(l => l.Quantity <= 0)) e["quantity"] = ["La cantidad debe ser mayor a cero."];
            if (r.Lines.Any(l => l.UnitPrice < 0)) e["unitPrice"] = ["El precio no puede ser negativo."];
            if (r.Lines.Any(l => !TaxRates.Contains(l.TaxRate))) e["taxRate"] = ["IVA válido: 16%, 8% o 0%."];
        }
        return e;
    }

    internal static void Apply(Invoice inv, InvoiceRequest r)
    {
        inv.CustomerId = r.CustomerId;
        inv.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
        inv.Lines = r.Lines.Select((l, i) => new InvoiceLine
        {
            Position = i, ProductId = l.ProductId, Description = l.Description.Trim(),
            Quantity = l.Quantity, UnitPrice = l.UnitPrice, TaxRate = l.TaxRate,
        }).ToList();
        (inv.Subtotal, inv.Tax, inv.Total) = Totals(inv.Lines);
    }

    public static async Task<int> NextFolioAsync(AppDb db, Guid companyId, string kind)
    {
        if (!db.Database.IsRelational())
        {
            var s = db.FolioSequences.Local.SingleOrDefault(x => x.Kind == kind) ?? await db.FolioSequences.SingleOrDefaultAsync(x => x.Kind == kind);
            if (s is null) db.FolioSequences.Add(s = new FolioSequence { Kind = kind });
            return ++s.LastNumber;
        }
        // Un solo statement atómico: dos emisiones simultáneas nunca obtienen el mismo número.
        var id = Guid.CreateVersion7();
        var rows = await db.Database.SqlQuery<int>($"""
            INSERT INTO "FolioSequences" ("Id", "CompanyId", "Kind", "LastNumber", "CreatedAt", "UpdatedAt")
            VALUES ({id}, {companyId}, {kind}, 1, now(), now())
            ON CONFLICT ("CompanyId", "Kind")
            DO UPDATE SET "LastNumber" = "FolioSequences"."LastNumber" + 1, "UpdatedAt" = now()
            RETURNING "LastNumber" AS "Value"
            """).ToListAsync();
        return rows.Single();
    }

    /// Asigna folio, da salida al inventario y fija vencimiento. Todo o nada. Devuelve el error o null.
    public static async Task<string?> EmitAsync(AppDb db, Guid id)
    {
        var relational = db.Database.IsRelational();
        await using var tx = relational ? await db.Database.BeginTransactionAsync() : null;
        if (relational) await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Invoices\" WHERE \"Id\" = {id} FOR UPDATE");

        var inv = await db.Invoices.Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == id);
        if (inv is null) return "Factura no encontrada.";
        if (inv.Status != "borrador") return "Solo se pueden emitir borradores.";
        if (inv.Lines.Count == 0) return "La factura no tiene partidas.";
        var customer = await db.Customers.SingleAsync(c => c.Id == inv.CustomerId);
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == inv.CompanyId);
        var cfdi = company is not null && Cfdi.Enabled(company);
        if (cfdi && Cfdi.Validate(company!, customer) is { Count: > 0 } problems) return string.Join(" ", problems);

        // Sustitución: primero se cancela la original (regresa su mercancía y revierte sus pólizas), luego se emite la nueva.
        Invoice? replaced = null;
        if (inv.ReplacesId is Guid oldId)
        {
            if (relational) await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Invoices\" WHERE \"Id\" = {oldId} FOR UPDATE");
            replaced = await db.Invoices.Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == oldId);
            if (replaced is not { Status: "emitida", Uuid: not null, Paid: 0, Credited: 0 }) return "La factura que se sustituye ya no está emitida.";
            await CancelCoreAsync(db, replaced, "01");
        }

        var folio = await NextFolioAsync(db, inv.CompanyId, "factura");
        foreach (var l in inv.Lines.Where(l => l.ProductId is not null).OrderBy(l => l.Position))
        {
            var error = await Inventario.RegisterAsync(db, l.ProductId!.Value, "salida", l.Quantity, $"Factura {FolioText(folio)}");
            if (error is not null) return $"{l.Description}: {error}";
        }

        inv.Folio = folio;
        inv.Status = "emitida";
        inv.IssuedAt = DateTime.UtcNow;
        inv.DueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(customer.CreditDays);
        // A crédito: pago en parcialidades o diferido (PPD, forma 99). De contado: una exhibición por transferencia.
        inv.PaymentMethod = customer.CreditDays > 0 ? "PPD" : "PUE";
        inv.PaymentForm = customer.CreditDays > 0 ? "99" : "03";
        var withheld = Cfdi.Withholdings(company, customer, inv.Lines);
        inv.IsrWithheld = withheld.Where(w => w.Tax == "001").Sum(w => w.Amount);
        inv.IvaWithheld = withheld.Where(w => w.Tax == "002").Sum(w => w.Amount);
        inv.Total = inv.Subtotal + inv.Tax - inv.IsrWithheld - inv.IvaWithheld;
        if (cfdi)
        {
            var ids = inv.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).ToList();
            var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
            var xml = Cfdi.Build(company!, customer, inv, products, replaced?.Uuid);
            (inv.Uuid, inv.StampedAt) = Cfdi.Stamp(xml);
            inv.Xml = Cfdi.Text(xml);
        }
        if (replaced is not null) replaced.SubstitutedBy = inv.Uuid;
        await Contabilidad.PostInvoiceEmittedAsync(db, inv);
        await db.SaveChangesAsync();
        if (tx is not null) await tx.CommitAsync();
        return null;
    }

    /// Una factura emitida devuelve su mercancía al inventario. Las pagadas no se cancelan. Una timbrada necesita motivo del SAT.
    public static async Task<string?> CancelAsync(AppDb db, Guid id, string? reason = null)
    {
        var relational = db.Database.IsRelational();
        await using var tx = relational ? await db.Database.BeginTransactionAsync() : null;
        if (relational) await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Invoices\" WHERE \"Id\" = {id} FOR UPDATE");

        var inv = await db.Invoices.Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == id);
        if (inv is null) return "Factura no encontrada.";
        if (inv.Status is "pagada" or "cancelada") return $"Una factura {inv.Status} no se puede cancelar.";
        if (inv.Paid > 0 || inv.Credited > 0) return "Tiene abonos o notas de crédito; no se puede cancelar.";
        if (reason == "01") return "El motivo 01 se aplica al emitir la factura que la sustituye (botón Sustituir).";
        if (inv.Uuid is not null && !Cfdi.CancelReasons.ContainsKey(reason ?? "")) return "Indica el motivo de cancelación del SAT (02, 03 o 04).";
        if (await db.Invoices.AnyAsync(i => i.ReplacesId == id && i.Status == "borrador")) return "Hay un borrador que la sustituye; cancela ese borrador primero.";

        await CancelCoreAsync(db, inv, inv.Uuid is null ? null : reason);
        await db.SaveChangesAsync();
        if (tx is not null) await tx.CommitAsync();
        return null;
    }

    static async Task CancelCoreAsync(AppDb db, Invoice inv, string? reason)
    {
        if (inv.Status == "emitida")
        {
            foreach (var l in inv.Lines.Where(l => l.ProductId is not null))
                await Inventario.RegisterAsync(db, l.ProductId!.Value, "entrada", l.Quantity, $"Cancelación factura {FolioText(inv.Folio)}");
            await Contabilidad.ReverseSourceAsync(db, "factura", inv.Id, DateOnly.FromDateTime(DateTime.Today), $"Cancelación factura {FolioText(inv.Folio)}");
        }
        inv.Status = "cancelada";
        inv.CancelReason = reason;
    }

    /// Borrador nuevo con el contenido de una factura timbrada; al emitirlo, la original se cancela con motivo 01.
    public static async Task<(Guid? Id, string? Error)> SubstituteAsync(AppDb db, Guid id)
    {
        var inv = await db.Invoices.Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == id);
        if (inv is not { Status: "emitida", Uuid: not null, Paid: 0, Credited: 0 }) return (null, "Solo se sustituyen facturas timbradas por cobrar, sin abonos ni notas de crédito.");
        if (await db.Invoices.AnyAsync(i => i.ReplacesId == id && i.Status == "borrador")) return (null, "Ya hay un borrador que la sustituye.");
        var copy = new Invoice { CustomerId = inv.CustomerId, ReplacesId = inv.Id, Notes = inv.Notes };
        Apply(copy, new InvoiceRequest(inv.CustomerId, inv.Notes,
            inv.Lines.OrderBy(l => l.Position).Select(l => new InvoiceLineRequest(l.ProductId, l.Description, l.Quantity, l.UnitPrice, l.TaxRate)).ToList()));
        db.Invoices.Add(copy);
        await db.SaveChangesAsync();
        return (copy.Id, null);
    }

    /// Abono (o cobro total si no se indica monto) con su póliza de ingreso, bajo bloqueo para no contabilizar dos veces.
    /// En una factura PPD timbrada cada abono lleva su recibo electrónico de pago.
    public static async Task<string?> PayAsync(AppDb db, Guid id, DateTime? paidAt = null, decimal? amount = null)
    {
        var relational = db.Database.IsRelational();
        await using var tx = relational ? await db.Database.BeginTransactionAsync() : null;
        if (relational) await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Invoices\" WHERE \"Id\" = {id} FOR UPDATE");

        var inv = await db.Invoices.Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == id);
        if (inv is null) return "Factura no encontrada.";
        if (inv.Status != "emitida") return "Solo se registran pagos de facturas emitidas.";
        var balance = inv.Total - inv.Paid - inv.Credited;
        var paid = Round(amount ?? balance);
        if (paid <= 0 || paid > balance) return $"El abono debe ser mayor a cero y no mayor al saldo ({balance:C}).";
        if (inv.PaymentMethod == "PUE" && paid != balance) return "Una factura PUE se cobra en una sola exhibición.";

        var at = paidAt ?? DateTime.UtcNow;
        var installment = await db.PaymentReceipts.CountAsync(r => r.InvoiceId == id) + 1;
        var receipt = new PaymentReceipt { InvoiceId = inv.Id, Installment = installment, PaidAt = at, Amount = paid, BalanceBefore = balance, PaymentForm = "03" };
        if (inv is { Uuid: not null, PaymentMethod: "PPD" } && await db.Companies.SingleOrDefaultAsync(c => c.Id == inv.CompanyId) is { } company && Cfdi.Enabled(company))
        {
            var customer = await db.Customers.SingleAsync(c => c.Id == inv.CustomerId);
            receipt.Folio = await NextFolioAsync(db, inv.CompanyId, "pago");
            var xml = Cfdi.BuildPayment(company, customer, inv, receipt.Folio.Value, at, "03", paid, balance, installment);
            (receipt.Uuid, _) = Cfdi.Stamp(xml);
            receipt.Xml = Cfdi.Text(xml);
        }
        db.PaymentReceipts.Add(receipt);
        inv.Paid += paid;
        if (inv.Paid + inv.Credited == inv.Total) (inv.Status, inv.PaidAt) = ("pagada", at);
        await Contabilidad.PostInvoicePaidAsync(db, inv, paid, at, installment);
        await db.SaveChangesAsync();
        if (tx is not null) await tx.CommitAsync();
        return null;
    }

    /// Nota de crédito por devolución o descuento sobre una factura emitida, hasta su saldo. Timbrada si la factura lo está.
    // ponytail: la devolución no regresa mercancía al inventario (se registra aparte con una entrada) y las notas no se cancelan.
    public static async Task<(Guid? Id, string? Error)> CreditAsync(AppDb db, Guid id, decimal amount, string kind, string? reason)
    {
        if (!CreditNote.Kinds.Contains(kind)) return (null, "Tipo de nota inválido.");
        if (string.IsNullOrWhiteSpace(reason)) return (null, "Indica el motivo de la nota de crédito.");
        var relational = db.Database.IsRelational();
        await using var tx = relational ? await db.Database.BeginTransactionAsync() : null;
        if (relational) await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Invoices\" WHERE \"Id\" = {id} FOR UPDATE");

        var inv = await db.Invoices.Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == id);
        if (inv is null) return (null, "Factura no encontrada.");
        if (inv.Status != "emitida") return (null, "Solo se emiten notas de crédito sobre facturas por cobrar.");
        // ponytail: la nota sobre una factura con retenciones tendría que devolver la parte proporcional de ISR e IVA retenidos.
        if (inv.IsrWithheld + inv.IvaWithheld > 0) return (null, "Las facturas con retenciones se corrigen cancelando y sustituyendo.");
        var balance = inv.Total - inv.Paid - inv.Credited;
        amount = Round(amount);
        if (amount <= 0 || amount > balance) return (null, $"El importe debe ser mayor a cero y no mayor al saldo ({balance:C}).");

        var parts = Cfdi.Split(inv, amount);
        var note = new CreditNote
        {
            InvoiceId = inv.Id, Folio = await NextFolioAsync(db, inv.CompanyId, "nota"), IssuedAt = DateTime.UtcNow, Kind = kind, Reason = reason.Trim(),
            Subtotal = parts.Sum(p => p.Base), Tax = parts.Sum(p => p.Tax), Total = amount,
        };
        if (inv.Uuid is not null && await db.Companies.SingleOrDefaultAsync(c => c.Id == inv.CompanyId) is { } company && Cfdi.Enabled(company))
        {
            var xml = Cfdi.BuildCredit(company, await db.Customers.SingleAsync(c => c.Id == inv.CustomerId), inv, note);
            (note.Uuid, _) = Cfdi.Stamp(xml);
            note.Xml = Cfdi.Text(xml);
        }
        db.CreditNotes.Add(note);
        inv.Credited += amount;
        if (inv.Paid + inv.Credited == inv.Total) (inv.Status, inv.PaidAt) = ("pagada", note.IssuedAt);
        await Contabilidad.PostCreditNoteAsync(db, inv, note);
        await db.SaveChangesAsync();
        if (tx is not null) await tx.CommitAsync();
        return (note.Id, null);
    }

    public static void MapFacturacion(this WebApplication app)
    {
        var g = app.MapGroup("/api/invoices").RequireAuthorization("facturacion");

        g.MapGet("/", async (AppDb db) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var rows = await db.Invoices.OrderByDescending(i => i.Folio == null).ThenByDescending(i => i.Folio).ThenByDescending(i => i.CreatedAt)
                .Select(i => new
                {
                    i.Id, i.Folio, i.Status, i.IssuedAt, i.DueDate, i.Total, i.Paid, i.Credited, i.CustomerId,
                    Customer = db.Customers.Where(c => c.Id == i.CustomerId).Select(c => c.Name).First(),
                }).ToListAsync();
            return rows.Select(i => new
            {
                i.Id, FolioText = FolioText(i.Folio), i.IssuedAt, i.DueDate, i.Total, Balance = i.Status == "emitida" ? i.Total - i.Paid - i.Credited : 0, i.Customer,
                Status = i.Status == "emitida" && i.DueDate < today ? "vencida" : i.Status,
            });
        });

        // Lo que necesita el formulario. Facturación no requiere permiso de Inventario ni de Ventas.
        g.MapGet("/catalog", async (AppDb db) => new
        {
            Customers = await db.Customers.Where(c => c.Active).OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name, c.CreditDays }).ToListAsync(),
            Products = await db.Products.Where(p => p.Active).OrderBy(p => p.Sku).Select(p => new
            {
                p.Id, p.Sku, p.Name, p.Unit, p.Price,
                Stock = db.InventoryMovements.Where(m => m.ProductId == p.Id).Sum(m => m.Quantity),
            }).ToListAsync(),
        });

        g.MapGet("/{id:guid}", async (Guid id, AppDb db) =>
        {
            var i = await db.Invoices.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id);
            if (i is null) return Results.NotFound();
            var today = DateOnly.FromDateTime(DateTime.Today);
            return Results.Ok(new
            {
                i.Id, FolioText = FolioText(i.Folio), i.CustomerId, i.IssuedAt, i.DueDate, i.PaidAt,
                i.Subtotal, i.Tax, i.Total, i.IsrWithheld, i.IvaWithheld, i.Notes, i.PaymentMethod, i.PaymentForm, i.Uuid, i.StampedAt, i.CancelReason, i.SubstitutedBy,
                Replaces = i.ReplacesId == null ? null : await db.Invoices.Where(x => x.Id == i.ReplacesId).Select(x => new { x.Id, x.Folio, x.Uuid }).SingleOrDefaultAsync(),
                i.Paid, Balance = i.Status == "emitida" ? i.Total - i.Paid - i.Credited : 0,
                i.Credited,
                CreditNotes = await db.CreditNotes.Where(n => n.InvoiceId == i.Id).OrderBy(n => n.Folio)
                    .Select(n => new { n.Id, n.Folio, n.IssuedAt, n.Kind, n.Reason, n.Total, n.Uuid }).ToListAsync(),
                Payments = await db.PaymentReceipts.Where(r => r.InvoiceId == i.Id).OrderBy(r => r.Installment)
                    .Select(r => new { r.Id, r.Installment, r.PaidAt, r.Amount, r.BalanceBefore, r.Folio, r.Uuid }).ToListAsync(),
                Status = i.Status == "emitida" && i.DueDate < today ? "vencida" : i.Status,
                Lines = i.Lines.OrderBy(l => l.Position)
                    .Select(l => new { l.ProductId, l.Description, l.Quantity, l.UnitPrice, l.TaxRate }),
            });
        });

        g.MapPost("/", async (InvoiceRequest req, AppDb db) =>
        {
            var errors = await ValidateAsync(db, req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var inv = new Invoice();
            Apply(inv, req);
            db.Invoices.Add(inv);
            await db.SaveChangesAsync();
            return Results.Created($"/api/invoices/{inv.Id}", new { inv.Id });
        });

        g.MapPut("/{id:guid}", async (Guid id, InvoiceRequest req, AppDb db) =>
        {
            var inv = await db.Invoices.Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == id);
            if (inv is null) return Results.NotFound();
            if (inv.Status != "borrador") return Results.Problem("Solo se pueden editar borradores.", statusCode: 409);
            var errors = await ValidateAsync(db, req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            db.InvoiceLines.RemoveRange(inv.Lines);
            Apply(inv, req);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/{id:guid}/emit", async (Guid id, AppDb db) =>
            await EmitAsync(db, id) is { } error ? Results.Problem(error, statusCode: 422) : Results.NoContent());

        g.MapPost("/{id:guid}/cancel", async (Guid id, AppDb db, string? reason) =>
            await CancelAsync(db, id, reason) is { } error ? Results.Problem(error, statusCode: 422) : Results.NoContent());

        g.MapPost("/{id:guid}/substitute", async (Guid id, AppDb db) =>
        {
            var (newId, error) = await SubstituteAsync(db, id);
            return error is not null ? Results.Problem(error, statusCode: 422) : Results.Created($"/api/invoices/{newId}", new { Id = newId });
        });

        g.MapGet("/{id:guid}/payments/{pid:guid}/xml", async (Guid id, Guid pid, AppDb db) =>
            await db.PaymentReceipts.Where(r => r.InvoiceId == id && r.Id == pid && r.Xml != null).Select(r => new { r.Folio, r.Uuid, r.Xml }).SingleOrDefaultAsync() is { } x
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(x.Xml!), "application/xml", $"P-{x.Folio:0000}_{x.Uuid}.xml")
                : Results.NotFound());

        g.MapGet("/{id:guid}/xml", async (Guid id, AppDb db) =>
            await db.Invoices.Where(i => i.Id == id && i.Xml != null).Select(i => new { i.Folio, i.Uuid, i.Xml }).SingleOrDefaultAsync() is { } x
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(x.Xml!), "application/xml", $"{FolioText(x.Folio)}_{x.Uuid}.xml")
                : Results.NotFound());

        // Datos para la representación impresa.
        g.MapGet("/{id:guid}/print", async (Guid id, AppDb db) =>
        {
            var i = await db.Invoices.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id && x.Status != "borrador");
            if (i is null) return Results.NotFound();
            var c = await db.Customers.SingleAsync(x => x.Id == i.CustomerId);
            var e = await db.Companies.SingleAsync(x => x.Id == i.CompanyId);
            return Results.Ok(new
            {
                FolioText = FolioText(i.Folio), i.IssuedAt, i.DueDate, i.Subtotal, i.Tax, i.Total, i.IsrWithheld, i.IvaWithheld, i.Notes, i.PaymentMethod, i.PaymentForm, i.Uuid, i.StampedAt, i.Status,
                Qr = i.Uuid is Guid u && e.Rfc is not null ? Cfdi.QrSvg(Cfdi.QrUrl(e.Rfc, c.Rfc!, i.Total, u, "PRUEBAS")) : null,
                Emitter = new { e.Name, e.LegalName, e.Rfc, e.ZipCode, Regime = e.TaxRegime is null ? null : $"{e.TaxRegime} {Cfdi.Regimes.GetValueOrDefault(e.TaxRegime)}" },
                Receiver = new { c.Name, c.Rfc, c.ZipCode, Regime = c.TaxRegime is null ? null : $"{c.TaxRegime} {Cfdi.Regimes.GetValueOrDefault(c.TaxRegime)}", Use = $"{c.CfdiUse} {Cfdi.Uses.GetValueOrDefault(c.CfdiUse)}" },
                Lines = i.Lines.OrderBy(l => l.Position).Select(l => new { l.Description, l.Quantity, l.UnitPrice, l.TaxRate, Amount = Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero) }),
            });
        });

        g.MapPost("/{id:guid}/credit-notes", async (Guid id, CreditNoteRequest req, AppDb db) =>
            await CreditAsync(db, id, req.Amount, req.Kind, req.Reason) is { Error: { } error } ? Results.Problem(error, statusCode: 422) : Results.NoContent());

        g.MapGet("/{id:guid}/credit-notes/{nid:guid}/xml", async (Guid id, Guid nid, AppDb db) =>
            await db.CreditNotes.Where(n => n.InvoiceId == id && n.Id == nid && n.Xml != null).Select(n => new { n.Folio, n.Uuid, n.Xml }).SingleOrDefaultAsync() is { } x
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(x.Xml!), "application/xml", $"NC-{x.Folio:0000}_{x.Uuid}.xml")
                : Results.NotFound());

        g.MapPost("/{id:guid}/pay", async (Guid id, AppDb db, decimal? amount) =>
            await PayAsync(db, id, null, amount) is { } error ? Results.Problem(error, statusCode: 422) : Results.NoContent());
    }
}
