using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public record JobRequest(string Kind, string? Report, DateOnly? From, DateOnly? To);

/// Tareas en segundo plano con avance visible (DESIGN.md §4). El estado vive en la base; la cola en memoria solo despierta al worker.
// ponytail: un worker en proceso; si hay varias instancias de la API, mover a Hangfire o a una cola externa.
public static class Tareas
{
    public static readonly Channel<Guid> Queue = Channel.CreateUnbounded<Guid>();
    public record Check(string Name, bool Ok, string Detail);

    public static string? Module(string kind) => kind switch { "conciliacion" => "contabilidad", "reporte" => "reportes", _ => null };

    public static async Task<(BackgroundJob? Job, string? Error)> EnqueueAsync(AppDb db, ClaimsPrincipal user, JobRequest req)
    {
        if (Module(req.Kind) is not { } module || !user.HasClaim(Modules.ClaimType, module)) return (null, "Tu rol no puede ejecutar esa tarea.");
        if (req.Kind == "reporte" && !Reportes.Available(user).Any(r => r.Code == req.Report)) return (null, "Reporte no disponible para tu rol.");
        if (req.From > req.To) return (null, "El periodo es inválido.");
        var title = req.Kind == "conciliacion" ? "Conciliación contable"
            : $"Reporte: {Reportes.Available(user).First(r => r.Code == req.Report).Name}";
        var job = new BackgroundJob { Kind = req.Kind, Title = title, Params = JsonSerializer.Serialize(req) };
        db.BackgroundJobs.Add(job);
        await db.SaveChangesAsync();
        await Queue.Writer.WriteAsync(job.Id);
        return (job, null);
    }

    /// Siete revisiones de consistencia entre módulos y libros. Cada una guarda su avance.
    public static async Task<List<Check>> ReconcileAsync(AppDb db, Func<int, string, Task> progress)
    {
        var checks = new List<Check>();
        async Task Step(string name, Func<Task<(bool, string)>> run)
        {
            await progress(checks.Count * 100 / 7, name);
            var (ok, detail) = await run();
            checks.Add(new(name, ok, detail));
        }
        decimal Bal(List<Contabilidad.AccountBalance> b, string code) => b.FirstOrDefault(a => a.Code == code)?.Closing ?? 0;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var balances = await Contabilidad.BalancesAsync(db, new DateOnly(2000, 1, 1), today.AddYears(1));

        await Step("Pólizas cuadradas", async () =>
        {
            var bad = await db.JournalEntries.Where(e => e.Lines.Sum(l => l.Debit) != e.Lines.Sum(l => l.Credit)).CountAsync();
            var total = await db.JournalEntries.CountAsync();
            return (bad == 0, bad == 0 ? $"{total} pólizas, todas con sumas iguales." : $"{bad} de {total} pólizas descuadradas.");
        });
        await Step("Existencias no negativas", async () =>
        {
            var neg = await db.Products.Where(p => db.InventoryMovements.Where(m => m.ProductId == p.Id).Sum(m => m.Quantity) < 0).Select(p => p.Sku).ToListAsync();
            return (neg.Count == 0, neg.Count == 0 ? "Ningún producto con existencia negativa." : $"Negativos: {string.Join(", ", neg)}.");
        });
        await Step("Totales de facturas", async () =>
        {
            var invoices = await db.Invoices.Include(i => i.Lines).Where(i => i.Status != "borrador").ToListAsync();
            var bad = invoices.Where(i => Facturacion.Totals(i.Lines) != (i.Subtotal, i.Tax, i.Total + i.IsrWithheld + i.IvaWithheld)).Select(i => Facturacion.FolioText(i.Folio)).ToList();
            return (bad.Count == 0, bad.Count == 0 ? $"{invoices.Count} facturas coinciden con sus partidas." : $"No coinciden: {string.Join(", ", bad)}.");
        });
        await Step("Totales de órdenes de compra", async () =>
        {
            var orders = await db.PurchaseOrders.Include(o => o.Lines).Where(o => o.Status != "borrador").ToListAsync();
            var bad = orders.Where(o => Facturacion.Totals(o.Lines.Select(l => (l.Quantity, l.UnitCost, l.TaxRate))) != (o.Subtotal, o.Tax, o.Total))
                .Select(o => Compras.FolioText(o.Folio)).ToList();
            return (bad.Count == 0, bad.Count == 0 ? $"{orders.Count} órdenes coinciden con sus partidas." : $"No coinciden: {string.Join(", ", bad)}.");
        });
        await Step("Clientes contra cartera", async () =>
        {
            var open = await db.Invoices.Where(i => i.Status == "emitida").SumAsync(i => i.Total - i.Paid - i.Credited);
            var diff = Bal(balances, Contabilidad.Clientes) - open;
            return (diff == 0, $"Cuenta 105: {Bal(balances, Contabilidad.Clientes):C}; facturas por cobrar: {open:C}." + (diff == 0 ? "" : $" Diferencia {diff:C}."));
        });
        await Step("Proveedores contra órdenes por pagar", async () =>
        {
            var open = await db.PurchaseOrders.Where(o => o.Status == "recibida").SumAsync(o => o.Total - o.Paid);
            var diff = Bal(balances, Contabilidad.Proveedores) - open;
            return (diff == 0, $"Cuenta 201: {Bal(balances, Contabilidad.Proveedores):C}; órdenes recibidas sin pagar: {open:C}." + (diff == 0 ? "" : $" Diferencia {diff:C}."));
        });
        await Step("Inventarios contra valuación", async () =>
        {
            var value = Math.Round(await db.Products.SumAsync(p => db.InventoryMovements.Where(m => m.ProductId == p.Id).Sum(m => m.Quantity) * p.Cost), 2);
            var diff = Bal(balances, Contabilidad.Inventarios) - value;
            // Informativa: el costo del producto se actualiza con cada compra, la póliza conserva el histórico.
            return (Math.Abs(diff) <= Math.Max(1, value * 0.02m), $"Cuenta 115: {Bal(balances, Contabilidad.Inventarios):C}; existencias al último costo: {value:C}. " + (diff == 0 ? "" : $"Diferencia {diff:C} por cambios de costo."));
        });
        await progress(100, "Terminada");
        return checks;
    }

