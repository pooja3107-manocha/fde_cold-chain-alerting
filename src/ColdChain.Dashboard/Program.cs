using System.Security.Claims;
using System.Text.Json.Serialization;
using ColdChain.Core.Domain;
using ColdChain.Dashboard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<UserDirectory>();
builder.Services.AddSingleton<IFleetDataSource>(_ => new SeedReplaySource(FindRepoRoot()));
builder.Services.AddAuthorization();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "coldchain.ops";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);   // one shift
        o.SlidingExpiration = true;
        // This is an API for a single-page app: answer with status codes, never redirect to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// ------------------------------------------------------------------------------------------- auth

app.MapPost("/api/login", async (LoginRequest req, UserDirectory users, HttpContext http) =>
{
    var user = users.Validate(req.Username, req.Password);
    if (user is null) return Results.Unauthorized();
    var principal = UserDirectory.ToPrincipal(user, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.Ok(Me(principal));
});

app.MapPost("/api/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/me", (ClaimsPrincipal user) => Me(user));

// ------------------------------------------------------------------------------------------- fleet

api.MapGet("/fleet", (ClaimsPrincipal user, IFleetDataSource data) =>
{
    var s = data.Current;
    var visible = s.Trucks.Values.Where(t => user.CanSee(t.Summary.TruckId)).ToList();
    return new
    {
        s.Source,
        s.DataStart,
        s.DataEnd,
        Trucks = visible.Select(t => t.Summary),
        Alerts = visible.SelectMany(t => t.Incidents).OrderByDescending(i => i.AlertedAt)
    };
});

// Filterable alert feed, for other consumers (a mobile app, a ticketing integration) as well as the UI.
api.MapGet("/alerts", (ClaimsPrincipal user, IFleetDataSource data, string? truck, IncidentStatus? status) =>
    data.Current.Trucks.Values
        .Where(t => user.CanSee(t.Summary.TruckId))
        .Where(t => truck is null || t.Summary.TruckId.Contains(truck, StringComparison.OrdinalIgnoreCase))
        .SelectMany(t => t.Incidents)
        .Where(i => status is null || i.Status == status)
        .OrderByDescending(i => i.AlertedAt));

api.MapGet("/trucks/{truckId}", (string truckId, ClaimsPrincipal user, IFleetDataSource data) =>
    !data.Current.Trucks.TryGetValue(truckId, out var detail) ? Results.NotFound()
    : !user.CanSee(truckId) ? Results.Forbid()
    : Results.Ok(detail));

// ------------------------------------------------------------------------------------------- UI

if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "", "index.html")))
    app.MapFallbackToFile("index.html");
else
    app.MapGet("/", () => Results.Text(
        "The dashboard UI has not been built yet. Run:\n\n  cd ui-src\n  npm install\n  npm run build\n\nthen restart this app. " +
        "For UI development, run 'npm run dev' in ui-src and open http://localhost:5173.", "text/plain"));

app.Run();

static object Me(ClaimsPrincipal user) => new
{
    Username = user.Identity?.Name,
    DisplayName = user.FindFirst("display_name")?.Value ?? user.Identity?.Name,
    AllTrucks = user.SeesAll(),
    Trucks = user.FindAll(UserDirectory.TruckClaim).Select(c => c.Value).Where(v => v != UserDirectory.AllTrucks)
};

static string FindRepoRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "config", "cargo_profiles.json"))) return dir.FullName;
    }
    throw new DirectoryNotFoundException("Could not find config/cargo_profiles.json. Run from the repository folder.");
}

record LoginRequest(string? Username, string? Password);
