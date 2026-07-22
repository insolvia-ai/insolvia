from __future__ import annotations

from urllib.parse import urlsplit

from insolvia_api.core.config import AppConfig

# Loopback hosts a browser dev server may serve from. Matching by hostname
# (any port, http or https) rather than listing ports keeps `flutter run -d
# chrome` working — it binds an arbitrary port per run. This is still an
# exact-origin echo in the response, never a wildcard.
_LOCALHOST_HOSTNAMES = frozenset({"localhost", "127.0.0.1", "::1"})


def origin_allowed(config: AppConfig, origin: str) -> bool:
    """Decide whether a browser Origin may read responses from this API.

    Pure allowlist logic (issue #68): exact per-environment origins from the
    config, plus loopback dev origins outside production. The absence of an
    Origin header (the desktop app, server-to-server callers like the
    marketing SSR Lambda) is not this function's concern — no header means no
    CORS response headers, which must never widen into a wildcard.
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
