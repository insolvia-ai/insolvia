from __future__ import annotations

from urllib.parse import urlsplit

from insolvia_admin.core.config import AppConfig

# Loopback hosts a browser dev server may serve from. The portal pins its dev
# server to :3100 (Google's redirect URIs are exact-match), but this CORS
# check deliberately does not depend on the port — same rule as the tenant
# API's copy of this module, which owns the fuller argument.
_LOCALHOST_HOSTNAMES = frozenset({"localhost", "127.0.0.1", "::1"})


def origin_allowed(config: AppConfig, origin: str) -> bool:
    """Decide whether a browser Origin may read responses from this service.

    Exact per-environment origins from the config, plus loopback dev origins
    outside production. No Origin header means no CORS headers — never a
    wildcard.
    """
    if origin in config.cors_allowed_origins:
        return True
    if not config.cors_allow_localhost:
        return False
    parts = urlsplit(origin)
    return (
        parts.scheme in ("http", "https")
        and parts.hostname in _LOCALHOST_HOSTNAMES
        and not parts.path
        and not parts.query
        and not parts.fragment
    )
