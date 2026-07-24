"""Transactional email templates (issue 6.6).

Pure — no framework or AWS imports; this module must satisfy the same
dependency-direction rule as every other `core/` module (see
tests/test_architecture.py). It builds an `OutboundEmail` value object; the
`Mailer` port in core/ports.py is what actually sends it (implemented by
adapters/aws/mailer_client.py's SigV4MailerClient in production and
adapters/memory/mailer_client.py's InMemoryMailerClient in tests / the plain
development server).

Every category name and the message_class here MUST match the mailer's
service-registry allowlist for insolvia_api exactly (allowed_categories =
["welcome", "email_verification", "password_reset"], allowed_message_classes =
["transactional"] — see infra/modules/mailer/main.tf local.insolvia_api_service)
or the mailer rejects the send with a 4xx.

Trigger points for a LATER milestone (auth flows, not this one — see the
milestone-7 PR description): once signup/verify/reset HTTP routes exist, they
call `welcome_email` / `email_verification_email` / `password_reset_email`
below and hand the result to `ApiDependencies.mailer.send(...)`. No route
calls these yet; this PR only proves the send capability end-to-end via
tests.

HTML bodies are deliberately email-client-safe: one self-contained document,
inline styles only (no <style> blocks, no external CSS/fonts/images), a
table-based layout, and a bulletproof button (a styled <a>, not <button>).
"""

from __future__ import annotations

from dataclasses import dataclass
from html import escape

# --- brand -------------------------------------------------------------

_COLOR_BG = "#f5f2eb"
_COLOR_CARD = "#ffffff"
_COLOR_INK = "#0b2a4a"
_COLOR_MUTED = "#6e7885"
_COLOR_LINE = "#e2dccf"
_COLOR_ACCENT = "#8f6428"
_COLOR_ACCENT_TEXT = "#ffffff"

_FONT_HEADING = "Georgia, 'Times New Roman', serif"
_FONT_BODY = "Arial, Helvetica, sans-serif"

_PRIVACY_URL = "https://www.insolvia.ai/privacy"

_MESSAGE_CLASS = "transactional"


@dataclass(frozen=True)
class OutboundEmail:
    """Everything a Mailer.send() call needs beyond the idempotency key.

    message_class is always "transactional" today (the only class the mailer
    allows insolvia_api to send), but it is a field rather than a constant so
    a future non-transactional category does not require touching the port.
    """

    category: str
    message_class: str
    to_address: str
    subject: str
    html_body: str
    text_body: str


def _greeting(recipient_name: str | None) -> str:
    return (
        f"Hi {recipient_name.strip()},"
        if recipient_name and recipient_name.strip()
        else "Hello,"
    )


def _html_document(*, preheader: str, heading: str, body_html: str) -> str:
    """Wrap templated body content in the shared branded shell.

    A single nested-table layout, inline styles only, so it renders
    consistently across the wide range of email clients that ignore or strip
    <style> blocks and external stylesheets.
    """
    return (
        "<!doctype html>"
        '<html lang="en">'
        "<head>"
        '<meta charset="utf-8">'
        '<meta name="viewport" content="width=device-width, initial-scale=1">'
        f"<title>{escape(heading)}</title>"
        "</head>"
        f'<body style="margin:0;padding:0;background-color:{_COLOR_BG};'
        f'font-family:{_FONT_BODY};">'
        # Preheader: hidden preview text, no visual footprint.
        f'<div style="display:none;max-height:0;overflow:hidden;opacity:0;">'
        f"{escape(preheader)}</div>"
        f'<table role="presentation" width="100%" cellpadding="0" cellspacing="0" '
        f'border="0" style="background-color:{_COLOR_BG};padding:32px 16px;">'
        '<tr><td align="center">'
        f'<table role="presentation" width="100%" cellpadding="0" cellspacing="0" '
        f'border="0" style="max-width:560px;background-color:{_COLOR_CARD};'
        f'border:1px solid {_COLOR_LINE};border-radius:8px;">'
        '<tr><td style="padding:32px 32px 8px 32px;">'
        f'<span style="font-family:{_FONT_HEADING};font-size:24px;'
        f'color:{_COLOR_INK};font-weight:bold;">Insolvia</span>'
        "</td></tr>"
        '<tr><td style="padding:16px 32px 32px 32px;">'
        f'<h1 style="font-family:{_FONT_HEADING};font-size:20px;'
        f'color:{_COLOR_INK};margin:0 0 16px 0;">{escape(heading)}</h1>'
        f'<div style="font-family:{_FONT_BODY};font-size:15px;line-height:1.6;'
        f'color:{_COLOR_INK};">{body_html}</div>'
        "</td></tr>"
        f'<tr><td style="padding:24px 32px 32px 32px;border-top:1px solid {_COLOR_LINE};">'
        f'<p style="font-family:{_FONT_BODY};font-size:12px;color:{_COLOR_MUTED};'
        'margin:0 0 4px 0;">Insolvia &mdash; bankruptcy case preparation &amp; '
        "e-filing</p>"
        f'<p style="font-family:{_FONT_BODY};font-size:12px;color:{_COLOR_MUTED};'
        'margin:0 0 4px 0;">This is a transactional message about your '
        "Insolvia account.</p>"
        f'<p style="font-family:{_FONT_BODY};font-size:12px;color:{_COLOR_MUTED};'
        f'margin:0;"><a href="{_PRIVACY_URL}" style="color:{_COLOR_MUTED};">'
        "Privacy policy</a></p>"
        "</td></tr>"
        "</table>"
        "</td></tr>"
        "</table>"
        "</body></html>"
    )


