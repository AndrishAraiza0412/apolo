using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Apolo.Api.Data;

/// Solo desarrollo: empresa demo con los cuatro roles del prototipo (DESIGN.md §3). Contraseña de todos: apolo1234
public static class Seed
{
    public static async Task RunAsync(AppDb db)
    {
        if (!await db.Companies.AnyAsync()) await DemoCompany(db);

        // Acceso rápido de desarrollo: admin / admin con rol Gerencia. Seed solo corre en Development.
        if (!await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == "admin"))
        {
            var gerencia = await db.Roles.IgnoreQueryFilters().FirstAsync(r => r.Name == "Gerencia");
            var admin = new User { CompanyId = gerencia.CompanyId, RoleId = gerencia.Id, Name = "Administrador", Email = "admin" };
            admin.PasswordHash = new PasswordHasher<User>().HashPassword(admin, "admin");
            db.Users.Add(admin);
            await db.SaveChangesAsync();
        }

        if (!await db.Products.IgnoreQueryFilters().AnyAsync()) await DemoInventory(db);
        if (!await db.Customers.IgnoreQueryFilters().AnyAsync()) await DemoVentas(db);
        if (!await db.Invoices.IgnoreQueryFilters().AnyAsync(i => i.Notes == "Demo")) await DemoInvoices(db);
        if (!await db.Suppliers.IgnoreQueryFilters().AnyAsync()) await DemoCompras(db);
        if (!await db.Projects.IgnoreQueryFilters().AnyAsync()) await DemoProyectos(db);

