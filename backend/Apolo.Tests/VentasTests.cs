using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class VentasTests
{
    readonly string dbName = Guid.NewGuid().ToString();
    readonly Guid companyA = Guid.NewGuid(), companyB = Guid.NewGuid();

    AppDb Db(Guid? company) => new(
        new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(dbName).Options,
        new CurrentUser { CompanyId = company, UserId = Guid.NewGuid() });

    [Fact]
    public async Task Opportunity_rejects_a_customer_from_another_company()
    {
        Guid foreign;
        using (var b = Db(companyB))
        {
            var c = new Customer { Name = "Cliente de B" };
            b.Customers.Add(c);
            await b.SaveChangesAsync();
            foreign = c.Id;
        }

        using var a = Db(companyA);
        var errors = await Ventas.ValidateAsync(a, new OpportunityRequest(foreign, "Venta", 100, "prospecto", null, null));
        Assert.Contains("customerId", errors.Keys);
    }

    [Fact]
    public async Task Opportunity_validates_stage_and_amount()
    {
        using var a = Db(companyA);
        var c = new Customer { Name = "Cliente" };
        a.Customers.Add(c);
        await a.SaveChangesAsync();

        Assert.Empty(await Ventas.ValidateAsync(a, new OpportunityRequest(c.Id, "Venta", 100, "propuesta", null, null)));
        var errors = await Ventas.ValidateAsync(a, new OpportunityRequest(c.Id, "Venta", -1, "cerrada", null, null));
        Assert.Contains("amount", errors.Keys);
        Assert.Contains("stage", errors.Keys);
    }
}
