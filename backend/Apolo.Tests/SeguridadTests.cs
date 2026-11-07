using Apolo.Api;
using Apolo.Api.Data;

namespace Apolo.Tests;

public class SeguridadTests
{
    [Fact]
    public void Session_version_changes_with_password_role_modules_or_deactivation()
    {
        var u = new User { Email = "a@b.mx", Name = "A", PasswordHash = "h1", RoleId = Guid.NewGuid(), Role = new Role { Name = "R", Modules = ["ventas"] } };
        var v = Endpoints.SessionVersion(u);
        Assert.Equal(v, Endpoints.SessionVersion(u));
        foreach (var change in new Action[] { () => u.PasswordHash = "h2", () => u.Role.Modules.Add("compras"), () => u.RoleId = Guid.NewGuid(), () => u.Active = false })
        {
            var before = Endpoints.SessionVersion(u);
            change();
            Assert.NotEqual(before, Endpoints.SessionVersion(u));
        }
    }

    [Theory]
    [InlineData("corta1", false)]
    [InlineData("solamenteletras", false)]
    [InlineData("123456789012", false)]
    [InlineData("contrasena12345", true)]
    public void Password_policy_requires_length_letters_and_digits(string password, bool valid) =>
        Assert.Equal(valid, Endpoints.PasswordError(password) is null);
}
