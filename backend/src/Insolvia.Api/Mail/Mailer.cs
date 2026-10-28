using Insolvia.Aws.Mailer;

namespace Insolvia.Api.Mail;

/// <summary>
/// Everything a <see cref="IMailer.SendAsync"/> needs beyond the idempotency
/// key (Python's <c>OutboundEmail</c>, core/mail.py). The templates that build
/// one arrive with the portal invitation (18.18); no route in this host sends
/// mail yet.
/// </summary>
/// <param name="Category">One of the mailer's allow-listed categories for <c>insolvia_api</c>.</param>
/// <param name="MessageClass">Always <c>transactional</c> today.</param>
/// <param name="ToAddress">The recipient.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="HtmlBody">The HTML part.</param>
/// <param name="TextBody">The plain-text part.</param>
/// <param name="ListUnsubscribeUrl">The footer's unsubscribe link, passed on as RFC 8058 headers; null for none.</param>
public sealed record OutboundEmail(
    string Category,
    string MessageClass,
    string ToAddress,
    string Subject,
    string HtmlBody,
    string TextBody,
    string? ListUnsubscribeUrl = null);

/// <summary>
/// Sends transactional mail through the mailer service, and suppresses an
/// address (Python's <c>Mailer</c> port, core/ports.py).
/// </summary>
/// <remarks>
/// It belongs in Insolvia.Core.Ports; it is here because Core was closed to
/// this change (#525). When it moves, <see cref="SigV4MailerClient"/>
/// implements it directly and <see cref="SigV4Mailer"/> goes away.
/// </remarks>
public interface IMailer
{
    /// <summary>
    /// Send <paramref name="email"/>. <paramref name="idempotencyKey"/>
    /// becomes the mailer's <c>application_message_id</c>, so a retried send
    /// is deduplicated there rather than delivered twice.
    /// </summary>
    /// <param name="email">The message.</param>
    /// <param name="idempotencyKey">Stable across retries of one logical send.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the mailer accepted it.</returns>
    Task SendAsync(OutboundEmail email, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop sending to <paramref name="address"/> (issue #80). Idempotent. The
    /// port takes no proof of ownership — the unsubscribe route verifies the
    /// link's HMAC BEFORE calling it; calling it without that would be a
    /// suppress-anyone button.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="reason">Why, e.g. <c>unsubscribe</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the mailer accepted it.</returns>
    Task SuppressAsync(string address, string reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// The in-memory <see cref="IMailer"/> (Python's <c>InMemoryMailerClient</c>):
/// tests, and a developer's machine with no <c>MAILER_API_URL</c>. It sends
/// nothing; it records what would have been sent.
/// </summary>
public sealed class MemoryMailer : IMailer
{
    private readonly Lock _gate = new();
    private readonly List<(OutboundEmail Email, string IdempotencyKey)> _sent = [];
    private readonly List<(string Address, string Reason)> _suppressed = [];

    /// <summary>Every send, in order.</summary>
    public IReadOnlyList<(OutboundEmail Email, string IdempotencyKey)> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>Every suppression, in order.</summary>
    public IReadOnlyList<(string Address, string Reason)> Suppressed
    {
        get
        {
            lock (_gate)
            {
                return [.. _suppressed];
            }
        }
    }

    /// <inheritdoc />
    public Task SendAsync(OutboundEmail email, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        lock (_gate)
        {
            _sent.Add((email, idempotencyKey));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SuppressAsync(string address, string reason, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _suppressed.Add((address, reason));
        }

        return Task.CompletedTask;
    }
}

/// <summary>The deployed <see cref="IMailer"/>: the port's calls handed to <see cref="SigV4MailerClient"/>.</summary>
/// <param name="client">The signed HTTP client.</param>
public sealed class SigV4Mailer(SigV4MailerClient client) : IMailer
{
    /// <inheritdoc />
    public Task SendAsync(OutboundEmail email, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        return client.SendAsync(
            new MailerMessage(email.Category, email.MessageClass, email.ToAddress, email.Subject, email.HtmlBody, email.TextBody, email.ListUnsubscribeUrl),
            idempotencyKey,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task SuppressAsync(string address, string reason, CancellationToken cancellationToken = default) =>
        client.SuppressAsync(address, reason, cancellationToken);
}
