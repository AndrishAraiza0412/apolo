using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public record ChatTurn(string Role, string Text);
public record ChatRequest(List<ChatTurn> Messages);

/// Apolo IA (DESIGN.md §4): Claude con herramientas filtradas por rol. Las consultas leen con el filtro de empresa;
/// los borradores solo crean una AgentAction pendiente que una persona aprueba.
public static class Ia
{
    const string ModelId = "claude-opus-5-5";
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    record Herramienta(string Name, string[] Modules, string Description, object Properties, string[] Required,
        Func<AppDb, ClaimsPrincipal, JsonElement, Task<object>> Run);

    // Parámetros de las herramientas (snake_case en el JSON que manda el modelo).
    record VentasArgs(int? Meses);
    record CarteraArgs(string? Cliente);
    record ReporteArgs(string Reporte, DateOnly? Desde, DateOnly? Hasta);
    record StockArgs(string? Buscar, bool? SoloBajoMinimo);
    record TareaArgs(string Tipo, string? Reporte, DateOnly? Desde, DateOnly? Hasta);
    record PartidaFactura(string? Sku, string? Descripcion, decimal Cantidad, decimal? PrecioUnitario, decimal? Iva);
    record FacturaArgs(string Cliente, List<PartidaFactura> Partidas, string? Notas);
    record PartidaOc(string Sku, decimal Cantidad, decimal? CostoUnitario);
    record OcArgs(string Proveedor, List<PartidaOc> Partidas, string? Notas);

    /// Error que se le regresa al modelo para que corrija o lo explique; no es una falla del servidor.
    class ToolError(string message) : Exception(message);

    static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");
    static string? Key(IConfiguration c) => c["Anthropic:ApiKey"] is { Length: > 0 } k ? k : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    static T Args<T>(JsonElement input) => input.Deserialize<T>(Json) ?? throw new ToolError("Parámetros inválidos.");

    static readonly Herramienta[] Tools =
    [
        new("consultar_ventas", ["facturacion"],
            "Ventas facturadas (subtotal sin IVA de facturas emitidas o pagadas) por mes y los 5 clientes con más facturación en el periodo.",
            new { meses = new { type = "integer", description = "Meses hacia atrás, incluido el actual (1 a 12). Por omisión 6." } }, [],
            async (db, _, input) =>
            {
                var meses = Math.Clamp(Args<VentasArgs>(input).Meses ?? 6, 1, 12);
                var today = DateTime.UtcNow;
                var desde = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1 - meses);
                var facturas = db.Invoices.Where(i => (i.Status == "emitida" || i.Status == "pagada") && i.IssuedAt >= desde);
                return new
                {
                    Desde = desde.ToString("yyyy-MM-dd"),
                    PorMes = await facturas.GroupBy(i => new { i.IssuedAt!.Value.Year, i.IssuedAt.Value.Month })
                        .Select(g => new { g.Key.Year, g.Key.Month, Subtotal = g.Sum(i => i.Subtotal), Facturas = g.Count() })
                        .OrderBy(x => x.Year).ThenBy(x => x.Month).ToListAsync(),
                    TopClientes = await facturas.GroupBy(i => i.CustomerId)
                        .Select(g => new { Cliente = db.Customers.Where(c => c.Id == g.Key).Select(c => c.Name).First(), Subtotal = g.Sum(i => i.Subtotal) })
                        .OrderByDescending(x => x.Subtotal).Take(5).ToListAsync(),
                };
            }),

        new("consultar_cartera", ["facturacion"],
            "Facturas emitidas sin pagar (cuentas por cobrar), con vencimiento y días de atraso. Filtra opcionalmente por nombre de cliente.",
            new { cliente = new { type = "string", description = "Parte del nombre del cliente." } }, [],
            async (db, _, input) =>
            {
                var cliente = Args<CarteraArgs>(input).Cliente;
                var today = DateOnly.FromDateTime(DateTime.Today);
                var q = db.Invoices.Where(i => i.Status == "emitida");
                if (!string.IsNullOrWhiteSpace(cliente))
                    q = q.Where(i => db.Customers.Any(c => c.Id == i.CustomerId && EF.Functions.ILike(c.Name, $"%{cliente}%")));
                var rows = await q.OrderBy(i => i.DueDate).Select(i => new
                {
                    i.Folio, Total = i.Total - i.Paid - i.Credited, i.DueDate,
                    Cliente = db.Customers.Where(c => c.Id == i.CustomerId).Select(c => c.Name).First(),
                }).Take(50).ToListAsync();
                return rows.Select(i => new
                {
                    Folio = Facturacion.FolioText(i.Folio), i.Cliente, i.Total, Vence = i.DueDate,
                    DiasVencida = i.DueDate < today ? today.DayNumber - i.DueDate.Value.DayNumber : 0,
                });
            }),

