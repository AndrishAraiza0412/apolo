using System.Security.Claims;

namespace Apolo.Api;

public class CurrentUser
{
    public const string CompanyClaim = "company";

    public Guid? UserId { get; init; }
    public Guid? CompanyId { get; init; }

    public CurrentUser() { }

    public CurrentUser(IHttpContextAccessor http)
    {
        var p = http.HttpContext?.User;
        if (Guid.TryParse(p?.FindFirstValue(ClaimTypes.NameIdentifier), out var u)) UserId = u;
        if (Guid.TryParse(p?.FindFirstValue(CompanyClaim), out var c)) CompanyId = c;
    }
}