        // Contabilidad y nómina reutilizan la lógica de los módulos, que lee con el filtro de empresa: corren como la empresa demo.
        var companyId = (await db.Companies.FirstAsync(c => c.Name == "Apolo Demo")).Id;
        await using var tenant = new AppDb((DbContextOptions<AppDb>)db.GetService<IDbContextOptions>(), new CurrentUser { CompanyId = companyId });
        if (!await tenant.JournalEntries.AnyAsync()) await DemoContabilidad(tenant, companyId);
        if (!await tenant.Employees.AnyAsync()) await DemoRrhh(tenant);
        if (!await tenant.InventoryMovements.AnyAsync(m => m.Note == "Demo histórico")) await DemoDemanda(tenant);
        if ((await tenant.Companies.SingleAsync(c => c.Id == companyId)).Rfc is null) await DemoFiscal(tenant, companyId);
        if (await tenant.PayrollLines.AnyAsync(l => l.Sbc == 0) || await tenant.PayrollRuns.AnyAsync(r => r.Status == "borrador" && r.Kind == "ordinaria" && r.EmployerCost == 0)) await DemoNominaFiscal(tenant);
        if (!await tenant.BankTransactions.AnyAsync()) await DemoBancos(tenant);
    }

    /// Un estado de cuenta de ejemplo: dos depósitos que coinciden con facturas por cobrar, la última nómina pagada y una comisión.
    static async Task DemoBancos(AppDb db)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var open = await db.Invoices.Where(i => i.Status == "emitida").OrderBy(i => i.Folio).Take(2).ToListAsync();
        var lines = new List<string> { "Fecha,Descripción,Referencia,Cargo,Abono" };
        lines.AddRange(open.Select((i, k) => $"{today.AddDays(-k - 1):dd/MM/yyyy},SPEI RECIBIDO,{Facturacion.FolioText(i.Folio)},,\"{i.Total:#,0.00}\""));
        if (await db.PayrollRuns.Where(r => r.Status == "pagada").OrderByDescending(r => r.EndDate).FirstOrDefaultAsync() is { } run)
            lines.Add($"{run.EndDate:dd/MM/yyyy},PAGO NOMINA DISPERSION,,\"{run.Net:#,0.00}\",");
        lines.Add($"{today.AddDays(-2):dd/MM/yyyy},COMISION MANEJO DE CUENTA,,250.00,");
        await Bancos.ImportAsync(db, string.Join("\n", lines));
    }

    /// Nóminas creadas antes del cálculo fiscal o de las cuotas patronales: los borradores se recalculan; las pagadas solo reciben su SBC.
    static async Task DemoNominaFiscal(AppDb db)
    {
        var employees = await db.Employees.ToDictionaryAsync(e => e.Id);
        foreach (var run in await db.PayrollRuns.Include(r => r.Lines).ToListAsync())
        {
            foreach (var l in run.Lines.Where(l => l.Sbc == 0))
                l.Sbc = Nomina.Sbc(l.DailySalary, employees.GetValueOrDefault(l.EmployeeId)?.HireDate, run.EndDate);
            if (run.Status == "borrador") Rrhh.Recalculate(run, await Rrhh.ParamsAsync(db));
        }
        await db.SaveChangesAsync();
    }

    /// Datos fiscales de prueba (RFC de pruebas del SAT) para que la demo timbre CFDI en modo de pruebas.
    static async Task DemoFiscal(AppDb db, Guid companyId)
    {
        var c = await db.Companies.SingleAsync(x => x.Id == companyId);
        (c.Rfc, c.LegalName, c.TaxRegime, c.ZipCode) = ("EKU9003173C9", "Escuela Kemper Urgate", "601", "42501");
        string[] zips = ["76000", "64000", "37000", "06600"];
        var i = 0;
        foreach (var x in await db.Customers.OrderBy(x => x.Name).ToListAsync())
            (x.TaxRegime, x.ZipCode, x.CfdiUse) = ("601", zips[i++ % zips.Length], "G03");
        await db.SaveChangesAsync();
    }

    /// 60 días de salidas para que el pronóstico de demanda tenga historia. Una entrada previa las compensa: la existencia actual no cambia.
    static async Task DemoDemanda(AppDb db)
    {
        var rnd = new Random(42);
        var today = DateTime.Today;
        var dated = new List<(Guid Id, DateTime At)>();
        foreach (var p in await db.Products.OrderBy(p => p.Sku).ToListAsync())
        {
            var rate = p.MinStock / 7;
            var outs = new List<InventoryMovement>();
            for (var d = 60; d >= 1; d--)
            {
                var day = today.AddDays(-d);
                var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                var qty = Math.Round(rate * (decimal)(0.4 + rnd.NextDouble() * 1.2) * (weekend ? 0.3m : 1));
                if (qty <= 0) continue;
                var m = new InventoryMovement { ProductId = p.Id, Kind = "salida", Quantity = -qty, Note = "Demo histórico" };
                outs.Add(m);
                dated.Add((m.Id, day.AddHours(10 + rnd.Next(8)).ToUniversalTime()));
            }
            var back = new InventoryMovement { ProductId = p.Id, Kind = "entrada", Quantity = -outs.Sum(o => o.Quantity), Note = "Demo histórico" };
            dated.Add((back.Id, today.AddDays(-61).AddHours(9).ToUniversalTime()));
            db.InventoryMovements.AddRange([back, .. outs]);
        }
        await db.SaveChangesAsync();
        // AppDb sella CreatedAt al guardar; la historia se fecha después.
        var ids = dated.Select(x => x.Id).ToArray();
        var ats = dated.Select(x => x.At).ToArray();
        await db.Database.ExecuteSqlAsync($"UPDATE \"InventoryMovements\" SET \"CreatedAt\" = v.at FROM unnest({ids}, {ats}) AS v(id, at) WHERE \"Id\" = v.id");
    }

    /// Póliza de apertura y las pólizas de lo que ya pasó (facturas, cobros, compras), en orden cronológico.
    static async Task DemoContabilidad(AppDb db, Guid companyId)
    {
        var invoices = await db.Invoices.Include(i => i.Lines).Where(i => i.Status == "emitida" || i.Status == "pagada").ToListAsync();
        var purchases = await db.PurchaseOrders.Where(o => o.Status == "recibida" || o.Status == "pagada").ToListAsync();
        var first = invoices.Select(i => i.IssuedAt).Concat(purchases.Select(o => o.ReceivedAt)).Min() ?? DateTime.UtcNow;
        var opening = DateOnly.FromDateTime(first).AddDays(-1);

        // Inventario inicial valuado al costo actual; el capital cuadra la apertura.
        var stock = await db.InventoryMovements.Where(m => m.Note == "Inventario inicial")
            .Join(db.Products, m => m.ProductId, p => p.Id, (m, p) => m.Quantity * p.Cost).SumAsync();
        await Contabilidad.PostAsync(db, companyId, opening, "diario", "Saldos iniciales", "apertura", null,
            (Contabilidad.Bancos, 500_000, 0), (Contabilidad.Caja, 20_000, 0), (Contabilidad.Inventarios, stock, 0), (Contabilidad.Capital, 0, 520_000 + stock));

        var events = invoices.Select(i => (At: i.IssuedAt!.Value, Post: (Func<Task>)(() => Contabilidad.PostInvoiceEmittedAsync(db, i))))
            .Concat(invoices.Where(i => i.PaidAt is not null).Select(i => (At: i.PaidAt!.Value, Post: (Func<Task>)(() => Contabilidad.PostInvoicePaidAsync(db, i)))))
            .Concat(purchases.Select(o => (At: o.ReceivedAt!.Value, Post: (Func<Task>)(() => Contabilidad.PostPurchaseReceivedAsync(db, o)))))
            .Concat(purchases.Where(o => o.PaidAt is not null).Select(o => (At: o.PaidAt!.Value, Post: (Func<Task>)(() => Contabilidad.PostPurchasePaidAsync(db, o)))))
            .OrderBy(e => e.At);
        foreach (var e in events) await e.Post();

        // Gastos de operación de cada mes, para que el estado de resultados no sea solo ventas.
        for (var m = 5; m >= 0; m--)
        {
            var day = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-m).AddDays(4);
            if (day > DateOnly.FromDateTime(DateTime.Today) || day <= opening) continue;
            await Contabilidad.PostAsync(db, companyId, day, "egreso", "Renta y servicios del mes", "manual", null,
                (Contabilidad.GastosGenerales, 18_500, 0), (Contabilidad.Bancos, 0, 18_500));
        }
        await db.SaveChangesAsync();
    }

    static async Task DemoRrhh(AppDb db)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        Employee E(string name, string position, string dept, decimal daily, int years) =>
            new() { Name = name, Position = position, Department = dept, DailySalary = daily, HireDate = today.AddYears(-years) };
        db.Employees.AddRange(
            E("Laura Méndez Ruiz", "Gerente general", "Dirección", 1_800, 6),
            E("Jorge Salinas Ortiz", "Ejecutivo de ventas", "Ventas", 750, 3),
            E("Ana Torres Vega", "Ejecutiva de ventas", "Ventas", 720, 1),
            E("Raúl Domínguez Paz", "Jefe de almacén", "Almacén", 680, 4),
            E("Pedro Lara Cruz", "Auxiliar de almacén", "Almacén", 420, 2),
            E("Sofía Ramírez León", "Contadora", "Finanzas", 1_100, 5));
        await db.SaveChangesAsync();

        // Dos quincenas pagadas y la actual en borrador.
        var month = new DateOnly(today.Year, today.Month, 1);
        (DateOnly Start, DateOnly End, bool Pay)[] runs =
        [
            (month.AddMonths(-1), month.AddMonths(-1).AddDays(14), true),
            (month.AddMonths(-1).AddDays(15), month.AddDays(-1), true),
            (month, month.AddDays(14), false),
        ];
        foreach (var r in runs)
        {
            var (run, _) = await Rrhh.CreateAsync(db, r.Start, r.End);
            if (run is null) continue;
            await db.SaveChangesAsync();
            if (r.Pay) await Rrhh.PayAsync(db, run.Id, r.End.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc));
        }
    }

    static async Task DemoProyectos(AppDb db)
    {
        var companyId = (await db.Companies.FirstAsync(c => c.Name == "Apolo Demo")).Id;
        var customers = await db.Customers.IgnoreQueryFilters().Where(c => c.CompanyId == companyId).ToDictionaryAsync(c => c.Name);
        var seller = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == "ventas@apolo.test");
        var today = DateOnly.FromDateTime(DateTime.Today);

        void Add(string name, string customer, decimal budget, int startDays, int dueDays, params (string title, bool done, int? due)[] tasks)
        {
            var p = new Project
            {
                CompanyId = companyId, Name = name, CustomerId = customers[customer].Id, Budget = budget,
                StartDate = today.AddDays(startDays), DueDate = today.AddDays(dueDays),
            };
            db.Projects.Add(p);
            foreach (var t in tasks)
                db.ProjectTasks.Add(new ProjectTask
                {
                    CompanyId = companyId, ProjectId = p.Id, Title = t.title, Done = t.done,
                    DueDate = t.due is int d ? today.AddDays(d) : null, AssigneeId = seller?.Id,
                });
        }

        Add("Suministro eléctrico torre B", "Constructora del Bajío", 186_500, -20, 25,
            ("Levantamiento de materiales", true, -15), ("Cotización aprobada", true, -8), ("Primera entrega de cable", false, 3),
            ("Segunda entrega y tableros", false, 15), ("Cierre y facturación final", false, 25));
        Add("Pintura 40 departamentos", "Desarrollos Norte", 142_000, 5, 60,
            ("Muestras de color al cliente", false, 7), ("Programa de entregas por torre", false, 14));
        await db.SaveChangesAsync();
    }

    /// Proveedores y dos órdenes: una enviada esperando mercancía y un borrador para resurtir lo que está bajo mínimo.
    static async Task DemoCompras(AppDb db)
    {
        var companyId = (await db.Companies.FirstAsync(c => c.Name == "Apolo Demo")).Id;
        var products = await db.Products.IgnoreQueryFilters().Where(p => p.CompanyId == companyId).ToDictionaryAsync(p => p.Sku);
        Supplier S(string name, string rfc, int credit) => new() { CompanyId = companyId, Name = name, Rfc = rfc, CreditDays = credit };
        Supplier[] suppliers =
        [
            S("Aceros y Tornillería del Centro", "ATC090511MN5", 30),
            S("Pinturas Industriales GDL", "PIG110820RS6", 15),
            S("Distribuidora Eléctrica Norte", "DEN140303TU7", 45),
        ];
        db.Suppliers.AddRange(suppliers);

        PurchaseOrder Order(Supplier s, string status, params (string sku, decimal qty)[] lines)
        {
            var po = new PurchaseOrder
            {
                CompanyId = companyId, SupplierId = s.Id, Status = status, Notes = "Demo",
                Lines = lines.Select((l, i) => new PurchaseLine
                {
                    CompanyId = companyId, Position = i, ProductId = products[l.sku].Id, Quantity = l.qty,
                    UnitCost = products[l.sku].Cost, TaxRate = 0.16m,
                }).ToList(),
            };
            (po.Subtotal, po.Tax, po.Total) = Facturacion.Totals(po.Lines.Select(l => (l.Quantity, l.UnitCost, l.TaxRate)));
            db.PurchaseOrders.Add(po);
            return po;
        }

        var sent = Order(suppliers[0], "enviada", ("TUE-002", 30), ("TOR-001", 20));
        sent.Folio = await Facturacion.NextFolioAsync(db, companyId, "compra");
        sent.SentAt = DateTime.UtcNow.AddDays(-3);
        Order(suppliers[1], "borrador", ("PIN-020", 16));
        await db.SaveChangesAsync();
    }

    /// Historial de 6 meses para el Dashboard. Partidas de servicio: no mueven inventario.
    static async Task DemoInvoices(AppDb db)
    {
        var companyId = (await db.Companies.FirstAsync(c => c.Name == "Apolo Demo")).Id;
        var customers = await db.Customers.IgnoreQueryFilters().Where(c => c.CompanyId == companyId).OrderBy(c => c.Name).ToListAsync();
        var today = DateTime.UtcNow.Date;
        (int daysAgo, int customer, decimal amount, bool paid)[] items =
        [
            (170, 0, 48_200, true), (152, 1, 21_500, true), (131, 2, 63_900, true), (118, 0, 35_400, true),
            (96, 3, 27_800, true), (74, 2, 71_300, true), (61, 1, 18_900, true), (50, 3, 22_400, false),
            (33, 0, 54_600, true), (12, 2, 39_750, false), (4, 1, 15_300, false),
        ];
        foreach (var i in items)
        {
            var c = customers[i.customer % customers.Count];
            var issued = today.AddDays(-i.daysAgo);
            var inv = new Invoice
            {
                CompanyId = companyId, CustomerId = c.Id, Notes = "Demo",
                Folio = await Facturacion.NextFolioAsync(db, companyId, "factura"),
                Status = i.paid ? "pagada" : "emitida",
                IssuedAt = issued.AddHours(17),
                DueDate = DateOnly.FromDateTime(issued).AddDays(Math.Max(c.CreditDays, 15)),
                PaidAt = i.paid ? issued.AddDays(20) : null,
                Lines = [new InvoiceLine { CompanyId = companyId, Description = "Venta de materiales (demo)", Quantity = 1, UnitPrice = i.amount, TaxRate = 0.16m }],
            };
            if (i.paid) inv.Paid = Math.Round(i.amount * 1.16m, 2);
            (inv.Subtotal, inv.Tax, inv.Total) = Facturacion.Totals(inv.Lines);
            db.Invoices.Add(inv);
        }
        await db.SaveChangesAsync();
    }

    static async Task DemoVentas(AppDb db)
    {
        var companyId = (await db.Companies.FirstAsync(c => c.Name == "Apolo Demo")).Id;
        var seller = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == "ventas@apolo.test");
        var today = DateOnly.FromDateTime(DateTime.Today);

        Customer C(string name, string rfc, int credit) => new() { CompanyId = companyId, Name = name, Rfc = rfc, CreditDays = credit };
        Customer[] customers =
        [
            C("Constructora del Bajío", "CBA120315AB1", 30),
            C("Ferretería La Esquina", "FES980701KL2", 15),
            C("Desarrollos Norte", "DNO150922PQ3", 45),
            C("Mantenimiento Integral MX", "MIM101010ZZ4", 0),
        ];
        db.Customers.AddRange(customers);

        (int customer, string title, decimal amount, string stage, int days)[] opps =
        [
            (0, "Material eléctrico torre B", 186_500, "negociacion", 10),
            (0, "Cemento para cimentación fase 2", 94_000, "propuesta", 25),
            (1, "Resurtido mensual tornillería", 12_800, "ganada", -5),
            (2, "Pintura para 40 departamentos", 142_000, "prospecto", 40),
            (2, "Herramienta eléctrica cuadrilla", 38_900, "propuesta", 18),
            (3, "Contrato anual de consumibles", 64_000, "perdida", -12),
        ];
        foreach (var o in opps)
            db.Opportunities.Add(new Opportunity
            {
                CompanyId = companyId, CustomerId = customers[o.customer].Id, Title = o.title, Amount = o.amount,
                Stage = o.stage, ExpectedClose = today.AddDays(o.days), OwnerId = seller?.Id,
            });
        await db.SaveChangesAsync();
    }

    static async Task DemoInventory(AppDb db)
    {
        var companyId = (await db.Companies.FirstAsync(c => c.Name == "Apolo Demo")).Id;
        (string sku, string name, string unit, decimal cost, decimal price, decimal min, decimal stock)[] items =
        [
            ("TOR-001", "Tornillo hexagonal 1/4\" (caja 100)", "caja", 85, 140, 20, 64),
            ("TUE-002", "Tuerca 1/4\" (caja 100)", "caja", 40, 72, 20, 12),
            ("CAB-010", "Cable THW calibre 12", "m", 9.5m, 16, 500, 1850),
            ("PIN-020", "Pintura vinílica blanca 19 L", "cubeta", 980, 1490, 10, 4),
            ("CEM-030", "Cemento gris 50 kg", "saco", 210, 285, 40, 160),
            ("GUA-040", "Guantes de nitrilo (par)", "par", 18, 35, 100, 340),
            ("TAL-050", "Taladro percutor 1/2\"", "pza", 1450, 2290, 5, 7),
            ("LIJ-060", "Lija de agua grano 220", "pza", 6, 12, 200, 90),
        ];
        foreach (var i in items)
        {
            var p = new Product { CompanyId = companyId, Sku = i.sku, Name = i.name, Unit = i.unit, Cost = i.cost, Price = i.price, MinStock = i.min };
            db.Products.Add(p);
            db.InventoryMovements.Add(new InventoryMovement { CompanyId = companyId, ProductId = p.Id, Kind = "entrada", Quantity = i.stock, Note = "Inventario inicial" });
        }
        await db.SaveChangesAsync();
    }

    /// Los cuatro roles con que arranca toda empresa, demo o real; Gerencia ve todos los módulos.
    public static Role[] DefaultRoles(Guid companyId)
    {
        string[] general = ["dashboard", "ia"];
        Role R(string name, params string[] mods) => new() { CompanyId = companyId, Name = name, Modules = [.. general, .. mods] };
        return
        [
            new() { CompanyId = companyId, Name = "Gerencia", Modules = Modules.All.Select(m => m.Code).ToList() },
            R("Ventas", "ventas", "facturacion", "proyectos", "reportes"),
            R("Almacén", "inventario", "compras", "reportes"),
            R("Finanzas", "facturacion", "compras", "contabilidad", "rrhh", "reportes"),
        ];
    }

    static async Task DemoCompany(AppDb db)
    {
        var company = new Company { Name = "Apolo Demo" };
        var roles = DefaultRoles(company.Id);

        var hasher = new PasswordHasher<User>();
        var users = roles.Select(r =>
        {
            var u = new User
            {
                CompanyId = company.Id,
                RoleId = r.Id,
                Name = $"Demo {r.Name}",
                Email = $"{r.Name.ToLowerInvariant().Replace("é", "e")}@apolo.test",
            };
            u.PasswordHash = hasher.HashPassword(u, "apolo1234");
            return u;
        });

        db.Companies.Add(company);
        db.Roles.AddRange(roles);
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
    }
}