        new("consultar_pipeline", ["ventas"],
            "Oportunidades de venta abiertas (prospecto, propuesta, negociación) con cliente, monto y cierre esperado.",
            new { }, [],
            async (db, _, _) => await db.Opportunities.Where(o => o.Stage != "ganada" && o.Stage != "perdida")
                .OrderBy(o => o.ExpectedClose).Select(o => new
                {
                    o.Title, o.Amount, Etapa = o.Stage, CierreEsperado = o.ExpectedClose,
                    Cliente = db.Customers.Where(c => c.Id == o.CustomerId).Select(c => c.Name).First(),
                }).ToListAsync()),

        new("consultar_stock", ["inventario", "compras"],
            "Existencias por producto (calculadas de los movimientos), mínimo, costo y precio. Puede buscar por SKU o nombre y limitar a lo que está bajo mínimo.",
            new
            {
                buscar = new { type = "string", description = "Parte del SKU o del nombre." },
                solo_bajo_minimo = new { type = "boolean", description = "Solo productos con existencia menor al mínimo." },
            }, [],
            async (db, _, input) =>
            {
                var a = Args<StockArgs>(input);
                var q = db.Products.Where(p => p.Active);
                if (!string.IsNullOrWhiteSpace(a.Buscar))
                    q = q.Where(p => EF.Functions.ILike(p.Sku, $"%{a.Buscar}%") || EF.Functions.ILike(p.Name, $"%{a.Buscar}%"));
                var rows = q.OrderBy(p => p.Sku).Select(p => new
                {
                    p.Sku, Nombre = p.Name, Unidad = p.Unit, Minimo = p.MinStock, Costo = p.Cost, Precio = p.Price,
                    Existencia = db.InventoryMovements.Where(m => m.ProductId == p.Id).Sum(m => m.Quantity),
                });
                if (a.SoloBajoMinimo == true) rows = rows.Where(p => p.Existencia < p.Minimo);
                return await rows.Take(50).ToListAsync();
            }),

        new("generar_reporte", ["reportes"],
            "Genera un reporte tabular. Reportes según el rol: ventas-por-cliente, antiguedad-saldos, valuacion-inventario, " +
            "compras-por-proveedor, estado-resultados, nomina. Si el rol no tiene el reporte, la herramienta lo dice.",
            new
            {
                reporte = new { type = "string", description = "Código del reporte." },
                desde = new { type = "string", description = "Fecha inicial YYYY-MM-DD (por omisión, inicio del mes)." },
                hasta = new { type = "string", description = "Fecha final YYYY-MM-DD (por omisión, hoy)." },
            }, ["reporte"],
            async (db, user, input) =>
            {
                var a = Args<ReporteArgs>(input);
                var available = Reportes.Available(user).ToList();
                return await Reportes.RunAsync(db, user, a.Reporte, a.Desde, a.Hasta) as object
                    ?? throw new ToolError($"Reporte no disponible para este rol. Disponibles: {string.Join(", ", available.Select(r => r.Code))}.");
            }),

