using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api.Data;

public class AppDb(DbContextOptions<AppDb> options, CurrentUser? user = null) : DbContext(options)
{
    static readonly HashSet<string> Secret = [nameof(User.PasswordHash)];

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<FolioSequence> FolioSequences => Set<FolioSequence>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseLine> PurchaseLines => Set<PurchaseLine>();
    public DbSet<AgentAction> AgentActions => Set<AgentAction>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<PaymentReceipt> PaymentReceipts => Set<PaymentReceipt>();
    public DbSet<CreditNote> CreditNotes => Set<CreditNote>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollLine> PayrollLines => Set<PayrollLine>();

    // Sin usuario (login, seed) los filtros no devuelven nada; esos caminos usan IgnoreQueryFilters a propósito.
    public Guid? CompanyId => user?.CompanyId;

    protected override void OnModelCreating(ModelBuilder b)
    {
        Tenant<Role>(b);
        Tenant<User>(b);
        Tenant<AuditEntry>(b);
        Tenant<Product>(b);
        Tenant<InventoryMovement>(b);

        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<AuditEntry>().Property(a => a.Changes).HasColumnType("jsonb");
        b.Entity<AuditEntry>().HasIndex(a => new { a.CompanyId, a.CreatedAt });

        b.Entity<Product>().HasIndex(p => new { p.CompanyId, p.Sku }).IsUnique();
        b.Entity<Product>().Property(p => p.Cost).HasPrecision(18, 2);
        b.Entity<Product>().Property(p => p.Price).HasPrecision(18, 2);
        b.Entity<Product>().Property(p => p.MinStock).HasPrecision(18, 4);
        b.Entity<InventoryMovement>().Property(m => m.Quantity).HasPrecision(18, 4);
        b.Entity<InventoryMovement>().HasOne<Product>().WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<InventoryMovement>().HasIndex(m => new { m.ProductId, m.CreatedAt });

        Tenant<Customer>(b);
        Tenant<Opportunity>(b);
        b.Entity<Opportunity>().Property(o => o.Amount).HasPrecision(18, 2);
        b.Entity<Opportunity>().HasOne<Customer>().WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Opportunity>().HasOne<User>().WithMany().HasForeignKey(o => o.OwnerId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Opportunity>().HasIndex(o => new { o.CompanyId, o.Stage });

        Tenant<Invoice>(b);
        Tenant<InvoiceLine>(b);
        Tenant<FolioSequence>(b);
        b.Entity<Invoice>().HasIndex(i => new { i.CompanyId, i.Folio }).IsUnique();
        b.Entity<Invoice>().HasIndex(i => new { i.CompanyId, i.Status });
        b.Entity<Invoice>().HasOne<Customer>().WithMany().HasForeignKey(i => i.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Invoice>().HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        foreach (var p in new[] { nameof(Invoice.Subtotal), nameof(Invoice.Tax), nameof(Invoice.Total), nameof(Invoice.Paid), nameof(Invoice.Credited), nameof(Invoice.IsrWithheld), nameof(Invoice.IvaWithheld) })
            b.Entity<Invoice>().Property(p).HasPrecision(18, 2);
        b.Entity<InvoiceLine>().HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<InvoiceLine>().Property(l => l.Quantity).HasPrecision(18, 4);
        b.Entity<InvoiceLine>().Property(l => l.UnitPrice).HasPrecision(18, 2);
        b.Entity<InvoiceLine>().Property(l => l.TaxRate).HasPrecision(5, 4);
        b.Entity<FolioSequence>().HasIndex(s => new { s.CompanyId, s.Kind }).IsUnique();

        Tenant<Supplier>(b);
        Tenant<PurchaseOrder>(b);
        Tenant<PurchaseLine>(b);
                b.Entity<PurchaseOrder>().Property(o => o.Paid).HasPrecision(18, 2);
b.Entity<PurchaseOrder>().HasIndex(o => new { o.CompanyId, o.Folio }).IsUnique();
        b.Entity<PurchaseOrder>().HasOne<Supplier>().WithMany().HasForeignKey(o => o.SupplierId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PurchaseOrder>().HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        foreach (var p in new[] { nameof(PurchaseOrder.Subtotal), nameof(PurchaseOrder.Tax), nameof(PurchaseOrder.Total) })
            b.Entity<PurchaseOrder>().Property(p).HasPrecision(18, 2);
        b.Entity<PurchaseLine>().HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PurchaseLine>().Property(l => l.Quantity).HasPrecision(18, 4);
        b.Entity<PurchaseLine>().Property(l => l.UnitCost).HasPrecision(18, 2);
        b.Entity<PurchaseLine>().Property(l => l.TaxRate).HasPrecision(5, 4);

        Tenant<AgentAction>(b);
        b.Entity<AgentAction>().Property(a => a.Payload).HasColumnType("jsonb");
        b.Entity<AgentAction>().HasIndex(a => new { a.CompanyId, a.Status });
        Tenant<BackgroundJob>(b);
        Tenant<BankTransaction>(b);
        Tenant<PaymentReceipt>(b);
        b.Entity<PaymentReceipt>().Property(r => r.Amount).HasPrecision(18, 2);
        b.Entity<PaymentReceipt>().Property(r => r.BalanceBefore).HasPrecision(18, 2);
        b.Entity<PaymentReceipt>().HasIndex(r => new { r.CompanyId, r.Folio }).IsUnique();
        b.Entity<PaymentReceipt>().HasOne<Invoice>().WithMany().HasForeignKey(r => r.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        Tenant<CreditNote>(b);
        foreach (var p in new[] { nameof(CreditNote.Subtotal), nameof(CreditNote.Tax), nameof(CreditNote.Total) })
            b.Entity<CreditNote>().Property(p).HasPrecision(18, 2);
        b.Entity<CreditNote>().HasIndex(n => new { n.CompanyId, n.Folio }).IsUnique();
        b.Entity<CreditNote>().HasOne<Invoice>().WithMany().HasForeignKey(n => n.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BankTransaction>().Property(t => t.Amount).HasPrecision(18, 2);
        b.Entity<BankTransaction>().HasIndex(t => new { t.CompanyId, t.Date, t.Amount, t.Description }).IsUnique();
        b.Entity<BackgroundJob>().Property(j => j.Params).HasColumnType("jsonb");
        b.Entity<BackgroundJob>().Property(j => j.Result).HasColumnType("jsonb");
        b.Entity<BackgroundJob>().HasIndex(j => new { j.CompanyId, j.CreatedBy, j.CreatedAt });

        Tenant<Project>(b);
        Tenant<ProjectTask>(b);
        b.Entity<Project>().Property(p => p.Budget).HasPrecision(18, 2);
        b.Entity<Project>().HasOne<Customer>().WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProjectTask>().HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectTask>().HasOne<User>().WithMany().HasForeignKey(t => t.AssigneeId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<ProjectTask>().HasIndex(t => t.ProjectId);

        Tenant<Account>(b);
        Tenant<JournalEntry>(b);
        Tenant<JournalLine>(b);
        b.Entity<Account>().HasIndex(a => new { a.CompanyId, a.Code }).IsUnique();
        b.Entity<JournalEntry>().HasIndex(e => new { e.CompanyId, e.Folio }).IsUnique();
        b.Entity<JournalEntry>().HasIndex(e => new { e.CompanyId, e.Date });
        b.Entity<JournalEntry>().HasIndex(e => new { e.Source, e.SourceId });
        b.Entity<JournalEntry>().HasMany(e => e.Lines).WithOne().HasForeignKey(l => l.JournalEntryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<JournalLine>().HasOne<Account>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<JournalLine>().Property(l => l.Debit).HasPrecision(18, 2);
        b.Entity<JournalLine>().Property(l => l.Credit).HasPrecision(18, 2);

        Tenant<Employee>(b);
        Tenant<PayrollRun>(b);
        Tenant<PayrollLine>(b);
        b.Entity<Employee>().Property(e => e.DailySalary).HasPrecision(18, 2);
        b.Entity<PayrollRun>().HasIndex(r => new { r.CompanyId, r.Folio }).IsUnique();
        b.Entity<PayrollRun>().HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.PayrollRunId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Company>().Property(c => c.RiskPremium).HasPrecision(9, 7);
        b.Entity<Company>().Property(c => c.ProfitCoefficient).HasPrecision(5, 4);
        b.Entity<PayrollRun>().Property(r => r.EmployerCost).HasPrecision(18, 2);
        foreach (var p in new[] { nameof(PayrollRun.Gross), nameof(PayrollRun.Deductions), nameof(PayrollRun.Net) })
        {
            b.Entity<PayrollRun>().Property(p).HasPrecision(18, 2);
            b.Entity<PayrollLine>().Property(p).HasPrecision(18, 2);
        }
        foreach (var p in new[] { nameof(PayrollLine.Sbc), nameof(PayrollLine.Isr), nameof(PayrollLine.Subsidy), nameof(PayrollLine.Imss), nameof(PayrollLine.Other),
                     nameof(PayrollLine.Overtime), nameof(PayrollLine.Exempt), nameof(PayrollLine.Employer),
                     nameof(PayrollLine.VacationPremium), nameof(PayrollLine.IsrAdjustment) })
            b.Entity<PayrollLine>().Property(p).HasPrecision(18, 2);
        b.Entity<PayrollLine>().Property(l => l.Days).HasPrecision(6, 2);
        b.Entity<PayrollLine>().Property(l => l.DoubleHours).HasPrecision(6, 2);
        b.Entity<PayrollLine>().Property(l => l.TripleHours).HasPrecision(6, 2);
        foreach (var p in new[] { nameof(PayrollLine.VacationDays), nameof(PayrollLine.Absences), nameof(PayrollLine.SickDays) })
            b.Entity<PayrollLine>().Property(p).HasPrecision(6, 2);
        b.Entity<PayrollLine>().Property(l => l.DailySalary).HasPrecision(18, 2);
        b.Entity<PayrollLine>().HasOne<Employee>().WithMany().HasForeignKey(l => l.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }

    void Tenant<T>(ModelBuilder b) where T : CompanyEntity
    {
        b.Entity<T>().HasQueryFilter(e => e.CompanyId == CompanyId);
        b.Entity<T>().HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Restrict);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    void Stamp()
    {
        // Periodo cerrado: ninguna póliza nueva con fecha igual o anterior al cierre, salvo la propia póliza de cierre.
        foreach (var j in ChangeTracker.Entries<JournalEntry>().Where(x => x.State == EntityState.Added && x.Entity.Source != "cierre").Select(x => x.Entity))
            if (Companies.Find(j.CompanyId == Guid.Empty ? CompanyId : j.CompanyId)?.ClosedThrough is DateOnly closed && j.Date <= closed)
                throw new PeriodClosedException(closed);

        var now = DateTime.UtcNow;
        var audit = new List<AuditEntry>();

        foreach (var e in ChangeTracker.Entries<Entity>().ToList())
        {
            if (e.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            if (e.Entity is CompanyEntity c)
            {
                if (c.CompanyId == Guid.Empty && CompanyId is Guid current) c.CompanyId = current;
                if (CompanyId is Guid mine && c.CompanyId != mine)
                    throw new InvalidOperationException($"Escritura de {e.Metadata.ClrType.Name} en otra empresa.");
            }

            if (e.State == EntityState.Added)
            {
                e.Entity.CreatedAt = now;
                e.Entity.CreatedBy ??= user?.UserId;
            }
            if (e.State != EntityState.Deleted) e.Entity.UpdatedAt = now;

            if (e.Entity is AuditEntry or BackgroundJob) continue; // el avance de una tarea no es un cambio de negocio
            var changes = e.State == EntityState.Deleted ? null : JsonSerializer.Serialize(
                e.Properties
                    .Where(p => e.State == EntityState.Added || p.IsModified)
                    .Where(p => p.Metadata.Name is not (nameof(Entity.CreatedAt) or nameof(Entity.UpdatedAt)))
                    .ToDictionary(p => p.Metadata.Name, p => Secret.Contains(p.Metadata.Name) ? "***" : p.CurrentValue));
            audit.Add(new AuditEntry
            {
                CompanyId = e.Entity is CompanyEntity ce ? ce.CompanyId : e.Entity.Id,
                Action = e.State switch { EntityState.Added => "alta", EntityState.Modified => "cambio", _ => "baja" },
                Entity = e.Metadata.ClrType.Name,
                EntityId = e.Entity.Id,
                Changes = changes,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = user?.UserId,
            });
        }

        AuditEntries.AddRange(audit);
    }
}