    static async Task RunAsync(IServiceProvider services, Guid id, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDb>>();
        BackgroundJob? job;
        await using (var root = new AppDb(options))
            job = await root.BackgroundJobs.IgnoreQueryFilters().SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null || job.Status is "terminada" or "fallida") return;

        // La tarea corre con la empresa y los permisos de quien la pidió.
        await using var db = new AppDb(options, new CurrentUser { CompanyId = job.CompanyId, UserId = job.CreatedBy });
        var owner = await db.Users.Include(u => u.Role).SingleAsync(u => u.Id == job.CreatedBy, ct);
        var user = new ClaimsPrincipal(new ClaimsIdentity(owner.Role.Modules.Select(m => new Claim(Modules.ClaimType, m)), "job"));
        var tracked = await db.BackgroundJobs.SingleAsync(j => j.Id == id, ct);

        async Task Progress(int pct, string message)
        {
            tracked.Progress = pct;
            tracked.Message = message;
            await db.SaveChangesAsync(ct);
        }

        try
        {
            tracked.Status = "ejecutando";
            tracked.StartedAt = DateTime.UtcNow;
            await Progress(0, "Iniciando");
            var req = JsonSerializer.Deserialize<JobRequest>(tracked.Params)!;
            object result = req.Kind == "conciliacion"
                ? await ReconcileAsync(db, Progress)
                : await Reportes.RunAsync(db, user, req.Report!, req.From, req.To) ?? throw new InvalidOperationException("Reporte no disponible.");
            tracked.Result = JsonSerializer.Serialize(result, JsonSerializerOptions.Web);
            tracked.Status = "terminada";
            tracked.Progress = 100;
            tracked.Message = req.Kind == "conciliacion" && result is List<Check> checks
                ? (checks.All(c => c.Ok) ? "Todo cuadra." : $"{checks.Count(c => !c.Ok)} revisiones con diferencias.")
                : "Listo.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            tracked.Status = "fallida";
            tracked.Message = "La tarea falló. Revisa el registro del servidor.";
            services.GetRequiredService<ILoggerFactory>().CreateLogger("Tareas").LogError(ex, "Tarea {Id} falló", id);
        }
        tracked.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public class Worker(IServiceProvider services) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // Al arrancar retoma lo que quedó pendiente o a medias por un reinicio.
            using (var scope = services.CreateScope())
            await using (var db = new AppDb(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDb>>()))
                foreach (var id in await db.BackgroundJobs.IgnoreQueryFilters().Where(j => j.Status == "pendiente" || j.Status == "ejecutando").Select(j => j.Id).ToListAsync(ct))
                    await Queue.Writer.WriteAsync(id, ct);

            await foreach (var id in Queue.Reader.ReadAllAsync(ct))
            {
                // Una tarea rota no debe tumbar la API ni a las demás.
                try { await RunAsync(services, id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    services.GetRequiredService<ILoggerFactory>().CreateLogger("Tareas").LogError(ex, "Tarea {Id} no pudo ejecutarse", id);
                }
            }
        }
    }

    public static void MapTareas(this WebApplication app)
    {
        var g = app.MapGroup("/api/jobs").RequireAuthorization();

        g.MapGet("/", (AppDb db, CurrentUser me) => db.BackgroundJobs.Where(j => j.CreatedBy == me.UserId).OrderByDescending(j => j.CreatedAt).Take(20)
            .Select(j => new { j.Id, j.Kind, j.Title, j.Status, j.Progress, j.Message, j.CreatedAt, j.FinishedAt }).ToListAsync());

        g.MapGet("/{id:guid}", async (Guid id, AppDb db, CurrentUser me) =>
        {
            var j = await db.BackgroundJobs.SingleOrDefaultAsync(x => x.Id == id && x.CreatedBy == me.UserId);
            return j is null ? Results.NotFound() : Results.Ok(new
            {
                j.Id, j.Kind, j.Title, j.Status, j.Progress, j.Message, j.CreatedAt, j.FinishedAt,
                Result = j.Result is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(j.Result),
            });
        });

        g.MapPost("/", async (JobRequest req, AppDb db, ClaimsPrincipal user) =>
        {
            var (job, error) = await EnqueueAsync(db, user, req);
            return error is not null ? Results.Problem(error, statusCode: 403) : Results.Accepted($"/api/jobs/{job!.Id}", new { job.Id });
        });
    }
}
