using System.Text.Json;
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
        Assert.Empty(db.Invoices); // proponer no crea nada

        var a = await Proposal(c.Id);
        var approver = Guid.NewGuid();
        Assert.Null(await Ia.DecideAsync(db, a, true, approver));

        var inv = await db.Invoices.SingleAsync();
        Assert.Equal((a.ResultId, "borrador", 232m), (inv.Id, inv.Status, inv.Total));
        Assert.Equal(("aprobada", approver), (a.Status, a.DecidedBy));
        Assert.NotNull(await Ia.DecideAsync(db, a, true, approver)); // no se aprueba dos veces
        Assert.Single(db.Invoices);
    }

    [Fact]
    public async Task Approval_revalidates_and_stays_pending_when_data_changed()
    {
        var c = new Customer { Name = "Cliente" };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        var a = await Proposal(c.Id);
        c.Active = false; // el cliente se dio de baja después de la propuesta
        await db.SaveChangesAsync();

        Assert.NotNull(await Ia.DecideAsync(db, a, true, Guid.NewGuid()));
        Assert.Equal("pendiente", a.Status);
        Assert.Empty(db.Invoices);
    }
}
