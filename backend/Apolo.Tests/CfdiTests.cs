using System.Xml.Linq;
using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class CfdiTests
{
    static readonly XNamespace C = "http://www.sat.gob.mx/cfd/4", Tfd = "http://www.sat.gob.mx/TimbreFiscalDigital";
    readonly Guid companyId = Guid.NewGuid();
    readonly AppDb db;

    public CfdiTests()
    {
        db = new(new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new CurrentUser { CompanyId = companyId, UserId = Guid.NewGuid() });
        db.Companies.Add(new Company { Id = companyId, Name = "Demo", Rfc = "EKU9003173C9", LegalName = "Escuela Kemper Urgate", TaxRegime = "601", ZipCode = "42501" });
        db.SaveChanges();
    }

    async Task<Invoice> Draft(Customer c)
    {
        var p = new Product { Sku = "A-1", Name = "Producto", SatCode = "31161500", SatUnit = "H87", Price = 100 };
        db.AddRange(c, p);
        await db.SaveChangesAsync();
        await Inventario.RegisterAsync(db, p.Id, "entrada", 10, null);
        var inv = new Invoice
        {
            CustomerId = c.Id,
            Lines =
            [
                new() { Position = 0, ProductId = p.Id, Description = "Producto", Quantity = 3, UnitPrice = 33.333m, TaxRate = 0.16m },
                new() { Position = 1, Description = "Instalación", Quantity = 1, UnitPrice = 500, TaxRate = 0m },
            ],
        };
        (inv.Subtotal, inv.Tax, inv.Total) = Facturacion.Totals(inv.Lines);
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();
        return inv;
    }

    [Fact]
    public async Task Emit_stamps_cfdi_whose_amounts_match_the_invoice()
    {
        var inv = await Draft(new Customer { Name = "Cliente Uno", Rfc = "CUN120315AB1", TaxRegime = "601", ZipCode = "76000", CreditDays = 30 });
        Assert.Null(await Facturacion.EmitAsync(db, inv.Id));

        Assert.NotNull(inv.Uuid);
        var x = XDocument.Parse(inv.Xml!).Root!;
        Assert.Equal(("4.0", "PPD", "99", "600.00", "16.00", "616.00"),
            (x.Attribute("Version")!.Value, x.Attribute("MetodoPago")!.Value, x.Attribute("FormaPago")!.Value,
             x.Attribute("SubTotal")!.Value, x.Element(C + "Impuestos")!.Attribute("TotalImpuestosTrasladados")!.Value, x.Attribute("Total")!.Value));
        Assert.Equal(inv.Total, decimal.Parse(x.Attribute("Total")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        var concepts = x.Element(C + "Conceptos")!.Elements().ToList();
        Assert.Equal(("31161500", "84111506"), (concepts[0].Attribute("ClaveProdServ")!.Value, concepts[1].Attribute("ClaveProdServ")!.Value));
        Assert.Equal(["0.160000", "0.000000"], x.Element(C + "Impuestos")!.Element(C + "Traslados")!.Elements().Select(t => t.Attribute("TasaOCuota")!.Value));
        Assert.Equal(inv.Uuid.ToString()!.ToUpperInvariant(), x.Descendants(Tfd + "TimbreFiscalDigital").Single().Attribute("UUID")!.Value);
        Assert.Equal("CLIENTE UNO", x.Element(C + "Receptor")!.Attribute("Nombre")!.Value);
    }

    [Fact]
    public async Task Emit_is_refused_without_receiver_tax_data_and_burns_no_stock()
    {
        var inv = await Draft(new Customer { Name = "Sin datos", Rfc = "SDA120315AB1" });
        Assert.Contains("régimen fiscal", await Facturacion.EmitAsync(db, inv.Id));
        Assert.Equal("borrador", inv.Status);
        Assert.Equal(10, await Inventario.Stock(db, inv.Lines[0].ProductId!.Value));
    }

    [Fact]
    public async Task Publico_general_uses_generic_receiver_and_cancel_needs_sat_reason()
    {
        var inv = await Draft(new Customer { Name = "Mostrador", Rfc = Cfdi.PublicoGeneral });
        Assert.Null(await Facturacion.EmitAsync(db, inv.Id));
        var r = XDocument.Parse(inv.Xml!).Root!.Element(C + "Receptor")!;
        Assert.Equal(("PUBLICO EN GENERAL", "616", "S01", "42501"),
            (r.Attribute("Nombre")!.Value, r.Attribute("RegimenFiscalReceptor")!.Value, r.Attribute("UsoCFDI")!.Value, r.Attribute("DomicilioFiscalReceptor")!.Value));

        Assert.NotNull(await Facturacion.CancelAsync(db, inv.Id));
        Assert.Null(await Facturacion.CancelAsync(db, inv.Id, "02"));
        Assert.Equal(("cancelada", "02"), (inv.Status, inv.CancelReason));
    }

    static readonly XNamespace P = "http://www.sat.gob.mx/Pagos20";

    [Fact]
    public async Task Paying_a_ppd_invoice_stamps_a_payment_receipt_with_tax_breakdown()
    {
        var inv = await Draft(new Customer { Name = "Cliente Uno", Rfc = "CUN120315AB1", TaxRegime = "601", ZipCode = "76000", CreditDays = 30 });
        Assert.Null(await Facturacion.EmitAsync(db, inv.Id));
        Assert.Null(await Facturacion.PayAsync(db, inv.Id, new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc)));

        var r = await db.PaymentReceipts.SingleAsync();
        var x = XDocument.Parse(r.Xml!).Root!;
        Assert.Equal(("P", "XXX", "0", "CP01"), (x.Attribute("TipoDeComprobante")!.Value, x.Attribute("Moneda")!.Value, x.Attribute("Total")!.Value,
            x.Element(C + "Receptor")!.Attribute("UsoCFDI")!.Value));
        var totals = x.Descendants(P + "Totales").Single();
        Assert.Equal(("616.00", "100.00", "16.00", "500.00", "0.00"), (totals.Attribute("MontoTotalPagos")!.Value,
            totals.Attribute("TotalTrasladosBaseIVA16")!.Value, totals.Attribute("TotalTrasladosImpuestoIVA16")!.Value,
            totals.Attribute("TotalTrasladosBaseIVA0")!.Value, totals.Attribute("TotalTrasladosImpuestoIVA0")!.Value));
        var doc = x.Descendants(P + "DoctoRelacionado").Single();
        Assert.Equal((inv.Uuid.ToString()!.ToUpperInvariant(), "616.00", "0.00"),
            (doc.Attribute("IdDocumento")!.Value, doc.Attribute("ImpPagado")!.Value, doc.Attribute("ImpSaldoInsoluto")!.Value));
        // Un solo Complemento con el pago y el timbre.
        Assert.Single(x.Elements(C + "Complemento"));
        Assert.Single(x.Descendants(Tfd + "TimbreFiscalDigital"));
    }

    [Fact]
    public async Task Partial_payments_issue_one_receipt_per_installment_and_settle_at_the_end()
    {
        var inv = await Draft(new Customer { Name = "Cliente Uno", Rfc = "CUN120315AB1", TaxRegime = "601", ZipCode = "76000", CreditDays = 30 });
        Assert.Null(await Facturacion.EmitAsync(db, inv.Id));
        Assert.NotNull(await Facturacion.PayAsync(db, inv.Id, null, 700)); // más que el saldo
        Assert.Null(await Facturacion.PayAsync(db, inv.Id, null, 308));
        Assert.Equal(("emitida", 308m), (inv.Status, inv.Paid));
        Assert.NotNull(await Facturacion.CancelAsync(db, inv.Id, "02")); // con abonos ya no se cancela
        Assert.Null(await Facturacion.PayAsync(db, inv.Id));               // sin monto: liquida el saldo
        Assert.Equal(("pagada", 616m), (inv.Status, inv.Paid));

        var receipts = await db.PaymentReceipts.OrderBy(r => r.Installment).ToListAsync();
        Assert.Equal([1, 2], receipts.Select(r => r.Installment));
        var doc = XDocument.Parse(receipts[1].Xml!).Root!.Descendants(P + "DoctoRelacionado").Single();
        Assert.Equal(("2", "308.00", "308.00", "0.00"), (doc.Attribute("NumParcialidad")!.Value, doc.Attribute("ImpSaldoAnt")!.Value,
            doc.Attribute("ImpPagado")!.Value, doc.Attribute("ImpSaldoInsoluto")!.Value));
        // Impuestos proporcionales: la mitad del total paga la mitad de cada base.
        var totals = XDocument.Parse(receipts[0].Xml!).Root!.Descendants(P + "Totales").Single();
        Assert.Equal(("308.00", "50.00", "8.00", "250.00"), (totals.Attribute("MontoTotalPagos")!.Value,
            totals.Attribute("TotalTrasladosBaseIVA16")!.Value, totals.Attribute("TotalTrasladosImpuestoIVA16")!.Value, totals.Attribute("TotalTrasladosBaseIVA0")!.Value));
        Assert.Equal(616m, await db.JournalLines.Where(l => l.AccountId == db.Accounts.Single(a => a.Code == Contabilidad.Bancos).Id).SumAsync(l => l.Debit));
    }

    [Fact]
    public async Task Credit_note_issues_egreso_cfdi_related_01_and_reduces_balance()
    {
        var inv = await Draft(new Customer { Name = "Cliente Uno", Rfc = "CUN120315AB1", TaxRegime = "601", ZipCode = "76000", CreditDays = 30 });
        Assert.Null(await Facturacion.EmitAsync(db, inv.Id));
        Assert.NotNull((await Facturacion.CreditAsync(db, inv.Id, 700, "descuento", "Pronto pago")).Error); // más que el saldo
        Assert.Null((await Facturacion.CreditAsync(db, inv.Id, 308, "descuento", "Pronto pago")).Error);

        var n = await db.CreditNotes.SingleAsync();
        Assert.Equal((300m, 8m, 308m, 308m), (n.Subtotal, n.Tax, n.Total, inv.Credited)); // 16%: 58 = 50 + 8; 0%: 250
        var x = XDocument.Parse(n.Xml!).Root!;
        Assert.Equal(("E", "308.00", "G02", "01", inv.Uuid.ToString()!.ToUpperInvariant()),
            (x.Attribute("TipoDeComprobante")!.Value, x.Attribute("Total")!.Value, x.Element(C + "Receptor")!.Attribute("UsoCFDI")!.Value,
             x.Element(C + "CfdiRelacionados")!.Attribute("TipoRelacion")!.Value, x.Element(C + "CfdiRelacionados")!.Element(C + "CfdiRelacionado")!.Attribute("UUID")!.Value));
        Assert.NotNull(await Facturacion.CancelAsync(db, inv.Id, "02"));                 // con nota ya no se cancela

        Assert.Null(await Facturacion.PayAsync(db, inv.Id));                              // cobra el resto
        Assert.Equal("pagada", inv.Status);
        var doc = XDocument.Parse((await db.PaymentReceipts.SingleAsync()).Xml!).Root!.Descendants(P + "DoctoRelacionado").Single();
        Assert.Equal("308.00", doc.Attribute("ImpSaldoAnt")!.Value);
    }

    [Fact]
    public async Task Substitution_cancels_original_with_reason_01_and_relates_the_new_cfdi()
    {
        var inv = await Draft(new Customer { Name = "Cliente Uno", Rfc = "CUN120315AB1", TaxRegime = "601", ZipCode = "76000" });
        Assert.Null(await Facturacion.EmitAsync(db, inv.Id));
        var productId = inv.Lines[0].ProductId!.Value;
        Assert.Equal(7, await Inventario.Stock(db, productId));
        Assert.NotNull(await Facturacion.CancelAsync(db, inv.Id, "01")); // 01 solo por sustitución

        var (newId, error) = await Facturacion.SubstituteAsync(db, inv.Id);
        Assert.Null(error);
        Assert.NotNull(await Facturacion.CancelAsync(db, inv.Id, "02")); // hay un borrador que la sustituye
        Assert.Null(await Facturacion.EmitAsync(db, newId!.Value));

        var fresh = await db.Invoices.SingleAsync(i => i.Id == newId);
        Assert.Equal(("cancelada", "01", fresh.Uuid), (inv.Status, inv.CancelReason, inv.SubstitutedBy));
        var rel = XDocument.Parse(fresh.Xml!).Root!.Element(C + "CfdiRelacionados")!;
        Assert.Equal(("04", inv.Uuid.ToString()!.ToUpperInvariant()), (rel.Attribute("TipoRelacion")!.Value, rel.Element(C + "CfdiRelacionado")!.Attribute("UUID")!.Value));
        Assert.Equal(7, await Inventario.Stock(db, productId)); // regresó y volvió a salir
    }

    [Fact]
    public void Qr_points_to_sat_verification_with_last_eight_seal_chars()
    {
        var url = Cfdi.QrUrl("EKU9003173C9", "CUN120315AB1", 616m, Guid.Parse("fbe75fe1-b713-40ba-adf2-b2e45c5204f5"), "ABCDEFGHIJ12345678");
        Assert.Equal("https://verificacfdi.facturaelectronica.sat.gob.mx/default.aspx?id=FBE75FE1-B713-40BA-ADF2-B2E45C5204F5&re=EKU9003173C9&rr=CUN120315AB1&tt=616.00&fe=12345678", url);
        Assert.StartsWith("<svg", Cfdi.QrSvg(url).TrimStart().Split('\n').Last(l => l.Contains("<svg")).Trim());
    }
}
