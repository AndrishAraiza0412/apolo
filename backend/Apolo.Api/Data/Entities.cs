namespace Apolo.Api.Data;

/// created_at / updated_at / created_by are filled in by AppDb on save (DESIGN.md §9).
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// Everything that belongs to a company. AppDb filters reads and validates writes by the user's company.
public abstract class CompanyEntity : Entity
{
    public Guid CompanyId { get; set; }
}

public class Company : Entity
{
    public required string Name { get; set; }
    public string Currency { get; set; } = "MXN";
    // CFDI issuer data. Without an RFC the company invoices without stamping (internal document).
    public string? Rfc { get; set; }
    public string? LegalName { get; set; }
    public string? TaxRegime { get; set; }
    public string? ZipCode { get; set; }
    /// IMSS occupational risk premium (fraction). A new company contributes at the average premium for its class; class I = 0.54355%.
    public decimal RiskPremium { get; set; } = 0.0054355m;
    /// Company payroll parameters (PayrollParams as JSON); empty = current legal values.
    public string? PayrollJson { get; set; }
    /// Profit coefficient from the last annual return (legal entity) for provisional ISR.
    public decimal ProfitCoefficient { get; set; }
    /// Last day of the closed fiscal year: journal entries dated on or before it are not accepted.
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
    /// Consecutive failed attempts; on the fifth the account is locked for 15 minutes.
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
}

/// Audit log: one row per create/update/delete. CreatedAt = when, CreatedBy = who.
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

/// Immutable. A product's stock is the sum of Quantity (inbound +, outbound −), never an editable field.
public class Customer : CompanyEntity
{
    public required string Name { get; set; }
    public string? Rfc { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int CreditDays { get; set; }
    public bool Active { get; set; } = true;
    // CFDI 4.0 recipient data.
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

/// Draft → issued → paid, or cancelled. "Overdue" is not stored: it is issued with a past DueDate.
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
    // CFDI: filled in when stamping.
    public string? PaymentMethod { get; set; }
    public string? PaymentForm { get; set; }
    public Guid? Uuid { get; set; }
    public DateTime? StampedAt { get; set; }
    public string? Xml { get; set; }
    public string? CancelReason { get; set; }
    // Substitution (reason 01): the new draft points to the invoice it replaces; the cancelled one stores the UUID that replaces it.
    public Guid? ReplacesId { get; set; }
    public Guid? SubstitutedBy { get; set; }
    /// Sum of payments. Balance = Total − Paid; it becomes "paid" when the balance is zero.
    public decimal Paid { get; set; }
    /// Sum of credit notes. Balance = Total − Paid − Credited.
    public decimal Credited { get; set; }
    /// Retenciones del cliente persona moral (ISR e IVA). Total = Subtotal + Tax − retenciones.
    public decimal IsrWithheld { get; set; }
    public decimal IvaWithheld { get; set; }
}

/// Credit note (CFDI of type egreso, i.e. outgoing) for a return or discount on an issued invoice; it reduces its balance.
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

/// Payment toward an invoice. On a stamped PPD invoice it carries its electronic payment receipt (CFDI type P): Folio, Uuid and Xml.
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

/// Consecutive folios (document numbers) per company and document type (DESIGN.md §9). Incremented only on the server.
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

/// Purchase order: draft → sent (folio) → received (enters inventory) → paid. It can be cancelled before it is received.
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
    /// Sum of payments to the supplier; it becomes "paid" when it covers the total.
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

/// Draft proposed by Apolo IA. The agent never writes directly: nothing exists until a person approves it (DESIGN.md §4).
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

/// One query to Apolo IA: tokens, cost and tools used. It does not store the question, the answer or the tool arguments.
public class AgentUsage : CompanyEntity
{
    public required string RequestId { get; set; }
    public required string Model { get; set; }
    /// ok, refusal, max_rounds, limit or error (also if the query is interrupted by an exception).
    public string Outcome { get; set; } = "error";
    public int Rounds { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public long DurationMs { get; set; }
    public string Tools { get; set; } = "[]";
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

/// Account in the chart of accounts. The nature (debit/credit) comes from the type.
public class Account : CompanyEntity
{
    public static readonly string[] Types = ["activo", "pasivo", "capital", "ingreso", "gasto"];

    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public bool Active { get; set; } = true;
    /// SAT grouping code for electronic accounting (e.g. 102.01).
    public string? SatCode { get; set; }
}

/// Journal entry. Immutable: a correction is another entry that reverses it (ReversalOf).
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
    /// Termination date: set when the severance is paid. With it the employee still counts for the PTU of the year they worked.
    public DateOnly? TerminationDate { get; set; }
    public decimal DailySalary { get; set; }
    public bool Active { get; set; } = true;
}

/// Payroll: daily wage × days plus overtime, minus ISR (with subsidy), IMSS employee contributions and other deductions (Nomina.cs).
/// Kind: ordinary, aguinaldo (year-end bonus) or ptu (profit sharing). Extraordinary runs do not accrue IMSS contributions (the aguinaldo already includes the SBC; the PTU does not).
public class PayrollRun : CompanyEntity
{
    public static readonly string[] Kinds = ["ordinaria", "aguinaldo", "ptu", "finiquito"];

    public int? Folio { get; set; }
    public string Kind { get; set; } = "ordinaria";
    /// Severance (finiquito): "renuncia" (resignation or voluntary termination) or "despido" (settlement for unjustified dismissal).
    public string? Cause { get; set; }
    /// Employer IMSS, RCV and INFONAVIT contributions for the period: a cost to the company, not deducted from the employee.
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
    /// Ordinary: vacation days taken (they earn a vacation premium). Severance: pending vacation days from previous years.
    public decimal VacationDays { get; set; }
    public decimal VacationPremium { get; set; }
    /// Absences and sick leave: not paid and no contributions.
    public decimal Absences { get; set; }
    public decimal SickDays { get; set; }
    /// Difference from the annual calculation: positive is withheld, negative is refunded.
    public decimal IsrAdjustment { get; set; }
    /// Severance: concepts with amount and exempt portion (Nomina.Concept as JSON).
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

/// Background task (DESIGN.md §4): progress is stored here and the interface polls it.
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

/// Movement from the imported bank statement. Amount: deposit +, withdrawal −.
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
