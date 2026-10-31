using System.Text;
using Insolvia.Core;

namespace Insolvia.Api.Mail;

/// <summary>
/// The two site URLs every template's footer needs (Python's <c>MailLinks</c>).
/// Per-environment — staging mail links staging pages — and the unsubscribe
/// one per-recipient, so a template takes them rather than reading a constant.
/// A null <see cref="UnsubscribeUrl"/> is legal: the footer shows the privacy
/// link alone and the message carries no List-Unsubscribe headers.
/// </summary>
/// <param name="PrivacyUrl">The marketing site's privacy page.</param>
/// <param name="UnsubscribeUrl">The recipient's signed unsubscribe link, or null.</param>
public sealed record MailLinks(string PrivacyUrl, string? UnsubscribeUrl = null);

/// <summary>
/// The transactional email templates, ported from the Python backend's
/// <c>core/mail.py</c> byte for byte — the tests' <c>Fixtures/python-mail.json</c>
/// is that module's own output, and every part of every template is compared
/// against it.
/// </summary>
/// <remarks>
/// <para>Every category and the message class MUST match the mailer's
/// allow-list for <c>insolvia_api</c> (insolvia-services'
/// <c>infra/modules/mailer</c>): <c>welcome</c>, <c>email_verification</c>,
/// <c>password_reset</c>, <c>client_invitation</c>; <c>transactional</c>.</para>
/// <para>HTML bodies are email-client-safe: one document, inline styles only,
/// a table layout, and a table-wrapped <c>&lt;a&gt;</c> for the button.</para>
/// <para>Only <see cref="ClientInvitation"/> has a caller today (the portal
/// invitation); the other three wait for the signup/verify/reset routes.</para>
/// </remarks>
public static class MailTemplates
{
    private const string ColorBg = "#f5f2eb";
    private const string ColorCard = "#ffffff";
    private const string ColorInk = "#0b2a4a";
    private const string ColorMuted = "#6e7885";
    private const string ColorLine = "#e2dccf";
    private const string ColorAccent = "#8f6428";
    private const string ColorAccentText = "#ffffff";
    private const string FontHeading = "Georgia, 'Times New Roman', serif";
    private const string FontBody = "Arial, Helvetica, sans-serif";
    private const string MessageClass = "transactional";

    /// <summary><c>links_for</c>: the footer links for an environment's marketing origin.</summary>
    /// <param name="marketingOrigin">The marketing site's origin; a trailing slash is ignored.</param>
    /// <param name="unsubscribeUrl">The recipient's unsubscribe link, or null.</param>
    /// <returns>The links.</returns>
    public static MailLinks LinksFor(string marketingOrigin, string? unsubscribeUrl = null)
    {
        ArgumentNullException.ThrowIfNull(marketingOrigin);
        return new MailLinks(marketingOrigin.TrimEnd('/') + "/privacy", unsubscribeUrl);
    }

    /// <summary>Category <c>welcome</c> — sent once, right after signup.</summary>
    /// <param name="toAddress">The recipient.</param>
    /// <param name="links">The footer links.</param>
    /// <param name="appUrl">Where the button goes.</param>
    /// <param name="recipientName">The greeting's name, or null for "Hello,".</param>
    /// <returns>The message.</returns>
    public static OutboundEmail Welcome(string toAddress, MailLinks links, string appUrl, string? recipientName = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        var greeting = Greeting(recipientName);
        const string intro = "Insolvia helps your firm prepare and e-file consumer bankruptcy cases faster, with fewer errors.";
        var bodyHtml =
            $"<p style=\"margin:0 0 16px 0;\">{Escape(greeting)}</p>"
            + $"<p style=\"margin:0 0 16px 0;\">{Escape(intro)}</p>"
            + Button("Open Insolvia", appUrl);
        var html = Document("Welcome to Insolvia.", "Welcome to Insolvia", bodyHtml, links);
        var text = $"{greeting}\n\n{intro}\n\nOpen Insolvia: {appUrl}{TextFooter(links)}";
        return new OutboundEmail("welcome", MessageClass, toAddress, "Welcome to Insolvia", html, text, links.UnsubscribeUrl);
    }

