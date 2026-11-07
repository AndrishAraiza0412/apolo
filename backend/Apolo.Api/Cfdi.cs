using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Apolo.Api.Data;
using QRCoder;

namespace Apolo.Api;

/// CFDI 4.0 de ingreso (DESIGN.md §12). Arma el XML del SAT y lo "timbra" en modo de pruebas.
// ponytail: timbrado simulado — UUID local y sellos "PRUEBAS". Para producción: firmar la cadena original con el CSD
// (.cer/.key) y enviar el XML al PAC elegido; solo cambia Stamp().
public static partial class Cfdi
{
    public const string PublicoGeneral = "XAXX010101000";

    public static readonly Dictionary<string, string> Regimes = new()
    {
        ["601"] = "General de Ley Personas Morales", ["603"] = "Personas Morales con Fines no Lucrativos",
        ["605"] = "Sueldos y Salarios", ["606"] = "Arrendamiento", ["612"] = "Personas Físicas con Actividades Empresariales y Profesionales",
        ["616"] = "Sin obligaciones fiscales", ["621"] = "Incorporación Fiscal", ["626"] = "Régimen Simplificado de Confianza",
    };
    public static readonly Dictionary<string, string> Uses = new()
    {
        ["G01"] = "Adquisición de mercancías", ["G03"] = "Gastos en general", ["I04"] = "Equipo de cómputo y accesorios",
        ["S01"] = "Sin efectos fiscales",
    };
    public static readonly Dictionary<string, string> CancelReasons = new()
    {
        ["01"] = "Comprobante emitido con errores con relación (sustitución)",
        ["02"] = "Comprobante emitido con errores sin relación",
        ["03"] = "No se llevó a cabo la operación",
        ["04"] = "Operación nominativa relacionada en una factura global",
    };

    [GeneratedRegex(@"^[A-ZÑ&]{3,4}\d{6}[A-Z0-9]{3}$")] private static partial Regex RfcPattern();
    [GeneratedRegex(@"^\d{5}$")] private static partial Regex ZipPattern();
    [GeneratedRegex(@"^\d{8}$")] private static partial Regex SatCodePattern();

    public static bool ValidRfc(string? rfc) => rfc is not null && RfcPattern().IsMatch(rfc);
    public static bool ValidZip(string? zip) => zip is not null && ZipPattern().IsMatch(zip);
    public static bool ValidSatCode(string? code) => code is not null && SatCodePattern().IsMatch(code);

    /// La empresa factura electrónicamente cuando tiene RFC.
    public static bool Enabled(Company c) => !string.IsNullOrWhiteSpace(c.Rfc);

    /// Lo que el SAT rechazaría. Vacío = se puede timbrar.
    public static List<string> Validate(Company c, Customer r)
    {
        var e = new List<string>();
        if (!ValidRfc(c.Rfc) || string.IsNullOrWhiteSpace(c.LegalName) || !Regimes.ContainsKey(c.TaxRegime ?? "") || !ValidZip(c.ZipCode))
            e.Add("Faltan datos fiscales de la empresa (RFC, razón social, régimen y código postal) en Configuración.");
        if (r.Rfc == PublicoGeneral) return e;
        if (!ValidRfc(r.Rfc)) e.Add($"El cliente {r.Name} no tiene un RFC válido.");
        if (!Regimes.ContainsKey(r.TaxRegime ?? "")) e.Add($"El cliente {r.Name} no tiene régimen fiscal.");
        if (!ValidZip(r.ZipCode)) e.Add($"El cliente {r.Name} no tiene código postal fiscal.");
        return e;
    }

    static string Money(decimal x) => x.ToString("0.00", CultureInfo.InvariantCulture);
    static string Qty(decimal x) => x.ToString("0.######", CultureInfo.InvariantCulture);
    static string Rate(decimal x) => x.ToString("0.000000", CultureInfo.InvariantCulture);

