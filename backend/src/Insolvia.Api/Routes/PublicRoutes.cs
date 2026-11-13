using System.ComponentModel.DataAnnotations;
using Insolvia.Api.Configuration;
using Insolvia.Api.Hosting;
using Insolvia.Api.Mail;
using Insolvia.Api.Waitlist;
using Insolvia.Data.Waitlist;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

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
        routes.MapPost("/v1/waitlist", SubmitAsync).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
    }

    /// <summary>Join the waitlist.</summary>
    /// <remarks>Unauthenticated: the marketing site calls it server to server.</remarks>
    internal static async Task<Created<WaitlistReceipt>> SubmitAsync(HttpContext context, WaitlistRequest request, CancellationToken cancellationToken)
    {
        var record = WaitlistSubmissions.Create(request.ToSubmission(), context.Clock());
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
        routes.MapPost("/v1/unsubscribe", SubmitAsync).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
    }

    /// <summary>Stop email to the address an unsubscribe link was made for.</summary>
    /// <remarks>
    /// Unauthenticated: the token is the authentication. An invalid token is a
    /// 400; a valid one always answers the same 202, whatever the address's state.
    /// </remarks>
    internal static async Task<Accepted<UnsubscribeReceipt>> SubmitAsync(HttpContext context, UnsubscribeRequest request, CancellationToken cancellationToken)
    {
        var token = request.Token!;
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
/// A waitlist sign-up (<c>POST /v1/waitlist</c>): the marketing form's caps,
/// and the DNS name limit for the host the marketing action sends. Unknown
/// keys are ignored — the form's honeypot is checked in its action and never
/// reaches this API.
/// </summary>
/// <param name="Name">The person's name.</param>
/// <param name="Firm">Their firm's name.</param>
/// <param name="Email">A work email.</param>
/// <param name="CurrentSoftware">What they use today.</param>
/// <param name="Message">Anything else.</param>
/// <param name="Host">The marketing host the form was sent from.</param>
public sealed record WaitlistRequest(
    [property: Required(ErrorMessage = "Please tell us your name."), MaxLength(200, ErrorMessage = "Please keep this under 200 characters.")] string? Name,
    [property: Required(ErrorMessage = "Please tell us your firm's name."), MaxLength(200, ErrorMessage = "Please keep this under 200 characters.")] string? Firm,
    [property: Required(ErrorMessage = "A work email is required."), MaxLength(320, ErrorMessage = "Please keep this under 320 characters."), RegularExpression(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", ErrorMessage = "That doesn't look like a valid email address.")] string? Email,
    [property: MaxLength(100, ErrorMessage = "Please keep this under 100 characters.")] string? CurrentSoftware = null,
    [property: MaxLength(2000, ErrorMessage = "Please keep this under 2000 characters.")] string? Message = null,
    [property: MaxLength(253, ErrorMessage = "Please keep this under 253 characters.")] string? Host = null)
{
    /// <summary>The submission to store; an absent optional field is stored empty.</summary>
    /// <returns>The submission.</returns>
    public WaitlistSubmission ToSubmission() => new(Name!, Firm!, Email!, CurrentSoftware ?? "", Message ?? "", Host ?? "");
}

/// <summary>A stored waitlist sign-up.</summary>
/// <param name="Id">The sign-up's server-generated id.</param>
/// <param name="SubmittedAt">When it was stored, ISO 8601 UTC with milliseconds.</param>
public sealed record WaitlistReceipt(string Id, string SubmittedAt);

/// <summary>An unsubscribe click (<c>POST /v1/unsubscribe</c>).</summary>
/// <param name="Token">The signed token from the email's unsubscribe link.</param>
public sealed record UnsubscribeRequest([property: Required(ErrorMessage = "token is required")] string? Token);

/// <summary>The one answer to a valid unsubscribe token.</summary>
/// <param name="Status">Always <c>unsubscribed</c>.</param>
public sealed record UnsubscribeReceipt(string Status);
