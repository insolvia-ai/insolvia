from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass

from insolvia_api.core.errors import ValidationError

SERVICE_NAME = "insolvia-api"

ENVIRONMENTS = ("local", "staging", "production")

# Per-environment CORS allowlist (issue #68). Exact origins only — NO
# wildcard, ever:
#   - The desktop app is a native client and sends no Origin header at all.
#     "No Origin" must never become a reason to widen this list or answer
#     with `*` — CORS simply does not apply to that client, and the browser
#     clients CORS does protect get the tightest possible policy.
#   - www.insolvia.ai deliberately does NOT appear here: the marketing site's
#     waitlist action calls POST /v1/waitlist server-to-server from its SSR
#     Lambda (no browser, no Origin header), so CORS is not in play for it
#     (docs/adr/0001).
# Localhost dev origins are handled separately (cors_allow_localhost) because
# `flutter run -d chrome` picks an arbitrary port; the response still echoes
# the one matched origin, never a wildcard.
_CORS_ALLOWED_ORIGINS: dict[str, tuple[str, ...]] = {
    "production": ("https://app.insolvia.ai",),
    "staging": ("https://staging-app.insolvia.ai",),
    "local": (),
}

# Where the marketing site lives, per environment (issue #80). Every URL this
# service puts in an outgoing email — the privacy policy, the unsubscribe link
# — is built from this, so a staging email links staging pages and a
# production email links production pages. That matters more than it sounds:
# staging mail pointing at www.insolvia.ai/unsubscribe would send a tester's
# click to the production API, and it would work, and it would suppress the
# address in production.
#
# A constant map rather than an SSM parameter because these hosts are decided
# in this repo (infra/envs/*/variables.tf marketing_subdomain) and change with
# a code review, not with an ops action. `local` is the React Router dev
# server's default port.
_MARKETING_ORIGINS: dict[str, str] = {
    "production": "https://www.insolvia.ai",
    "staging": "https://staging-www.insolvia.ai",
    "local": "http://localhost:3000",
}


@dataclass(frozen=True)
class AppConfig:
    """The service configuration, parsed and validated once at composition time.

    load_config is the only real constructor — the field defaults exist so
    tests can build a local config tersely, and they match INSOLVIA_ENV=local.
    Everything is read in load_config; nothing else in the package touches
    os.environ.
    """

    environment: str
    waitlist_table_name: str | None = None
    mailer_api_url: str | None = None
    unsubscribe_secret: str | None = None
    marketing_origin: str = _MARKETING_ORIGINS["local"]
    cors_allowed_origins: tuple[str, ...] = ()
    cors_allow_localhost: bool = True


def load_config(environ: Mapping[str, str] | None = None) -> AppConfig:
    """Build the configuration from the environment.

    INSOLVIA_ENV (local|staging|production) defaults to "local", mirroring the
    app's --dart-define=INSOLVIA_ENV. WAITLIST_TABLE_NAME names the DynamoDB
    table behind POST /v1/waitlist — in local dev that is this machine's real
    per-developer table (scripts/dev-aws-setup.sh); unset means the in-memory
    store, which only unit tests and the bare development server use.
    MAILER_API_URL is the mailer service's public HTTPS base URL (published
    to SSM as /insolvia/<env>/api/mailer-api-url and re-derived into this env
    var by the deploy workflow); unset means the in-memory mailer, same
    fallback shape as the waitlist store.
    UNSUBSCRIBE_SECRET is the HMAC key for unsubscribe tokens (SSM
    SecureString /insolvia/<env>/api/unsubscribe-secret, same derivation).
    Unset is NOT a fallback: core/unsubscribe.py refuses to mint or verify
    without it rather than degrading to unsigned tokens, so the failure is a
    500 on the unsubscribe route and a send that carries no unsubscribe link
    — loud, not silent.
    """
    source = os.environ if environ is None else environ
    environment = source.get("INSOLVIA_ENV", "local")
    if environment not in ENVIRONMENTS:
        raise ValidationError(
            f"INSOLVIA_ENV must be one of {', '.join(ENVIRONMENTS)}, "
            f"got {environment!r}"
        )
    return AppConfig(
        environment=environment,
        waitlist_table_name=source.get("WAITLIST_TABLE_NAME") or None,
        mailer_api_url=source.get("MAILER_API_URL") or None,
        unsubscribe_secret=source.get("UNSUBSCRIBE_SECRET") or None,
        marketing_origin=_MARKETING_ORIGINS[environment],
        cors_allowed_origins=_CORS_ALLOWED_ORIGINS[environment],
        cors_allow_localhost=environment != "production",
    )