        new("pronostico_demanda", ["inventario", "compras"],
            "Pronóstico de demanda por producto (promedio exponencial de las salidas de 90 días): demanda diaria, días para llegar al mínimo " +
            "y para agotarse, y cantidad sugerida a pedir para cubrir 30 días más el mínimo. El cálculo es del sistema; tú solo lo explicas.",
            new { buscar = new { type = "string", description = "Parte del SKU o del nombre." } }, [],
            async (db, _, input) =>
            {
                var a = Args<StockArgs>(input);
                return (await Pronosticos.DemandAsync(db))
                    .Where(d => string.IsNullOrWhiteSpace(a.Buscar) || d.Sku.Contains(a.Buscar, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(a.Buscar, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d.DaysToMinimum ?? int.MaxValue).Take(30)
                    .Select(d => new { d.Sku, Nombre = d.Name, Existencia = d.Stock, Minimo = d.MinStock, DemandaDiaria = d.DailyDemand, DiasParaMinimo = d.DaysToMinimum, DiasParaAgotarse = d.DaysToStockout, SugeridoPedir = d.SuggestedOrder })
                    .ToList();
            }),

        new("pronostico_caja", ["contabilidad"],
            "Saldo proyectado de caja y bancos al cierre de los próximos 3 meses, con los conceptos que lo mueven y los supuestos del cálculo. Menciona los supuestos al explicarlo.",
            new { }, [],
            async (db, _, _) => await Pronosticos.CashAsync(db)),

        new("tarea_en_segundo_plano", ["contabilidad", "reportes"],
            "Lanza una tarea larga en segundo plano: 'conciliacion' (revisa que pólizas, existencias, facturas y órdenes cuadren con los libros) " +
            "o 'reporte' (genera un reporte). Regresa de inmediato; el avance se ve en la Cola de tareas.",
            new
            {
                tipo = new { type = "string", @enum = new[] { "conciliacion", "reporte" } },
                reporte = new { type = "string", description = "Código del reporte, si tipo = reporte." },
                desde = new { type = "string", description = "Fecha inicial YYYY-MM-DD." },
                hasta = new { type = "string", description = "Fecha final YYYY-MM-DD." },
            }, ["tipo"],
            async (db, user, input) =>
            {
                var a = Args<TareaArgs>(input);
                var (job, error) = await Tareas.EnqueueAsync(db, user, new(a.Tipo, a.Reporte, a.Desde, a.Hasta));
                return error is null ? new { job!.Id, Tarea = job.Title, Estado = "pendiente" } : throw new ToolError(error);
            }),

        new("crear_factura_borrador", ["facturacion"],
            "Propone una factura en borrador. NO la crea: queda en la cola de tareas hasta que una persona la apruebe. " +
            "Cada partida lleva sku (producto del inventario; el precio por omisión es el de lista) o descripción y precio (servicio).",
            new
            {
                cliente = new { type = "string", description = "Nombre del cliente tal como está registrado." },
                partidas = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            sku = new { type = "string" },
                            descripcion = new { type = "string" },
                            cantidad = new { type = "number" },
                            precio_unitario = new { type = "number", description = "Sin IVA." },
                            iva = new { type = "number", description = "0.16, 0.08 o 0. Por omisión 0.16." },
                        },
                        required = new[] { "cantidad" },
                    },
                },
                notas = new { type = "string" },
            }, ["cliente", "partidas"],
            async (db, _, input) =>
            {
                var a = Args<FacturaArgs>(input);
                var customer = await Unique(db.Customers.Where(c => c.Active), c => c.Name, a.Cliente, "cliente");
                var lines = new List<InvoiceLineRequest>();
                foreach (var p in a.Partidas ?? [])
                {
                    var product = p.Sku is { Length: > 0 } ? await db.Products.SingleOrDefaultAsync(x => x.Sku == p.Sku && x.Active)
                        ?? throw new ToolError($"No existe el SKU {p.Sku}.") : null;
                    lines.Add(new(product?.Id, p.Descripcion ?? product?.Name ?? "", p.Cantidad,
                        p.PrecioUnitario ?? product?.Price ?? throw new ToolError("Las partidas de servicio necesitan precio_unitario."), p.Iva ?? 0.16m));
                }
                var req = new InvoiceRequest(customer.Id, a.Notas, lines);
                var errors = await Facturacion.ValidateAsync(db, req);
                if (errors.Count > 0) throw new ToolError(string.Join(" ", errors.Values.SelectMany(v => v)));
                var (_, _, total) = Facturacion.Totals(lines.Select(l => (l.Quantity, l.UnitPrice, l.TaxRate)));
                return await Propose(db, "factura", $"Factura para {customer.Name} por {total.ToString("C", Mx)} ({lines.Count} partidas)", req);
            }),

        new("crear_oc_borrador", ["compras"],
            "Propone una orden de compra en borrador. NO la crea: queda en la cola de tareas hasta que una persona la apruebe. " +
            "El costo por omisión es el último costo del producto.",
            new
            {
                proveedor = new { type = "string", description = "Nombre del proveedor tal como está registrado." },
                partidas = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            sku = new { type = "string" },
                            cantidad = new { type = "number" },
                            costo_unitario = new { type = "number", description = "Sin IVA." },
                        },
                        required = new[] { "sku", "cantidad" },
                    },
                },
                notas = new { type = "string" },
            }, ["proveedor", "partidas"],
            async (db, _, input) =>
            {
                var a = Args<OcArgs>(input);
                var supplier = await Unique(db.Suppliers.Where(s => s.Active), s => s.Name, a.Proveedor, "proveedor");
                var lines = new List<PurchaseLineRequest>();
                foreach (var p in a.Partidas ?? [])
                {
                    var product = await db.Products.SingleOrDefaultAsync(x => x.Sku == p.Sku && x.Active) ?? throw new ToolError($"No existe el SKU {p.Sku}.");
                    lines.Add(new(product.Id, p.Cantidad, p.CostoUnitario ?? product.Cost, 0.16m));
                }
                var req = new PurchaseRequest(supplier.Id, a.Notas, lines);
                var errors = await Compras.ValidateAsync(db, req);
                if (errors.Count > 0) throw new ToolError(string.Join(" ", errors.Values.SelectMany(v => v)));
                var (_, _, total) = Facturacion.Totals(lines.Select(l => (l.Quantity, l.UnitCost, l.TaxRate)));
                return await Propose(db, "orden_compra", $"Orden de compra a {supplier.Name} por {total.ToString("C", Mx)} ({lines.Count} partidas)", req);
            }),
    ];

    /// Busca por nombre exacto y, si no, por coincidencia parcial única. Ambigüedad = error para que el modelo pregunte.
    static async Task<T> Unique<T>(IQueryable<T> q, System.Linq.Expressions.Expression<Func<T, string>> name, string text, string noun) where T : class
    {
        var all = await q.ToListAsync();
        var get = name.Compile();
        var exact = all.Where(x => string.Equals(get(x), text?.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact : all.Where(x => get(x).Contains(text?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ToolError($"No encontré el {noun} \"{text}\"."),
            _ => throw new ToolError($"\"{text}\" coincide con varios: {string.Join(", ", matches.Select(get))}. Pregunta cuál."),
        };
    }

    static async Task<object> Propose(AppDb db, string kind, string summary, object payload)
    {
        var a = new AgentAction { Kind = kind, Summary = summary, Payload = JsonSerializer.Serialize(payload) };
        db.AgentActions.Add(a);
        await db.SaveChangesAsync();
        return new { Propuesta = a.Id, Estado = "pendiente de aprobación en la cola de tareas", Resumen = summary };
    }

    static IEnumerable<Herramienta> ToolsFor(ClaimsPrincipal user) =>
        Tools.Where(t => t.Modules.Any(m => user.HasClaim(Modules.ClaimType, m)));

    static string ModuleFor(string kind) => kind == "factura" ? "facturacion" : "compras";

    /// Crea el borrador real con las mismas validaciones del módulo. Los datos pudieron cambiar desde la propuesta.
    static async Task<(Guid? Id, string? Error)> ApplyAsync(AppDb db, AgentAction a)
    {
        if (a.Kind == "factura")
        {
            var req = JsonSerializer.Deserialize<InvoiceRequest>(a.Payload)!;
            var errors = await Facturacion.ValidateAsync(db, req);
            if (errors.Count > 0) return (null, string.Join(" ", errors.Values.SelectMany(v => v)));
            var inv = new Invoice();
            Facturacion.Apply(inv, req);
            db.Invoices.Add(inv);
            return (inv.Id, null);
        }
        else
        {
            var req = JsonSerializer.Deserialize<PurchaseRequest>(a.Payload)!;
            var errors = await Compras.ValidateAsync(db, req);
            if (errors.Count > 0) return (null, string.Join(" ", errors.Values.SelectMany(v => v)));
            var po = new PurchaseOrder();
            Compras.Apply(po, req);
            db.PurchaseOrders.Add(po);
            return (po.Id, null);
        }
    }

    /// Aprobar crea el borrador y marca la propuesta en el mismo SaveChanges: no hay propuesta aprobada sin borrador.
    public static async Task<string?> DecideAsync(AppDb db, AgentAction a, bool approve, Guid? userId)
    {
        if (a.Status != "pendiente") return "Esta propuesta ya se resolvió.";
        if (approve)
        {
            var (resultId, error) = await ApplyAsync(db, a);
            if (error is not null) return error;
            a.ResultId = resultId;
        }
        a.Status = approve ? "aprobada" : "rechazada";
        a.DecidedBy = userId;
        a.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return null;
    }

    public static void MapIa(this WebApplication app)
    {
        var g = app.MapGroup("/api/ia").RequireAuthorization("ia");

        g.MapGet("/tools", (ClaimsPrincipal user, IConfiguration config) => new
        {
            Configured = !string.IsNullOrWhiteSpace(Key(config)),
            Tools = ToolsFor(user).Select(t => new { t.Name, t.Description, Writes = t.Name.StartsWith("crear_") }),
        });

        g.MapPost("/chat", async (ChatRequest req, AppDb db, ClaimsPrincipal user, IConfiguration config, ILoggerFactory log, CurrentUser me) =>
        {
            var key = Key(config);
            if (string.IsNullOrWhiteSpace(key))
                return Results.Problem("Apolo IA no está configurado: falta la variable ANTHROPIC_API_KEY en el servidor.", statusCode: 503);
            if (req.Messages is not { Count: > 0 and <= 40 } || req.Messages.Any(m => m.Text is not { Length: > 0 and <= 4000 } || m.Role is not ("user" or "assistant"))
                || req.Messages[0].Role != "user" || req.Messages[^1].Role != "user")
                return Results.Problem("Conversación inválida.", statusCode: 400);

            var tools = ToolsFor(user).ToList();
            var company = await db.Companies.IgnoreQueryFilters().Where(c => c.Id == me.CompanyId).Select(c => c.Name).SingleAsync();
            var role = user.FindFirstValue(ClaimTypes.Role);
            var system = $"""
                Eres Apolo IA, el asistente del ERP de {company}. Responde en español de México, breve y claro. Moneda: MXN.
                Usuario: {user.FindFirstValue(ClaimTypes.Name)}, rol {role}. Hoy es {DateTime.Today:yyyy-MM-dd}.
                Reglas:
                - Cifras solo de las herramientas. Si no hay herramienta o datos para algo, dilo; nunca inventes ni estimes cifras.
                - Las herramientas disponibles son exactamente las que permite el rol. Si piden algo fuera de ellas, explica que su rol no tiene acceso.
                - Las herramientas crear_* no crean nada: dejan una propuesta en la cola de tareas que una persona aprueba. Dilo así.
                - Antes de proponer un borrador con datos ambiguos (cliente, cantidades, precios), pregunta.
                """;

            var messages = req.Messages.Select(m => m.Role == "user"
                ? (BetaMessageParam)new BetaUserMessageParam { Content = m.Text }
                : new BetaAssistantMessageParam { Content = m.Text }).ToList();
            var consulted = new List<object>();
            var client = new AnthropicClient { ApiKey = key };

            try
            {
                // ponytail: tope de 8 rondas de herramientas por mensaje; subir si las preguntas reales lo necesitan.
                for (var round = 0; round < 8; round++)
                {
                    var response = await client.Beta.Messages.Create(new MessageCreateParams
                    {
                        Model = ModelId,
                        MaxTokens = 16000,
                        System = system,
                        OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                        // Si un clasificador rechaza la petición, la atiende el modelo de respaldo en la misma llamada.
                        Betas = ["server-side-fallback-2026-07-01"],
                        Fallbacks = new Default(),
                        Tools = tools.Select(t => (BetaToolUnion)new BetaTool
                        {
                            Name = t.Name,
                            Description = t.Description,
                            InputSchema = new()
                            {
                                Properties = JsonSerializer.SerializeToElement(t.Properties).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
                                Required = t.Required,
                            },
                        }).ToList(),
                        Messages = messages,
                    });

                    if (response.StopReason == "refusal")
                        return Results.Ok(new { Reply = "No puedo ayudar con esa solicitud.", Consulted = consulted });

                    var assistant = new List<BetaContentBlockParam>();
                    var results = new List<BetaContentBlockParam>();
                    var text = "";
                    foreach (var block in response.Content)
                    {
                        if (block.TryPickText(out var t)) { text += t.Text; assistant.Add(new BetaTextBlockParam { Text = t.Text }); }
                        else if (block.TryPickThinking(out var th)) assistant.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                        else if (block.TryPickRedactedThinking(out var rt)) assistant.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                        else if (block.TryPickToolUse(out var tu))
                        {
                            assistant.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                            var tool = tools.FirstOrDefault(x => x.Name == tu.Name);
                            string content; bool error = false;
                            try
                            {
                                if (tool is null) throw new ToolError("Herramienta no permitida para este rol.");
                                content = JsonSerializer.Serialize(await tool.Run(db, user, JsonSerializer.SerializeToElement(tu.Input)), Json);
                            }
                            catch (Exception ex) when (ex is ToolError or JsonException)
                            {
                                content = ex.Message; error = true;
                            }
                            consulted.Add(new { Tool = tu.Name, Ok = !error });
                            results.Add(new BetaToolResultBlockParam { ToolUseID = tu.ID, Content = content, IsError = error });
                        }
                    }

                    if (response.StopReason != "tool_use")
                        return Results.Ok(new { Reply = text.Trim(), Consulted = consulted });
                    messages.Add(new BetaAssistantMessageParam { Content = assistant });
                    messages.Add(new BetaUserMessageParam { Content = results });
                }
                return Results.Ok(new { Reply = "La consulta necesitó demasiados pasos. Intenta una pregunta más concreta.", Consulted = consulted });
            }
            catch (AnthropicRateLimitException)
            {
                return Results.Problem("Apolo IA está saturado. Intenta en un minuto.", statusCode: 429);
            }
            catch (AnthropicApiException ex)
            {
                log.CreateLogger("Ia").LogError(ex, "Error de la API de Claude");
                return Results.Problem("Apolo IA no respondió. Intenta de nuevo.", statusCode: 502);
            }
        });

        g.MapGet("/actions", async (AppDb db, ClaimsPrincipal user) =>
        {
            var kinds = AgentAction.Kinds.Where(k => user.HasClaim(Modules.ClaimType, ModuleFor(k))).ToList();
            var rows = await db.AgentActions.Where(a => kinds.Contains(a.Kind))
                .OrderBy(a => a.Status != "pendiente").ThenByDescending(a => a.CreatedAt).Take(50)
                .Select(a => new
                {
                    a.Id, a.Kind, a.Summary, a.Status, a.CreatedAt, a.DecidedAt, a.ResultId, a.Error,
                    RequestedBy = db.Users.Where(u => u.Id == a.CreatedBy).Select(u => u.Name).FirstOrDefault(),
                    DecidedBy = db.Users.Where(u => u.Id == a.DecidedBy).Select(u => u.Name).FirstOrDefault(),
                }).ToListAsync();
            return rows;
        });

        // Aprobar exige permiso sobre el módulo del borrador, además de "ia". La bitácora guarda quién aprobó.
        g.MapPost("/actions/{id:guid}/{decision:regex(^(approve|reject)$)}", async (Guid id, string decision, AppDb db, ClaimsPrincipal user, CurrentUser me) =>
        {
            var a = await db.AgentActions.SingleOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            if (!user.HasClaim(Modules.ClaimType, ModuleFor(a.Kind))) return Results.Forbid();
            if (a.Status != "pendiente") return Results.Problem("Esta propuesta ya se resolvió.", statusCode: 409);

            if (await DecideAsync(db, a, decision == "approve", me.UserId) is { } error)
                return Results.Problem($"No se pudo crear el borrador: {error}", statusCode: 422);
            return Results.Ok(new { a.Status, a.ResultId });
        });
    }
}
