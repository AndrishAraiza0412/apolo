namespace Apolo.Api.Data;

/// created_at / updated_at / created_by los llena AppDb al guardar (DESIGN.md §9).
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// Todo lo que pertenece a una empresa. AppDb filtra lecturas y valida escrituras por la empresa del usuario.
public abstract class CompanyEntity : Entity
{
    public Guid CompanyId { get; set; }
}

public class Company : Entity
{
    public required string Name { get; set; }
    public string Currency { get; set; } = "MXN";
    // Datos del emisor del CFDI. Sin RFC la empresa factura sin timbrar (documento interno).
    public string? Rfc { get; set; }
    public string? LegalName { get; set; }
    public string? TaxRegime { get; set; }
    public string? ZipCode { get; set; }
    /// Prima de riesgo de trabajo del IMSS (fracción). Una empresa nueva cotiza con la prima media de su clase; clase I = 0.54355%.
    public decimal RiskPremium { get; set; } = 0.0054355m;
    /// Parámetros de nómina de la empresa (PayrollParams en JSON); vacío = valores legales vigentes.
    public string? PayrollJson { get; set; }
    /// Coeficiente de utilidad de la última declaración anual (persona moral) para el ISR provisional.
    public decimal ProfitCoefficient { get; set; }
    /// Último día del ejercicio cerrado: no se aceptan pólizas con fecha igual o anterior.
    public DateOnly? ClosedThrough { get; set; }
}

public class PeriodClosedException(DateOnly closed) : Exception($"El periodo está cerrado hasta el {closed:dd/MM/yyyy}.");

public class Role : CompanyEntity
{
    public required string Name { get; set; }
    public List<string> Modules { get; set; } = [];
}

public class User : CompanyEntity
{
    public required string Email { get; set; }
    public required string Name { get; set; }
    public string PasswordHash { get; set; } = "";
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public string? Theme { get; set; }
    public bool Active { get; set; } = true;
    /// Intentos fallidos seguidos; al quinto la cuenta se bloquea 15 minutos.
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
}

/// Bitácora de auditoría: una fila por alta/cambio/baja. CreatedAt = cuándo, CreatedBy = quién.
public class AuditEntry : CompanyEntity
{
    public required string Action { get; set; }
    public required string Entity { get; set; }
    public Guid EntityId { get; set; }
    public string? Changes { get; set; }
}

public class Product : CompanyEntity
{
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public string Unit { get; set; } = "pza";
    public string SatCode { get; set; } = "01010101";
    public string SatUnit { get; set; } = "H87";
    public decimal Cost { get; set; }
    public decimal Price { get; set; }
    public decimal MinStock { get; set; }
    public bool Active { get; set; } = true;
}

/// Inmutable. La existencia de un producto es la suma de Quantity (entradas +, salidas −), nunca un campo editable.
public class Customer : CompanyEntity
{
    public required string Name { get; set; }
    public string? Rfc { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int CreditDays { get; set; }
    public bool Active { get; set; } = true;
    // Datos del receptor del CFDI 4.0.
    public string? TaxRegime { get; set; }
    public string? ZipCode { get; set; }
    public string CfdiUse { get; set; } = "G03";
}

public class Opportunity : CompanyEntity
{
    public static readonly string[] Stages = ["prospecto", "propuesta", "negociacion", "ganada", "perdida"];

    public Guid CustomerId { get; set; }
    public required string Title { get; set; }
    public decimal Amount { get; set; }
    public string Stage { get; set; } = "prospecto";
    public DateOnly? ExpectedClose { get; set; }
    public Guid? OwnerId { get; set; }
}

/// Borrador → emitida → pagada, o cancelada. "Vencida" no se guarda: es emitida con DueDate pasada.
public class Invoice : CompanyEntity
{
    public int? Folio { get; set; }
    public Guid CustomerId { get; set; }
    public string Status { get; set; } = "borrador";
    public DateTime? IssuedAt { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public List<InvoiceLine> Lines { get; set; } = [];
    // CFDI: se llenan al timbrar.
    public string? PaymentMethod { get; set; }
    public string? PaymentForm { get; set; }
    public Guid? Uuid { get; set; }
    public DateTime? StampedAt { get; set; }
    public string? Xml { get; set; }
    public string? CancelReason { get; set; }
    // Sustitución (motivo 01): el borrador nuevo apunta a la factura que reemplaza; la cancelada guarda el UUID que la sustituye.
    public Guid? ReplacesId { get; set; }
    public Guid? SubstitutedBy { get; set; }
    /// Suma de abonos. Saldo = Total − Paid; llega a "pagada" cuando el saldo es cero.
    public decimal Paid { get; set; }
    /// Suma de notas de crédito. Saldo = Total − Paid − Credited.
    public decimal Credited { get; set; }
    /// Retenciones del cliente persona moral (ISR e IVA). Total = Subtotal + Tax − retenciones.
    public decimal IsrWithheld { get; set; }
    public decimal IvaWithheld { get; set; }
}

/// Nota de crédito (CFDI de egreso) por devolución o descuento sobre una factura emitida; reduce su saldo.
public class CreditNote : CompanyEntity
{
    public static readonly string[] Kinds = ["devolucion", "descuento"];

