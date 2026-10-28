using System.Collections.Concurrent;
using Insolvia.Api.Auth;
using Insolvia.Api.Hosting;
using Insolvia.Api.Mail;
using Insolvia.Core;
using Insolvia.Core.Clients;
using Insolvia.Core.Firms;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Data.Waitlist;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Insolvia.Api.Tests.Support;

/// <summary>
/// The API with auth composed — the C# of test_auth.py's <c>make_client</c>
/// and test_portal_routes.py's <c>build</c>: the real Program pipeline, the
/// static JWKS provider serving <see cref="Tokens.PublicKey"/>, seeded
/// in-memory stores, a log capture, and the test-only routes of
/// <see cref="ProbeRoutes"/> (they exist in this assembly only).
/// </summary>
internal sealed class AuthHost : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthHost(
        WebApplicationFactory<Program> factory,
        IReadOnlyDictionary<string, string?>? settings = null,
        bool composeJwks = true,
        bool composeFirmStore = true,
        Action<IEndpointRouteBuilder>? extraRoutes = null,
        Action<IServiceCollection>? services = null)
    {
        var effective = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["INSOLVIA_ENV"] = "local",
            ["AUTH_ISSUER_URL"] = Tokens.Issuer,
            ["AUTH_CLIENT_ID"] = Tokens.StaffClientId,
            ["AUTH_PORTAL_CLIENT_ID"] = Tokens.PortalClientId,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            effective[key] = value;
        }

        _factory = factory.WithWebHostBuilder(host =>
        {
            foreach (var (key, value) in effective)
            {
                host.UseSetting(key, value);
            }

            host.ConfigureTestServices(collection => Compose(collection, composeJwks, composeFirmStore, extraRoutes, services));
        });
    }

    private void Compose(
        IServiceCollection services,
        bool composeJwks,
        bool composeFirmStore,
        Action<IEndpointRouteBuilder>? extraRoutes,
        Action<IServiceCollection>? configure)
    {
        services.RemoveAll<IJwksProvider>();
        if (composeJwks)
        {
            services.AddSingleton<IJwksProvider>(new StaticJwksProvider(new Dictionary<string, Microsoft.IdentityModel.Tokens.SecurityKey> { [Tokens.Kid] = Tokens.PublicKey }));
        }

        services.RemoveAll<IFirmStore>();
        if (composeFirmStore)
        {
            services.AddSingleton<IFirmStore>(Firms);
        }

        services.RemoveAll<IClientBindingStore>();
        services.AddSingleton<IClientBindingStore>(Bindings);
        services.RemoveAll<IAccessLog>();
        services.AddSingleton<IAccessLog>(AccessLog);

        // The firm-side routes' ports (18.14): the Python fixtures' memory adapters.
        services.RemoveAll<IUserDirectory>();
        services.AddSingleton<IUserDirectory>(Directory);
        services.RemoveAll<IMailer>();
        services.AddSingleton<IMailer>(Mailer);
        services.RemoveAll<IWaitlistStore>();
        services.AddSingleton<IWaitlistStore>(Waitlist);
        services.RemoveAll<ICourtRegistry>();
        services.AddSingleton<ICourtRegistry>(Courts);
        services.RemoveAll<IDebtorClientLinks>();
        services.AddSingleton<IDebtorClientLinks>(DebtorLinks);

        services.AddSingleton<ILoggerProvider>(Logs);
        services.AddSingleton<IEndpointModule>(new ProbeRoutes(extraRoutes));
        configure?.Invoke(services);
    }

    public MemoryFirmStore Firms { get; } = new();

    public MemoryClientBindingStore Bindings { get; } = new();

    public MemoryAccessLog AccessLog { get; } = new();

    public LogCapture Logs { get; } = new();

    public MemoryUserDirectory Directory { get; } = new();

    public MemoryMailer Mailer { get; } = new();

    public MemoryWaitlistStore Waitlist { get; } = new();

    public FakeCourtRegistry Courts { get; } = new();

    public FakeDebtorClientLinks DebtorLinks { get; } = new();

    public IServiceProvider Services => _factory.Services;

    public HttpClient CreateClient() => _factory.CreateClient();

    /// <summary>The cast: two firms (B suspended), staff with each shape of standing, and portal clients.</summary>
    public async Task<AuthHost> SeedAsync()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await Firms.CreateFirmAsync(new Firm(Ids.FirmA, "Example & Partners", FirmStatus.Active, at, at));
        await Firms.CreateFirmAsync(new Firm(Ids.FirmB, "Other Firm LLP", FirmStatus.Suspended, at, at));

        FirmUser Member(string subject, string firm, FirmRole role, bool admin = false, FirmUserStatus status = FirmUserStatus.Active) =>
            new(firm, subject, subject[^4..] + "@example.test", "Person", subject[^4..], role, admin, AccessAllCases: false, Permissions.DefaultFor(role), status, at, at);

        await Firms.AddUserAsync(Member(Ids.Alice, Ids.FirmA, FirmRole.Attorney, admin: true));
        await Firms.AddUserAsync(Member(Ids.Bob, Ids.FirmA, FirmRole.Paralegal));
        await Firms.AddUserAsync(Member(Ids.Erin, Ids.FirmA, FirmRole.Staff));
        await Firms.AddUserAsync(Member(Ids.Dana, Ids.FirmA, FirmRole.Paralegal, status: FirmUserStatus.Disabled));
        await Firms.AddUserAsync(Member(Ids.Sam, Ids.FirmB, FirmRole.Attorney, admin: true));

        ClientBinding Binding(string subject, string firm, string caseId, BindingStatus status, ClientRole role) =>
            new(firm, caseId, subject, subject[^4..] + "@example.test", "Pat Example", [role], status, Ids.Alice, at, at);

        await Bindings.BindAsync(Binding(Ids.Pat, Ids.FirmA, Ids.Case, BindingStatus.Invited, ClientRole.Debtor1), []);
        await Bindings.BindAsync(Binding(Ids.Quinn, Ids.FirmA, Ids.OtherCase, BindingStatus.Revoked, ClientRole.Debtor1), []);
        await Bindings.BindAsync(Binding(Ids.Ray, Ids.FirmB, Ids.FirmBCase, BindingStatus.Active, ClientRole.Debtor1), []);
        return this;
    }

    public void Dispose() => _factory.Dispose();
}