def _button_html(*, label: str, url: str) -> str:
    """A bulletproof button: a table-wrapped <a>, styled inline. No <button>
    (unreliable rendering support), no background-image tricks."""
    return (
        '<table role="presentation" cellpadding="0" cellspacing="0" border="0" '
        'style="margin:24px 0;">'
        "<tr><td>"
        f'<a href="{escape(url)}" target="_blank" '
        f'style="display:inline-block;background-color:{_COLOR_ACCENT};'
        f"color:{_COLOR_ACCENT_TEXT};font-family:{_FONT_BODY};font-size:15px;"
        "font-weight:bold;text-decoration:none;padding:14px 28px;"
        f'border-radius:6px;">{escape(label)}</a>'
        "</td></tr>"
        "</table>"
    )


def _text_footer() -> str:
    return (
        "\n\n--\n"
        "Insolvia -- bankruptcy case preparation & e-filing\n"
        "This is a transactional message about your Insolvia account.\n"
        f"Privacy policy: {_PRIVACY_URL}\n"
    )


# --- templates -----------------------------------------------------------


def welcome_email(
    to_address: str, *, recipient_name: str | None = None, app_url: str
) -> OutboundEmail:
    """category "welcome" — sent once, right after signup."""
    greeting = _greeting(recipient_name)
    intro = (
        "Insolvia helps your firm prepare and e-file consumer bankruptcy "
        "cases faster, with fewer errors."
    )
    body_html = (
        f'<p style="margin:0 0 16px 0;">{escape(greeting)}</p>'
        f'<p style="margin:0 0 16px 0;">{escape(intro)}</p>'
        f"{_button_html(label='Open Insolvia', url=app_url)}"
    )
    html_body = _html_document(
        preheader="Welcome to Insolvia.",
        heading="Welcome to Insolvia",
        body_html=body_html,
    )
    text_body = f"{greeting}\n\n{intro}\n\nOpen Insolvia: {app_url}{_text_footer()}"
    return OutboundEmail(
        category="welcome",
        message_class=_MESSAGE_CLASS,
        to_address=to_address,
        subject="Welcome to Insolvia",
        html_body=html_body,
        text_body=text_body,
    )


def email_verification_email(
    to_address: str, *, verification_url: str, recipient_name: str | None = None
) -> OutboundEmail:
    """category "email_verification" — sent to confirm a new account's email."""
    greeting = _greeting(recipient_name)
    intro = "Please confirm your email address to activate your Insolvia account."
    expiry_note = (
        "This link expires soon. If you didn't create an Insolvia account, "
        "you can safely ignore this email."
    )
    body_html = (
        f'<p style="margin:0 0 16px 0;">{escape(greeting)}</p>'
        f'<p style="margin:0 0 16px 0;">{escape(intro)}</p>'
        f"{_button_html(label='Verify email', url=verification_url)}"
        f'<p style="margin:16px 0 0 0;font-size:13px;color:{_COLOR_MUTED};">'
        "or paste this link into your browser:<br>"
        f'<a href="{escape(verification_url)}" style="color:{_COLOR_ACCENT};'
        f'word-break:break-all;">{escape(verification_url)}</a></p>'
        f'<p style="margin:16px 0 0 0;font-size:13px;color:{_COLOR_MUTED};">'
        f"{escape(expiry_note)}</p>"
    )
    html_body = _html_document(
        preheader="Confirm your email address to activate your account.",
        heading="Verify your email address",
        body_html=body_html,
    )
    text_body = (
        f"{greeting}\n\n{intro}\n\n"
        f"Verify email: {verification_url}\n\n"
        f"{expiry_note}"
        f"{_text_footer()}"
    )
    return OutboundEmail(
        category="email_verification",
        message_class=_MESSAGE_CLASS,
        to_address=to_address,
        subject="Verify your email address",
        html_body=html_body,
        text_body=text_body,
    )


def password_reset_email(
    to_address: str, *, reset_url: str, recipient_name: str | None = None
) -> OutboundEmail:
    """category "password_reset" — sent when a reset is requested."""
    greeting = _greeting(recipient_name)
    intro = "We received a request to reset the password for your Insolvia account."
    security_note = (
        "If you didn't request this, you can safely ignore this email and "
        "your password will stay unchanged."
    )
    body_html = (
        f'<p style="margin:0 0 16px 0;">{escape(greeting)}</p>'
        f'<p style="margin:0 0 16px 0;">{escape(intro)}</p>'
        f"{_button_html(label='Reset password', url=reset_url)}"
        f'<p style="margin:16px 0 0 0;font-size:13px;color:{_COLOR_MUTED};">'
        "or paste this link into your browser:<br>"
        f'<a href="{escape(reset_url)}" style="color:{_COLOR_ACCENT};'
        f'word-break:break-all;">{escape(reset_url)}</a></p>'
        f'<p style="margin:16px 0 0 0;font-size:13px;color:{_COLOR_MUTED};">'
        f"{escape(security_note)}</p>"
    )
    html_body = _html_document(
        preheader="Reset the password for your Insolvia account.",
        heading="Reset your Insolvia password",
        body_html=body_html,
    )
    text_body = (
        f"{greeting}\n\n{intro}\n\n"
        f"Reset password: {reset_url}\n\n"
        f"{security_note}"
        f"{_text_footer()}"
    )
    return OutboundEmail(
        category="password_reset",
        message_class=_MESSAGE_CLASS,
        to_address=to_address,
        subject="Reset your Insolvia password",
        html_body=html_body,
        text_body=text_body,
    )