    static readonly XNamespace C = "http://www.sat.gob.mx/cfd/4", Tfd = "http://www.sat.gob.mx/TimbreFiscalDigital",
        Xsi = "http://www.w3.org/2001/XMLSchema-instance", Pago = "http://www.sat.gob.mx/Pagos20";

    static decimal Round(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    /// Base e IVA por tasa, redondeados por partida como en Facturacion.Totals.
    static List<(decimal Rate, decimal Base, decimal Tax)> Taxes(Invoice inv) => inv.Lines
        .Select(l => (l.TaxRate, Amount: Round(l.Quantity * l.UnitPrice)))
        .GroupBy(x => x.TaxRate).OrderByDescending(g => g.Key)
        .Select(g => (g.Key, g.Sum(x => x.Amount), g.Sum(x => Round(x.Amount * g.Key)))).ToList();

    /// Retenciones (LISR 106 y 116, LIVA 1-A II): persona física que presta servicios o arrienda (partidas sin producto) a una persona moral.
    /// ISR 10% (honorarios 612, arrendamiento 606) o 1.25% (RESICO 626); IVA dos terceras partes del 16%.
    public const decimal IvaWithholdingRate = 0.106667m;

    public static List<(string Tax, decimal Base, decimal Rate, decimal Amount)> Withholdings(Company? c, Customer r, IEnumerable<InvoiceLine> lines)
    {
        var result = new List<(string, decimal, decimal, decimal)>();
        if (c?.Rfc is not { Length: 13 } || r.Rfc is not { Length: 12 }) return result;
        var services = lines.Where(l => l.ProductId is null).Select(l => (l.TaxRate, Amount: Round(l.Quantity * l.UnitPrice))).ToList();
        var isrRate = c.TaxRegime switch { "626" => 0.0125m, "612" or "606" => 0.10m, _ => 0m };
        if (isrRate > 0 && services.Sum(x => x.Amount) > 0)
            result.Add(("001", services.Sum(x => x.Amount), isrRate, services.Sum(x => Round(x.Amount * isrRate))));
        var taxed = services.Where(x => x.TaxRate == 0.16m).ToList();
        if (taxed.Sum(x => x.Amount) > 0)
            result.Add(("002", taxed.Sum(x => x.Amount), IvaWithholdingRate, taxed.Sum(x => Round(x.Amount * IvaWithholdingRate))));
        return result;
    }

    static XElement Retencion(string prefix, (string Tax, decimal Base, decimal Rate, decimal Amount) w, string suffix = "") => new(prefix == "c" ? C + "Retencion" : Pago + $"Retencion{suffix}",
        new XAttribute($"Base{suffix}", Money(w.Base)), new XAttribute($"Impuesto{suffix}", w.Tax), new XAttribute($"TipoFactor{suffix}", "Tasa"),
        new XAttribute($"TasaOCuota{suffix}", Rate(w.Rate)), new XAttribute($"Importe{suffix}", Money(w.Amount)));

    static XElement Emisor(Company c) =>
        new(C + "Emisor", new XAttribute("Rfc", c.Rfc!), new XAttribute("Nombre", c.LegalName!.ToUpperInvariant()), new XAttribute("RegimenFiscal", c.TaxRegime!));

    static XElement Receptor(Company c, Customer r, string use)
    {
        var general = r.Rfc == PublicoGeneral;
        return new(C + "Receptor", new XAttribute("Rfc", r.Rfc!),
            new XAttribute("Nombre", general ? "PUBLICO EN GENERAL" : r.Name.ToUpperInvariant()),
            new XAttribute("DomicilioFiscalReceptor", general ? c.ZipCode! : r.ZipCode!),
            new XAttribute("RegimenFiscalReceptor", general ? "616" : r.TaxRegime!),
            new XAttribute("UsoCFDI", general && use != "CP01" ? "S01" : use));
    }

    /// URL de verificación del SAT que va en el QR de la representación impresa (Anexo 20).
    public static string QrUrl(string emitter, string receiver, decimal total, Guid uuid, string sello) =>
        $"https://verificacfdi.facturaelectronica.sat.gob.mx/default.aspx?id={uuid.ToString().ToUpperInvariant()}&re={emitter}&rr={receiver}" +
        $"&tt={total.ToString("0.00####", CultureInfo.InvariantCulture)}&fe={sello[Math.Max(0, sello.Length - 8)..]}";

    public static string QrSvg(string text)
    {
        using var gen = new QRCodeGenerator();
        return new SvgQRCode(gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M)).GetGraphic(4);
    }

