using System.Text.Json.Nodes;
using Insolvia.Api.Configuration;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Api.Mail;
using Insolvia.Api.Waitlist;
using Insolvia.Core;
using Insolvia.Data.Waitlist;

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
        routes.MapPost("/v1/waitlist", SubmitAsync);
    }

    private static async Task<IResult> SubmitAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var submission = WaitlistSubmissions.Parse(await context.BodyAsync(MaxRequestBytes));
        var record = WaitlistSubmissions.Create(submission, context.Clock());
        var store = context.RequestServices.GetService<IWaitlistStore>()
            ?? throw new InvalidOperationException("waitlist store is not composed");
        await store.AddAsync(record, cancellationToken);

        // GLBA: only the server-generated id — never a submitted value.
        context.Log(Logger, "waitlist submission stored", ("waitlist_id", record.Id));
        return FlaskJson.Result(
            new JsonObject { ["id"] = record.Id, ["submittedAt"] = FlaskJson.Millis(record.SubmittedAt) },
            StatusCodes.Status201Created);
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
        routes.MapPost("/v1/unsubscribe", SubmitAsync);
    }

    private static async Task<IResult> SubmitAsync(HttpContext context, CancellationToken cancellationToken)
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
        return FlaskJson.Result(new JsonObject { ["status"] = "unsubscribed" }, StatusCodes.Status202Accepted);
    }
}
