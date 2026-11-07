using Apolo.Api;
using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Tests;

public class AppDbTests
{
    readonly string dbName = Guid.NewGuid().ToString();
    readonly Guid companyA = Guid.NewGuid(), companyB = Guid.NewGuid(), userA = Guid.NewGuid();

    AppDb Db(Guid? company = null, Guid? user = null) => new(
        new DbContextOptionsBuilder<AppDb>().UseInMemoryDatabase(dbName).Options,
        new CurrentUser { CompanyId = company, UserId = user });

    [Fact]
    public async Task Reads_are_scoped_to_the_users_company()
    {
        using (var seed = Db())
        {
            seed.Roles.AddRange(
                new Role { CompanyId = companyA, Name = "A" },
                new Role { CompanyId = companyB, Name = "B" });
            await seed.SaveChangesAsync();
        }

        using var db = Db(companyA, userA);
        Assert.Equal(["A"], await db.Roles.Select(r => r.Name).ToListAsync());
    }

    [Fact]
    public async Task Writes_are_stamped_and_audited_without_secrets()
    {
        using var db = Db(companyA, userA);
        var role = new Role { Name = "Ventas" };
        db.Roles.Add(role);
        db.Users.Add(new User { Name = "Ana", Email = "ana@x.mx", RoleId = role.Id, PasswordHash = "hash-secreto" });
        await db.SaveChangesAsync();

        Assert.Equal(companyA, role.CompanyId);
        Assert.Equal(userA, role.CreatedBy);
        Assert.NotEqual(default, role.CreatedAt);

        var audit = await db.AuditEntries.ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, a => Assert.Equal(userA, a.CreatedBy));
        Assert.DoesNotContain(audit, a => a.Changes!.Contains("hash-secreto"));

        role.Name = "Ventas MX";
        await db.SaveChangesAsync();
        var change = await db.AuditEntries.SingleAsync(a => a.Action == "cambio");
        Assert.Contains("Ventas MX", change.Changes);
    }

    [Fact]
    public async Task Writing_into_another_company_throws()
    {
        using var db = Db(companyA, userA);
        db.Roles.Add(new Role { CompanyId = companyB, Name = "Intruso" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