/// <summary>
/// Routes that exist only in this test assembly, to drive the whole 401/403
/// matrix through the production filters and pipeline. Production has no
/// protected route yet (18.14 / 18.15 bring them).
/// </summary>
internal sealed class ProbeRoutes(Action<IEndpointRouteBuilder>? extra) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder routes)
    {
        // RequireAuth only — "who are you", nothing about a firm.
        routes.MapGet("/test/staff", (HttpContext context) => Results.Json(new { subject = context.CurrentPrincipal().Subject }))
            .RequireAuth();

        // The /v1/me shape: authenticated, firm optional and REPORTED.
        routes.MapGet("/test/staff/me", async (HttpContext context) =>
            {
                var accessor = await context.ResolveAccessorAsync();
                return Results.Json(new { subject = context.CurrentPrincipal().Subject, firm = accessor?.FirmId });
            })
            .RequireAuth();

        // An ordinary staff route: the firm is required.
        routes.MapGet("/test/staff/firm", async (HttpContext context) => Results.Json(new { firm = (await context.CurrentAccessorAsync()).FirmId }))
            .RequireAuth();

        // Authenticated and permitted.
        routes.MapPost("/test/staff/cases", (HttpContext context) => Results.Json(new { ok = true }))
            .RequireAuth()
            .Requires(Feature.Cases, PermissionLevel.AddEdit);
        routes.MapGet("/test/staff/portal-admin", (HttpContext context) => Results.Json(new { ok = true }))
            .RequireAuth()
            .Requires(Feature.ClientPortal, PermissionLevel.ViewOnly);

        // The decorator order reversed: the permission check reads a
        // principal nobody has verified yet — a programming error, a 500.
        routes.MapGet("/test/order/requires-first", (HttpContext context) => Results.Json(new { ok = true }))
            .Requires(Feature.Cases, PermissionLevel.ViewOnly)
            .RequireAuth();

        // The portal: one filter, both halves.
        routes.MapGet("/test/portal", (HttpContext context) => Results.Json(new { caseId = context.CurrentClient().CaseId, status = context.CurrentClient().Binding.Status.ToWire() }))
            .RequireClient();
        routes.MapPost("/test/portal", (HttpContext context) => Results.Json(new { caseId = context.CurrentClient().CaseId }))
            .RequireClient();

        extra?.Invoke(routes);
    }
}

/// <summary>Every log entry the host wrote, with its structured fields.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => [.. _entries];

    public IReadOnlyList<Entry> In(string category) => [.. _entries.Where(entry => entry.Category == category)];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    internal sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Fields, Exception? Exception)
    {
        /// <summary>Everything an operator would see of this entry, as one string.</summary>
        public string Rendered => Message + " " + string.Join(" ", Fields.Select(pair => pair.Key + "=" + pair.Value)) + " " + Exception;
    }

    private sealed class Logger(string category, ConcurrentQueue<Entry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    fields[key] = value;
                }
            }

            entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), fields, exception));
        }
    }
}