    /// Comprobante de ingreso con IVA trasladado por partida. Importe e IVA ya vienen redondeados como en Facturacion.Totals.
    public static XDocument Build(Company c, Customer r, Invoice inv, IReadOnlyDictionary<Guid, Product> products, Guid? replaces = null)
    {
        var concepts = inv.Lines.OrderBy(l => l.Position).Select(l =>
        {
            var p = l.ProductId is Guid id ? products.GetValueOrDefault(id) : null;
            var amount = Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero);
            var tax = Math.Round(amount * l.TaxRate, 2, MidpointRounding.AwayFromZero);
            return (Line: l, Code: p?.SatCode ?? "84111506", Unit: p?.SatUnit ?? "E48", Amount: amount, Tax: tax);
        }).ToList();
        var withheld = Withholdings(c, r, inv.Lines);

        var comprobante = new XElement(C + "Comprobante",
            new XAttribute(XNamespace.Xmlns + "cfdi", C), new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
            new XAttribute(Xsi + "schemaLocation", "http://www.sat.gob.mx/cfd/4 http://www.sat.gob.mx/sitio_internet/cfd/4/cfdv40.xsd"),
            new XAttribute("Version", "4.0"), new XAttribute("Serie", "A"), new XAttribute("Folio", inv.Folio!),
            new XAttribute("Fecha", (inv.IssuedAt ?? DateTime.UtcNow).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss")),
            new XAttribute("Sello", ""), new XAttribute("NoCertificado", "00000000000000000000"), new XAttribute("Certificado", ""),
            new XAttribute("FormaPago", inv.PaymentForm!), new XAttribute("MetodoPago", inv.PaymentMethod!),
            new XAttribute("SubTotal", Money(inv.Subtotal)), new XAttribute("Moneda", c.Currency), new XAttribute("Total", Money(inv.Total)),
            new XAttribute("TipoDeComprobante", "I"), new XAttribute("Exportacion", "01"), new XAttribute("LugarExpedicion", c.ZipCode!),
            // Sustitución: el nuevo comprobante se relaciona con el que cancela (tipo 04).
            replaces is Guid old ? new XElement(C + "CfdiRelacionados", new XAttribute("TipoRelacion", "04"),
                new XElement(C + "CfdiRelacionado", new XAttribute("UUID", old.ToString().ToUpperInvariant()))) : null,
            Emisor(c), Receptor(c, r, r.CfdiUse),
            new XElement(C + "Conceptos", concepts.Select(x => new XElement(C + "Concepto",
                new XAttribute("ClaveProdServ", x.Code), new XAttribute("Cantidad", Qty(x.Line.Quantity)), new XAttribute("ClaveUnidad", x.Unit),
                new XAttribute("Descripcion", x.Line.Description), new XAttribute("ValorUnitario", Qty(x.Line.UnitPrice)),
                new XAttribute("Importe", Money(x.Amount)), new XAttribute("ObjetoImp", "02"),
                new XElement(C + "Impuestos", new XElement(C + "Traslados", new XElement(C + "Traslado",
                    new XAttribute("Base", Money(x.Amount)), new XAttribute("Impuesto", "002"), new XAttribute("TipoFactor", "Tasa"),
                    new XAttribute("TasaOCuota", Rate(x.Line.TaxRate)), new XAttribute("Importe", Money(x.Tax)))),
                    Withholdings(c, r, [x.Line]) is { Count: > 0 } lw ? new XElement(C + "Retenciones", lw.Select(w => Retencion("c", w))) : null)))),
            new XElement(C + "Impuestos",
                withheld.Count > 0 ? new XAttribute("TotalImpuestosRetenidos", Money(withheld.Sum(w => w.Amount))) : null,
                new XAttribute("TotalImpuestosTrasladados", Money(inv.Tax)),
                withheld.Count > 0 ? new XElement(C + "Retenciones", withheld.Select(w => new XElement(C + "Retencion",
                    new XAttribute("Impuesto", w.Tax), new XAttribute("Importe", Money(w.Amount))))) : null,
                new XElement(C + "Traslados", Taxes(inv).Select(t => new XElement(C + "Traslado",
                    new XAttribute("Base", Money(t.Base)), new XAttribute("Impuesto", "002"), new XAttribute("TipoFactor", "Tasa"),
                    new XAttribute("TasaOCuota", Rate(t.Rate)), new XAttribute("Importe", Money(t.Tax)))))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), comprobante);
    }

