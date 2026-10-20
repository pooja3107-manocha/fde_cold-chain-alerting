using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace ColdChain.Dashboard;

public sealed record DashboardUser
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string DisplayName { get; init; } = "";
    // No default here: the config binder appends to an existing list, so a default "*" would leak into every user.
    // A user with no trucks listed sees none.
    public List<string> Trucks { get; init; } = [];
}

/// <summary>
/// Demo user list from appsettings.json. The rest of the app only sees the claims this produces,
/// so swapping in Entra ID later means producing the same "truck" claims from group membership.
/// </summary>
public sealed class UserDirectory(IConfiguration config)
{
    public const string TruckClaim = "truck";
    public const string AllTrucks = "*";

    private readonly List<DashboardUser> _users = config.GetSection("Dashboard:Users").Get<List<DashboardUser>>() ?? [];

    public DashboardUser? Validate(string? username, string? password)
    {
        var user = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        if (user is null || password is null) return null;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(user.Password), Encoding.UTF8.GetBytes(password))
            ? user : null;
    }

    public static ClaimsPrincipal ToPrincipal(DashboardUser user, string scheme)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new("display_name", string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName)
        };
        claims.AddRange(user.Trucks.Select(t => new Claim(TruckClaim, t)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }
}

public static class TruckAccess
{
    public static bool SeesAll(this ClaimsPrincipal user) => user.FindAll(UserDirectory.TruckClaim).Any(c => c.Value == UserDirectory.AllTrucks);

    public static bool CanSee(this ClaimsPrincipal user, string truckId) =>
        user.FindAll(UserDirectory.TruckClaim).Any(c => c.Value == UserDirectory.AllTrucks ||
                                                       string.Equals(c.Value, truckId, StringComparison.OrdinalIgnoreCase));
}