    public Guid InvoiceId { get; set; }
    public int Folio { get; set; }
    public DateTime IssuedAt { get; set; }
    public required string Kind { get; set; }
    public required string Reason { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public Guid? Uuid { get; set; }
    public string? Xml { get; set; }
}

/// Abono a una factura. En una factura PPD timbrada lleva su recibo electrónico de pago (CFDI tipo P): Folio, Uuid y Xml.
public class PaymentReceipt : CompanyEntity
{
    public Guid InvoiceId { get; set; }
    public int Installment { get; set; }
    public DateTime PaidAt { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceBefore { get; set; }
    public required string PaymentForm { get; set; }
    public int? Folio { get; set; }
    public Guid? Uuid { get; set; }
    public string? Xml { get; set; }
}

public class InvoiceLine : CompanyEntity
{
    public Guid InvoiceId { get; set; }
    public int Position { get; set; }
    public Guid? ProductId { get; set; }
    public required string Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
}

/// Folios consecutivos por empresa y tipo de documento (DESIGN.md §9). Se incrementa solo en servidor.
public class FolioSequence : CompanyEntity
{
    public required string Kind { get; set; }
    public int LastNumber { get; set; }
}

public class InventoryMovement : CompanyEntity
{
    public Guid ProductId { get; set; }
    public required string Kind { get; set; }
    public decimal Quantity { get; set; }
    public string? Note { get; set; }
}

public class Supplier : CompanyEntity
{
    public required string Name { get; set; }
    public string? Rfc { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int CreditDays { get; set; }
    public bool Active { get; set; } = true;
}

/// Orden de compra: borrador → enviada (folio) → recibida (entra al inventario) → pagada. Se cancela antes de recibirse.
public class PurchaseOrder : CompanyEntity
{
    public int? Folio { get; set; }
    public Guid SupplierId { get; set; }
    public string Status { get; set; } = "borrador";
    public DateTime? SentAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    /// Suma de abonos al proveedor; llega a "pagada" cuando cubre el total.
    public decimal Paid { get; set; }
    public string? Notes { get; set; }
    public List<PurchaseLine> Lines { get; set; } = [];
}

public class PurchaseLine : CompanyEntity
{
    public Guid PurchaseOrderId { get; set; }
    public int Position { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TaxRate { get; set; }
}

/// Borrador propuesto por Apolo IA. El agente nunca escribe directo: nada existe hasta que una persona lo aprueba (DESIGN.md §4).
public class AgentAction : CompanyEntity
{
    public static readonly string[] Kinds = ["factura", "orden_compra"];

    public required string Kind { get; set; }
    public required string Summary { get; set; }
    public required string Payload { get; set; }
    public string Status { get; set; } = "pendiente";
    public Guid? ResultId { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Error { get; set; }
}

public class Project : CompanyEntity
{
    public static readonly string[] Statuses = ["activo", "pausado", "cerrado"];

    public required string Name { get; set; }
    public Guid? CustomerId { get; set; }
    public string Status { get; set; } = "activo";
    public decimal Budget { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string? Notes { get; set; }
}

public class ProjectTask : CompanyEntity
{
    public Guid ProjectId { get; set; }
    public required string Title { get; set; }
    public bool Done { get; set; }
    public DateOnly? DueDate { get; set; }
    public Guid? AssigneeId { get; set; }
}

/// Cuenta del catálogo contable. La naturaleza (deudora/acreedora) sale del tipo.
public class Account : CompanyEntity
{
    public static readonly string[] Types = ["activo", "pasivo", "capital", "ingreso", "gasto"];

    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public bool Active { get; set; } = true;
    /// Código agrupador del SAT para la contabilidad electrónica (p. ej. 102.01).
    public string? SatCode { get; set; }
}

/// Póliza. Inmutable: una corrección es otra póliza que la revierte (ReversalOf).
public class JournalEntry : CompanyEntity
{
    public static readonly string[] Kinds = ["diario", "ingreso", "egreso"];