    /// <summary>Category <c>email_verification</c> — confirms a new account's address.</summary>
    /// <param name="toAddress">The recipient.</param>
    /// <param name="links">The footer links.</param>
    /// <param name="verificationUrl">The verification link.</param>
    /// <param name="recipientName">The greeting's name, or null.</param>
    /// <returns>The message.</returns>
    public static OutboundEmail EmailVerification(string toAddress, MailLinks links, string verificationUrl, string? recipientName = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        var greeting = Greeting(recipientName);
        const string intro = "Please confirm your email address to activate your Insolvia account.";
        const string expiryNote = "This link expires soon. If you didn't create an Insolvia account, you can safely ignore this email.";
        var bodyHtml =
            $"<p style=\"margin:0 0 16px 0;\">{Escape(greeting)}</p>"
            + $"<p style=\"margin:0 0 16px 0;\">{Escape(intro)}</p>"
            + Button("Verify email", verificationUrl)
            + PasteLink(verificationUrl)
            + $"<p style=\"margin:16px 0 0 0;font-size:13px;color:{ColorMuted};\">{Escape(expiryNote)}</p>";
        var html = Document("Confirm your email address to activate your account.", "Verify your email address", bodyHtml, links);
        var text = $"{greeting}\n\n{intro}\n\nVerify email: {verificationUrl}\n\n{expiryNote}{TextFooter(links)}";
        return new OutboundEmail("email_verification", MessageClass, toAddress, "Verify your email address", html, text, links.UnsubscribeUrl);
    }

    /// <summary>Category <c>password_reset</c> — sent when a reset is requested.</summary>
    /// <param name="toAddress">The recipient.</param>
    /// <param name="links">The footer links.</param>
    /// <param name="resetUrl">The reset link.</param>
    /// <param name="recipientName">The greeting's name, or null.</param>
    /// <returns>The message.</returns>
    public static OutboundEmail PasswordReset(string toAddress, MailLinks links, string resetUrl, string? recipientName = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        var greeting = Greeting(recipientName);
        const string intro = "We received a request to reset the password for your Insolvia account.";
        const string securityNote = "If you didn't request this, you can safely ignore this email and your password will stay unchanged.";
        var bodyHtml =
            $"<p style=\"margin:0 0 16px 0;\">{Escape(greeting)}</p>"
            + $"<p style=\"margin:0 0 16px 0;\">{Escape(intro)}</p>"
            + Button("Reset password", resetUrl)
            + PasteLink(resetUrl)
            + $"<p style=\"margin:16px 0 0 0;font-size:13px;color:{ColorMuted};\">{Escape(securityNote)}</p>";
        var html = Document("Reset the password for your Insolvia account.", "Reset your Insolvia password", bodyHtml, links);
        var text = $"{greeting}\n\n{intro}\n\nReset password: {resetUrl}\n\n{securityNote}{TextFooter(links)}";
        return new OutboundEmail("password_reset", MessageClass, toAddress, "Reset your Insolvia password", html, text, links.UnsubscribeUrl);
    }

    /// <summary>
    /// Category <c>client_invitation</c> — the CONTEXT half of a portal
    /// invitation (ADR 0023: two messages, one purpose each). Cognito's own
    /// mail carries the temporary password, which nothing here sees; this one
    /// says which firm is asking, what the portal is for and where to sign in.
    /// IT CARRIES NO SECRET: a forwarded copy admits nobody, and the link is
    /// the portal's front door, the same for every client.
    /// </summary>
    /// <param name="toAddress">The client's address.</param>
    /// <param name="links">The footer links.</param>
    /// <param name="firmName">The inviting firm's name.</param>
    /// <param name="portalUrl">The portal's sign-in URL.</param>
    /// <param name="recipientName">The name the firm gave, or null.</param>
    /// <returns>The message.</returns>
    public static OutboundEmail ClientInvitation(string toAddress, MailLinks links, string firmName, string portalUrl, string? recipientName = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        var greeting = Greeting(recipientName);
        var intro = $"{firmName} has invited you to their client portal on Insolvia, where you can answer questions about your bankruptcy case and send the documents they ask for.";
        const string passwordNote = "You will receive a separate email from Insolvia with your sign-in name and a temporary password. If you have signed in to Insolvia before, use the password you already chose.";
        var safetyNote = $"Nobody from {firmName} or Insolvia will ever ask you for your password. If you were not expecting this, contact your law firm directly.";
        var bodyHtml =
            $"<p style=\"margin:0 0 16px 0;\">{Escape(greeting)}</p>"
            + $"<p style=\"margin:0 0 16px 0;\">{Escape(intro)}</p>"
            + $"<p style=\"margin:0 0 16px 0;\">{Escape(passwordNote)}</p>"
            + Button("Open the client portal", portalUrl)
            + $"<p style=\"margin:16px 0 0 0;font-size:13px;color:{ColorMuted};\">{Escape(safetyNote)}</p>";
        var html = Document($"{firmName} invited you to their client portal.", "You're invited to your client portal", bodyHtml, links);
        var text = $"{greeting}\n\n{intro}\n\n{passwordNote}\n\nClient portal: {portalUrl}\n\n{safetyNote}{TextFooter(links)}";
        return new OutboundEmail("client_invitation", MessageClass, toAddress, $"{firmName} invited you to your client portal", html, text, links.UnsubscribeUrl);
    }

