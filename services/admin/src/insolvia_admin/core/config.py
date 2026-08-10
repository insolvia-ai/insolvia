from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass

from insolvia_core.errors import ValidationError

SERVICE_NAME = "insolvia-admin"

ENVIRONMENTS = ("local", "staging", "production")

# The Workspace domain staff tokens must carry in `hd`. A constant, not an
# environment variable: it is decided in this repo, changes with a code
# review, and a typo'd env var here would lock every staff member out (or,
# worse, be empty and fail closed into a support mystery).
WORKSPACE_DOMAIN = "insolvia.ai"

# Per-environment CORS allowlist — the admin portal's origins and nothing
# else. Same exact-origin rules as the tenant API's list (its config.py owns
# the no-wildcard argument); localhost handling is separate because the
# portal's dev server owns :3100 but this check deliberately does not depend
# on the port.
_CORS_ALLOWED_ORIGINS: dict[str, tuple[str, ...]] = {
    "production": ("https://admin.insolvia.ai",),
    "staging": ("https://staging-admin.insolvia.ai",),
    "local": (),
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
    firm_table_name: str | None = None
    firm_user_pool_id: str | None = None
    admin_audit_table_name: str | None = None
    google_client_id: str | None = None
    workspace_domain: str = WORKSPACE_DOMAIN
    cors_allowed_origins: tuple[str, ...] = ()
    cors_allow_localhost: bool = True


def load_config(environ: Mapping[str, str] | None = None) -> AppConfig:
    """Build the configuration from the environment.

    INSOLVIA_ENV (local|staging|production) defaults to "local", matching the
    tenant API. The rest, all following its unset-means-in-memory shape for
    local work:

    FIRM_TABLE_NAME — the SAME table the tenant API reads
    (infra/modules/firm_store): this service is the second principal with
    access to it, which is the exception ADR 0011 records. Unset composes the
    in-memory store.

    FIRM_USER_POOL_ID — the FIRM pool (insolvia-users-<env>), where
    provisioning mints the first administrator's account. Deliberately not
    named AUTH_USER_POOL_ID as the API's is: this service never verifies
    tokens from that pool — staff tokens come from Google — and a name that
    read like "the auth pool" would invite exactly that confusion.

    ADMIN_AUDIT_TABLE_NAME — the append-only provisioning record
    (infra/modules/admin_service). The IAM grant is PutItem and nothing else.

    GOOGLE_CLIENT_ID — the environment's OAuth client id (a public value;
    it appears in every sign-in redirect the portal makes). Unset fails
    CLOSED: every staff route answers 401, never "allow".
    """
    env = dict(environ if environ is not None else os.environ)

    environment = env.get("INSOLVIA_ENV", "local").strip() or "local"
    if environment not in ENVIRONMENTS:
        raise ValidationError(
            "INSOLVIA_ENV must be one of "
            + ", ".join(ENVIRONMENTS)
            + f"; got {environment!r}"
        )

    def optional(name: str) -> str | None:
        value = env.get(name, "").strip()
        return value or None

    return AppConfig(
        environment=environment,
        firm_table_name=optional("FIRM_TABLE_NAME"),
        firm_user_pool_id=optional("FIRM_USER_POOL_ID"),
        admin_audit_table_name=optional("ADMIN_AUDIT_TABLE_NAME"),
        google_client_id=optional("GOOGLE_CLIENT_ID"),
        cors_allowed_origins=_CORS_ALLOWED_ORIGINS[environment],
        cors_allow_localhost=environment != "production",
    )