    public int Folio { get; set; }
    public DateOnly Date { get; set; }
    public required string Kind { get; set; }
    public required string Concept { get; set; }
    public string Source { get; set; } = "manual";
    public Guid? SourceId { get; set; }
    public Guid? ReversalOf { get; set; }
    public List<JournalLine> Lines { get; set; } = [];
}

public class JournalLine : CompanyEntity
{
    public Guid JournalEntryId { get; set; }
    public Guid AccountId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Description { get; set; }
}

public class Employee : CompanyEntity
{
    public required string Name { get; set; }
    public string? Rfc { get; set; }
    public string? Position { get; set; }
    public string? Department { get; set; }
    public DateOnly? HireDate { get; set; }
    /// Fecha de baja: se fija al pagar su finiquito. Con ella el empleado sigue contando para la PTU del año que trabajó.
    public DateOnly? TerminationDate { get; set; }
    public decimal DailySalary { get; set; }
    public bool Active { get; set; } = true;
}

/// Nómina: sueldo diario × días más horas extra, menos ISR (con subsidio), cuotas obreras IMSS y otras deducciones (Nomina.cs).
/// Kind: ordinaria, aguinaldo o ptu. Las extraordinarias no causan cuotas IMSS (el aguinaldo ya integra el SBC; la PTU no lo integra).
public class PayrollRun : CompanyEntity
{
    public static readonly string[] Kinds = ["ordinaria", "aguinaldo", "ptu", "finiquito"];

    public int? Folio { get; set; }
    public string Kind { get; set; } = "ordinaria";
    /// Finiquito: "renuncia" (o terminación voluntaria) o "despido" (liquidación por despido injustificado).
    public string? Cause { get; set; }
    /// Cuotas patronales IMSS, RCV e INFONAVIT del periodo: costo de la empresa, no se descuentan al empleado.
    public decimal EmployerCost { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Status { get; set; } = "borrador";
    public DateTime? PaidAt { get; set; }
    public decimal Gross { get; set; }
    public decimal Deductions { get; set; }
    public decimal Net { get; set; }
    public List<PayrollLine> Lines { get; set; } = [];
}

public class PayrollLine : CompanyEntity
{
    public Guid PayrollRunId { get; set; }
    public Guid EmployeeId { get; set; }
    public decimal Days { get; set; }
    public decimal DailySalary { get; set; }
    public decimal Sbc { get; set; }
    public decimal DoubleHours { get; set; }
    public decimal TripleHours { get; set; }
    /// Ordinaria: días de vacaciones gozados (pagan prima vacacional). Finiquito: días de vacaciones pendientes de años anteriores.
    public decimal VacationDays { get; set; }
    public decimal VacationPremium { get; set; }
    /// Faltas e incapacidades: no se pagan ni cotizan.
    public decimal Absences { get; set; }
    public decimal SickDays { get; set; }
    /// Diferencia del cálculo anual: positiva se retiene, negativa se devuelve.
    public decimal IsrAdjustment { get; set; }
    /// Finiquito: conceptos con importe y exento (Nomina.Concept en JSON).
    public string? Concepts { get; set; }
    public decimal Overtime { get; set; }
    /// Parte exenta de ISR: horas extra, aguinaldo o PTU.
    public decimal Exempt { get; set; }
    public decimal Employer { get; set; }
    public decimal Gross { get; set; }
    public decimal Isr { get; set; }
    public decimal Subsidy { get; set; }
    public decimal Imss { get; set; }
    public decimal Other { get; set; }
    public decimal Deductions { get; set; }
    public decimal Net { get; set; }
}

/// Tarea en segundo plano (DESIGN.md §4): el avance se guarda aquí y la interfaz lo consulta.
public class BackgroundJob : CompanyEntity
{
    public static readonly string[] Kinds = ["reporte", "conciliacion"];

    public required string Kind { get; set; }
    public required string Title { get; set; }
    public required string Params { get; set; }
    public string Status { get; set; } = "pendiente";
    public int Progress { get; set; }
    public string? Message { get; set; }
    public string? Result { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// Movimiento del estado de cuenta bancario importado. Amount: depósito +, retiro −.
public class BankTransaction : CompanyEntity
{
    public DateOnly Date { get; set; }
    public required string Description { get; set; }
    public string? Reference { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "pendiente";
    public string? MatchKind { get; set; }
    public Guid? MatchId { get; set; }
}