    /// <summary>
    /// Python's <c>html.escape(s, quote=True)</c>: <c>&amp;</c> first, then
    /// <c>&lt; &gt; " '</c> — the apostrophe as <c>&amp;#x27;</c>.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The escaped text.</returns>
    internal static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#x27;",
                _ => character.ToString(),
            });
        }

        return builder.ToString();
    }

    private static string Greeting(string? recipientName) =>
        recipientName is not null && TextRules.Strip(recipientName).Length > 0
            ? $"Hi {TextRules.Strip(recipientName)},"
            : "Hello,";

    private static string PasteLink(string url) =>
        $"<p style=\"margin:16px 0 0 0;font-size:13px;color:{ColorMuted};\">"
        + "or paste this link into your browser:<br>"
        + $"<a href=\"{Escape(url)}\" style=\"color:{ColorAccent};word-break:break-all;\">{Escape(url)}</a></p>";

    // The shared branded shell: one nested-table layout, inline styles only.
    private static string Document(string preheader, string heading, string bodyHtml, MailLinks links) =>
        "<!doctype html>"
        + "<html lang=\"en\">"
        + "<head>"
        + "<meta charset=\"utf-8\">"
        + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        + $"<title>{Escape(heading)}</title>"
        + "</head>"
        + $"<body style=\"margin:0;padding:0;background-color:{ColorBg};font-family:{FontBody};\">"
        + "<div style=\"display:none;max-height:0;overflow:hidden;opacity:0;\">"
        + $"{Escape(preheader)}</div>"
        + "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" "
        + $"border=\"0\" style=\"background-color:{ColorBg};padding:32px 16px;\">"
        + "<tr><td align=\"center\">"
        + "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" "
        + $"border=\"0\" style=\"max-width:560px;background-color:{ColorCard};"
        + $"border:1px solid {ColorLine};border-radius:8px;\">"
        + "<tr><td style=\"padding:32px 32px 8px 32px;\">"
        + $"<span style=\"font-family:{FontHeading};font-size:24px;"
        + $"color:{ColorInk};font-weight:bold;\">Insolvia</span>"
        + "</td></tr>"
        + "<tr><td style=\"padding:16px 32px 32px 32px;\">"
        + $"<h1 style=\"font-family:{FontHeading};font-size:20px;"
        + $"color:{ColorInk};margin:0 0 16px 0;\">{Escape(heading)}</h1>"
        + $"<div style=\"font-family:{FontBody};font-size:15px;line-height:1.6;"
        + $"color:{ColorInk};\">{bodyHtml}</div>"
        + "</td></tr>"
        + "<tr><td style=\"padding:24px 32px 32px 32px;"
        + $"border-top:1px solid {ColorLine};\">"
        + $"<p style=\"font-family:{FontBody};font-size:12px;color:{ColorMuted};"
        + "margin:0 0 4px 0;\">Insolvia &mdash; bankruptcy case preparation &amp; "
        + "e-filing</p>"
        + $"<p style=\"font-family:{FontBody};font-size:12px;color:{ColorMuted};"
        + "margin:0 0 4px 0;\">This is a transactional message about your "
        + "Insolvia account.</p>"
        + $"<p style=\"font-family:{FontBody};font-size:12px;color:{ColorMuted};"
        + $"margin:0;\"><a href=\"{Escape(links.PrivacyUrl)}\" "
        + $"style=\"color:{ColorMuted};\">Privacy policy</a>"
        + (links.UnsubscribeUrl is { Length: > 0 } unsubscribe
            ? $"&nbsp;&middot;&nbsp;<a href=\"{Escape(unsubscribe)}\" style=\"color:{ColorMuted};\">Unsubscribe</a>"
            : string.Empty)
        + "</p>"
        + "</td></tr>"
        + "</table>"
        + "</td></tr>"
        + "</table>"
        + "</body></html>";

    // A bulletproof button: a table-wrapped <a>, styled inline.
    private static string Button(string label, string url) =>
        "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" "
        + "style=\"margin:24px 0;\">"
        + "<tr><td>"
        + $"<a href=\"{Escape(url)}\" target=\"_blank\" "
        + $"style=\"display:inline-block;background-color:{ColorAccent};"
        + $"color:{ColorAccentText};font-family:{FontBody};font-size:15px;"
        + "font-weight:bold;text-decoration:none;padding:14px 28px;"
        + $"border-radius:6px;\">{Escape(label)}</a>"
        + "</td></tr>"
        + "</table>";

    private static string TextFooter(MailLinks links)
    {
        var footer =
            "\n\n--\n"
            + "Insolvia -- bankruptcy case preparation & e-filing\n"
            + "This is a transactional message about your Insolvia account.\n"
            + $"Privacy policy: {links.PrivacyUrl}\n";
        if (links.UnsubscribeUrl is { Length: > 0 })
        {
            footer += $"Unsubscribe: {links.UnsubscribeUrl}\n";
        }

        return footer;
    }
}
