using System.Security.Claims;

namespace Apolo.Api;

/// The authenticated user and company of the current request, read from the session claims. AppDb uses it to filter reads and stamp writes by company (DESIGN.md §9).
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