    /// Reparte un importe con IVA entre las tasas de la factura en proporción a su total; la suma cuadra exacta con el importe.
    public static List<(decimal Rate, decimal Base, decimal Tax)> Split(Invoice inv, decimal amount)
    {
        var taxes = Taxes(inv);
        var parts = new List<(decimal Rate, decimal Base, decimal Tax)>();
        var left = amount;
        foreach (var (t, i) in taxes.Select((t, i) => (t, i)))
        {
            var total = i == taxes.Count - 1 ? left : Round((t.Base + t.Tax) * amount / inv.Total);
            left -= total;
            var @base = Round(total / (1 + t.Rate));
            parts.Add((t.Rate, @base, total - @base));
        }
        return parts;
    }

    /// Nota de crédito (CFDI tipo E) relacionada con la factura (tipo 01), un concepto por tasa de IVA.
    // ponytail: forma de pago 17 (compensación) porque la nota se aplica contra el saldo; si se reembolsara se usaría la del reembolso.
    public static XDocument BuildCredit(Company c, Customer r, Invoice inv, CreditNote n)
    {
        var parts = Split(inv, n.Total);
        var description = n.Kind == "devolucion" ? "Devolución de mercancía" : "Descuento o bonificación";
        var comprobante = new XElement(C + "Comprobante",
            new XAttribute(XNamespace.Xmlns + "cfdi", C), new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
            new XAttribute(Xsi + "schemaLocation", "http://www.sat.gob.mx/cfd/4 http://www.sat.gob.mx/sitio_internet/cfd/4/cfdv40.xsd"),
            new XAttribute("Version", "4.0"), new XAttribute("Serie", "NC"), new XAttribute("Folio", n.Folio),
            new XAttribute("Fecha", n.IssuedAt.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss")),
            new XAttribute("Sello", ""), new XAttribute("NoCertificado", "00000000000000000000"), new XAttribute("Certificado", ""),
            new XAttribute("FormaPago", "17"), new XAttribute("MetodoPago", "PUE"),
            new XAttribute("SubTotal", Money(n.Subtotal)), new XAttribute("Moneda", c.Currency), new XAttribute("Total", Money(n.Total)),
            new XAttribute("TipoDeComprobante", "E"), new XAttribute("Exportacion", "01"), new XAttribute("LugarExpedicion", c.ZipCode!),
            new XElement(C + "CfdiRelacionados", new XAttribute("TipoRelacion", "01"),
                new XElement(C + "CfdiRelacionado", new XAttribute("UUID", inv.Uuid!.Value.ToString().ToUpperInvariant()))),
            Emisor(c), Receptor(c, r, "G02"),
            new XElement(C + "Conceptos", parts.Select(p => new XElement(C + "Concepto",
                new XAttribute("ClaveProdServ", "84111506"), new XAttribute("Cantidad", "1"), new XAttribute("ClaveUnidad", "ACT"),
                new XAttribute("Descripcion", $"{description}: {n.Reason}"), new XAttribute("ValorUnitario", Money(p.Base)),
                new XAttribute("Importe", Money(p.Base)), new XAttribute("ObjetoImp", "02"),
                new XElement(C + "Impuestos", new XElement(C + "Traslados", new XElement(C + "Traslado",
                    new XAttribute("Base", Money(p.Base)), new XAttribute("Impuesto", "002"), new XAttribute("TipoFactor", "Tasa"),
                    new XAttribute("TasaOCuota", Rate(p.Rate)), new XAttribute("Importe", Money(p.Tax)))))))),
            new XElement(C + "Impuestos", new XAttribute("TotalImpuestosTrasladados", Money(n.Tax)),
                new XElement(C + "Traslados", parts.Select(p => new XElement(C + "Traslado",
                    new XAttribute("Base", Money(p.Base)), new XAttribute("Impuesto", "002"), new XAttribute("TipoFactor", "Tasa"),
                    new XAttribute("TasaOCuota", Rate(p.Rate)), new XAttribute("Importe", Money(p.Tax)))))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), comprobante);
    }

    /// Recibo electrónico de pago (CFDI tipo P, complemento Pagos 2.0) de una parcialidad de una factura PPD.
    /// Los impuestos del pago son proporcionales a lo pagado (ImpPagado / Total).
    public static XDocument BuildPayment(Company c, Customer r, Invoice inv, int folio, DateTime paidAt, string form,
        decimal amount, decimal balanceBefore, int installment)
    {
        var factor = amount / inv.Total;
        var taxes = Taxes(inv).Select(t => (t.Rate, Base: Round(t.Base * factor), Tax: Round(t.Tax * factor))).ToList();
        string Key(decimal rate) => rate switch { 0.16m => "16", 0.08m => "8", _ => "0" };
        var totals = new XElement(Pago + "Totales");
        foreach (var t in taxes)
        {
            totals.Add(new XAttribute($"TotalTrasladosBaseIVA{Key(t.Rate)}", Money(t.Base)));
            totals.Add(new XAttribute($"TotalTrasladosImpuestoIVA{Key(t.Rate)}", Money(t.Tax)));
        }
        var withheld = Withholdings(c, r, inv.Lines).Select(w => (w.Tax, Base: Round(w.Base * factor), w.Rate, Amount: Round(w.Amount * factor))).ToList();
        foreach (var w in withheld) totals.Add(new XAttribute(w.Tax == "001" ? "TotalRetencionesISR" : "TotalRetencionesIVA", Money(w.Amount)));
        totals.Add(new XAttribute("MontoTotalPagos", Money(amount)));
        XElement Traslado(string suffix, (decimal Rate, decimal Base, decimal Tax) t) => new(Pago + $"Traslado{suffix}",
            new XAttribute($"Base{suffix}", Money(t.Base)), new XAttribute($"Impuesto{suffix}", "002"), new XAttribute($"TipoFactor{suffix}", "Tasa"),
            new XAttribute($"TasaOCuota{suffix}", Rate(t.Rate)), new XAttribute($"Importe{suffix}", Money(t.Tax)));

        var comprobante = new XElement(C + "Comprobante",
            new XAttribute(XNamespace.Xmlns + "cfdi", C), new XAttribute(XNamespace.Xmlns + "pago20", Pago), new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
            new XAttribute(Xsi + "schemaLocation", "http://www.sat.gob.mx/cfd/4 http://www.sat.gob.mx/sitio_internet/cfd/4/cfdv40.xsd " +
                "http://www.sat.gob.mx/Pagos20 http://www.sat.gob.mx/sitio_internet/cfd/Pagos/Pagos20.xsd"),
            new XAttribute("Version", "4.0"), new XAttribute("Serie", "P"), new XAttribute("Folio", folio),
            new XAttribute("Fecha", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")),
            new XAttribute("Sello", ""), new XAttribute("NoCertificado", "00000000000000000000"), new XAttribute("Certificado", ""),
            new XAttribute("SubTotal", "0"), new XAttribute("Moneda", "XXX"), new XAttribute("Total", "0"),
            new XAttribute("TipoDeComprobante", "P"), new XAttribute("Exportacion", "01"), new XAttribute("LugarExpedicion", c.ZipCode!),
            Emisor(c), Receptor(c, r, "CP01"),
            new XElement(C + "Conceptos", new XElement(C + "Concepto",
                new XAttribute("ClaveProdServ", "84111506"), new XAttribute("Cantidad", "1"), new XAttribute("ClaveUnidad", "ACT"),
                new XAttribute("Descripcion", "Pago"), new XAttribute("ValorUnitario", "0"), new XAttribute("Importe", "0"), new XAttribute("ObjetoImp", "01"))),
            new XElement(C + "Complemento", new XElement(Pago + "Pagos", new XAttribute("Version", "2.0"), totals,
                new XElement(Pago + "Pago",
                    new XAttribute("FechaPago", paidAt.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss")), new XAttribute("FormaDePagoP", form),
                    new XAttribute("MonedaP", "MXN"), new XAttribute("TipoCambioP", "1"), new XAttribute("Monto", Money(amount)),
                    new XElement(Pago + "DoctoRelacionado",
                        new XAttribute("IdDocumento", inv.Uuid!.Value.ToString().ToUpperInvariant()), new XAttribute("Serie", "A"), new XAttribute("Folio", inv.Folio!),
                        new XAttribute("MonedaDR", "MXN"), new XAttribute("EquivalenciaDR", "1"), new XAttribute("NumParcialidad", installment),
                        new XAttribute("ImpSaldoAnt", Money(balanceBefore)), new XAttribute("ImpPagado", Money(amount)), new XAttribute("ImpSaldoInsoluto", Money(balanceBefore - amount)),
                        new XAttribute("ObjetoImpDR", "02"),
                        new XElement(Pago + "ImpuestosDR",
                            withheld.Count > 0 ? new XElement(Pago + "RetencionesDR", withheld.Select(w => Retencion("p", w, "DR"))) : null,
                            new XElement(Pago + "TrasladosDR", taxes.Select(t => Traslado("DR", t))))),
                    new XElement(Pago + "ImpuestosP",
                        withheld.Count > 0 ? new XElement(Pago + "RetencionesP", withheld.Select(w => new XElement(Pago + "RetencionP",
                            new XAttribute("ImpuestoP", w.Tax), new XAttribute("ImporteP", Money(w.Amount))))) : null,
                        new XElement(Pago + "TrasladosP", taxes.Select(t => Traslado("P", t))))))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), comprobante);
    }

    public static string Text(XDocument xml) => $"{xml.Declaration}\n{xml.Root}";

    /// Agrega el timbre fiscal. En pruebas el UUID es local y los sellos dicen PRUEBAS.
    public static (Guid Uuid, DateTime At) Stamp(XDocument xml)
    {
        var uuid = Guid.NewGuid();
        var at = DateTime.UtcNow;
        var complemento = xml.Root!.Element(C + "Complemento");
        if (complemento is null) xml.Root.Add(complemento = new XElement(C + "Complemento"));
        complemento.Add(new XElement(Tfd + "TimbreFiscalDigital",
            new XAttribute(XNamespace.Xmlns + "tfd", Tfd), new XAttribute("Version", "1.1"), new XAttribute("UUID", uuid.ToString().ToUpperInvariant()),
            new XAttribute("FechaTimbrado", at.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss")), new XAttribute("RfcProvCertif", "AAA010101AAA"),
            new XAttribute("SelloCFD", "PRUEBAS"), new XAttribute("NoCertificadoSAT", "00000000000000000000"), new XAttribute("SelloSAT", "PRUEBAS")));
        return (uuid, at);
    }
}
