using System.Text.Json;
using Anthropic.Exceptions;
using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class IaTests
{
    readonly AppDb db = new(
        new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new CurrentUser { CompanyId = Guid.NewGuid(), UserId = Guid.NewGuid() });

    async Task<AgentAction> Proposal(Guid customerId)
    {
        var a = new AgentAction
        {
            Kind = "factura", Summary = "Factura demo",
            Payload = JsonSerializer.Serialize(new InvoiceRequest(customerId, null, [new(null, "Servicio", 2, 100, 0.16m)])),
        };
        db.AgentActions.Add(a);
        await db.SaveChangesAsync();
        return a;
    }

    [Fact]
    public async Task Approving_creates_the_draft_once_and_records_who_approved()
    {
        var c = new Customer { Name = "Cliente" };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        Assert.Empty(db.Invoices); // proposing creates nothing

        var a = await Proposal(c.Id);
        var approver = Guid.NewGuid();
        Assert.Null(await Ia.DecideAsync(db, a, true, approver));

        var inv = await db.Invoices.SingleAsync();
        Assert.Equal((a.ResultId, "borrador", 232m), (inv.Id, inv.Status, inv.Total));
        Assert.Equal(("aprobada", approver), (a.Status, a.DecidedBy));
        Assert.NotNull(await Ia.DecideAsync(db, a, true, approver)); // it is not approved twice
        Assert.Single(db.Invoices);
    }

    [Fact]
    public async Task Approval_revalidates_and_stays_pending_when_data_changed()
    {
        var c = new Customer { Name = "Cliente" };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        var a = await Proposal(c.Id);
        c.Active = false; // the customer was removed after the proposal
        await db.SaveChangesAsync();

        Assert.NotNull(await Ia.DecideAsync(db, a, true, Guid.NewGuid()));
        Assert.Equal("pendiente", a.Status);
        Assert.Empty(db.Invoices);
    }

    [Fact]
    public void Cartera_totals_count_only_overdue_rows_as_overdue_and_cover_rows_beyond_the_list_limit()
    {
        var filas = Enumerable.Range(0, 60).Select(i => new Ia.FilaCartera($"F-{i}", "Al corriente", 10m, null, 0)).ToList();
        filas.Insert(0, new("V-1", "Atrasado", 100m, new DateOnly(2026, 1, 1), 5));
        filas.Insert(1, new("V-2", "Atrasado", 50m, new DateOnly(2026, 1, 2), 4));

        var r = Ia.ResumirCartera(filas);

        Assert.Equal((750m, 150m), (r.TotalPorCobrar, r.TotalVencido));
        Assert.Equal(["Atrasado"], r.ClientesConVencidas);
        Assert.Equal(50, r.Facturas.Count);
    }

    [Fact]
    public void Pipeline_summary_finds_total_largest_and_nearest_close_ignoring_missing_dates()
    {
        var r = Ia.ResumirPipeline(
        [
            new("Sin fecha", 900m, "prospecto", null, "A"),
            new("Tarde", 100m, "propuesta", new DateOnly(2026, 12, 1), "B"),
            new("Pronto", 200m, "negociacion", new DateOnly(2026, 11, 20), "C"),
        ]);

        Assert.Equal((1200m, 3, "Sin fecha", "Pronto"), (r.Total, r.Cantidad, r.MayorMonto!.Title, r.CierreMasProximo!.Title));
        Assert.Null(Ia.ResumirPipeline([]).MayorMonto);
    }

    [Fact]
    public async Task RetryEmpty_repeats_only_empty_replies_and_gives_up_after_the_limit()
    {
        var calls = 0;
        Assert.Equal("ok", await Ia.RetryEmpty<string>(() => ++calls < 3 ? throw new AnthropicInvalidDataException("vacío") : Task.FromResult("ok")));
        Assert.Equal(3, calls);

        calls = 0;
        await Assert.ThrowsAsync<AnthropicInvalidDataException>(() => Ia.RetryEmpty<string>(() => { calls++; throw new AnthropicInvalidDataException("vacío"); }));
        Assert.Equal(3, calls);

        calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ia.RetryEmpty<string>(() => { calls++; throw new InvalidOperationException(); }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Cost_uses_model_prices_is_zero_for_a_local_model_and_assumes_the_highest_price_when_unknown()
    {
        Assert.Equal(24m, Ia.Costo("claude-opus-5-5", false, 1_000_000, 1_000_000));
        Assert.Equal(0.0024m, Ia.Costo("claude-opus-5-5", false, 100, 100));
        Assert.Equal(0m, Ia.Costo("qwen2.5:3b-instruct", true, 1_000_000, 1_000_000));
        Assert.Equal(60m, Ia.Costo("modelo-nuevo", false, 1_000_000, 1_000_000));
    }

    [Fact]
    public async Task Monthly_spend_counts_only_this_month_and_is_not_written_to_the_audit_log()
    {
        var old = new AgentUsage { RequestId = "a", Model = "m", CostUsd = 100m };
        db.AgentUsages.AddRange(old, new AgentUsage { RequestId = "b", Model = "m", CostUsd = 1.5m }, new AgentUsage { RequestId = "c", Model = "m", CostUsd = 0.25m });
        await db.SaveChangesAsync();
        old.CreatedAt = DateTime.UtcNow.AddMonths(-2);
        await db.SaveChangesAsync();

        Assert.Equal(1.75m, await Ia.GastoDelMes(db));
        Assert.Empty(db.AuditEntries);
    }

    [Fact]
    public void Optional_dates_accept_empty_text_and_reject_invalid_values()
    {
        var options = new JsonSerializerOptions { Converters = { new Ia.FechaOpcional() } };

        Assert.Null(JsonSerializer.Deserialize<DateOnly?>("\"\"", options));
        Assert.Null(JsonSerializer.Deserialize<DateOnly?>("null", options));
        Assert.Equal(new DateOnly(2026, 11, 7), JsonSerializer.Deserialize<DateOnly?>("\"2026-11-07\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DateOnly?>("\"ayer\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DateOnly?>("5", options));
        Assert.Equal("\"2026-11-07\"", JsonSerializer.Serialize<DateOnly?>(new DateOnly(2026, 11, 7), options));
    }
}
