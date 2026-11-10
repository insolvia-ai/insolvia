using System.Text.Json.Serialization;
using Insolvia.Api.Configuration;
using Insolvia.Api.Hosting;
using Insolvia.Api.Mail;
using Insolvia.Api.Waitlist;
using Insolvia.Data.Waitlist;
using Insolvia.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Insolvia.Api.Routes;

/// <summary>
/// <c>POST /v1/waitlist</c> (services/api routes/waitlist.py). Deliberately
/// UNAUTHENTICATED: the marketing SSR Lambda calls it server-to-server
/// (ADR 0001). Abuse control is the gateway's throttling and the form's
/// honeypot, which the marketing action checks and never forwards.
/// </summary>
public sealed class WaitlistRoutes : IEndpointModule
{
    // Generous for a six-field form; refuses junk before parsing it.
    private const int MaxRequestBytes = 64 * 1024;
    private const string Logger = "insolvia_api.api.routes.waitlist";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/v1/waitlist", SubmitAsync).RequestBody<WaitlistRequest>();
    }

    /// <summary>Join the waitlist.</summary>
    /// <remarks>Unauthenticated: the marketing site calls it server to server.</remarks>
    internal static async Task<Created<WaitlistReceipt>> SubmitAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var submission = WaitlistSubmissions.Parse(await context.BodyAsync(MaxRequestBytes));
        var record = WaitlistSubmissions.Create(submission, context.Clock());
        var store = context.RequestServices.GetService<IWaitlistStore>()
            ?? throw new InvalidOperationException("waitlist store is not composed");
        await store.AddAsync(record, cancellationToken);

        // GLBA: only the server-generated id — never a submitted value.
        context.Log(Logger, "waitlist submission stored", ("waitlist_id", record.Id));
        return TypedResults.Created((string?)null, new WaitlistReceipt(record.Id, WireFormat.Millis(record.SubmittedAt)));
    }
}

/// <summary>
/// <c>POST /v1/unsubscribe</c> (issue #80; services/api routes/unsubscribe.py).
/// Unauthenticated in the AWS sense, like the waitlist: the marketing site
/// forwards a click, and the person clicking holds no credentials. THE TOKEN
/// IS THE AUTHENTICATION (<see cref="UnsubscribeTokens"/>).
/// </summary>
/// <remarks>
/// An invalid token is a 400 and suppresses nothing; no configured secret is
/// a 500 (without a key no token can be told from a forgery, and 200 would be
/// a lie); a valid one is ALWAYS the same 202 body — it never reveals the
/// address, whether it was already suppressed, or whether it has an account.
/// </remarks>
public sealed class UnsubscribeRoutes : IEndpointModule
{
    // A token is ~120 characters; room for a long address, not for a body worth parsing.
    private const int MaxRequestBytes = 4 * 1024;
    private const string Logger = "insolvia_api.api.routes.unsubscribe";
    private const string Reason = "unsubscribe";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/v1/unsubscribe", SubmitAsync).RequestBody<UnsubscribeRequest>();
    }

    /// <summary>Stop email to the address an unsubscribe link was made for.</summary>
    /// <remarks>
    /// Unauthenticated: the token is the authentication. An invalid token is a
    /// 400; a valid one always answers the same 202, whatever the address's state.
    /// </remarks>
    internal static async Task<Accepted<UnsubscribeReceipt>> SubmitAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var payload = await context.BodyAsync(MaxRequestBytes);
        var token = Json.AsString(payload["token"]) ?? throw new ValidationException("token is required");

        var config = context.RequestServices.GetRequiredService<ApiConfig>();
        var address = UnsubscribeTokens.Verify(token, config.UnsubscribeSecret);
        var mailer = context.RequestServices.GetService<IMailer>()
            ?? throw new InvalidOperationException("mailer is not composed");
        await mailer.SuppressAsync(address, Reason, cancellationToken);

        // The address is PII and stays out of the log line.
        context.Log(Logger, "unsubscribe honoured");
        return TypedResults.Accepted((string?)null, new UnsubscribeReceipt("unsubscribed"));
    }
}

/// <summary>
/// A waitlist sign-up (<c>POST /v1/waitlist</c>). Parsed by
/// <c>WaitlistSubmissions.Parse</c>, which owns validation: values are
/// trimmed, a non-string counts as missing, and unknown keys are ignored.
/// </summary>
/// <param name="Name">The person's name (required, up to 200 characters).</param>
/// <param name="Firm">Their firm's name (required, up to 200 characters).</param>
/// <param name="Email">A work email (required, up to 320 characters).</param>
/// <param name="CurrentSoftware">What they use today (up to 100 characters).</param>
/// <param name="Message">Anything else (up to 2000 characters).</param>
/// <param name="Host">The marketing host the form was sent from (up to 253 characters).</param>
public sealed record WaitlistRequest(
    string Name,
    string Firm,
    string Email,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CurrentSoftware,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Host);

/// <summary>A stored waitlist sign-up.</summary>
/// <param name="Id">The sign-up's server-generated id.</param>
/// <param name="SubmittedAt">When it was stored, ISO 8601 UTC with milliseconds.</param>
public sealed record WaitlistReceipt(string Id, string SubmittedAt);

/// <summary>An unsubscribe click (<c>POST /v1/unsubscribe</c>).</summary>
/// <param name="Token">The signed token from the email's unsubscribe link.</param>
public sealed record UnsubscribeRequest(string Token);

/// <summary>The one answer to a valid unsubscribe token.</summary>
/// <param name="Status">Always <c>unsubscribed</c>.</param>
public sealed record UnsubscribeReceipt(string Status);
