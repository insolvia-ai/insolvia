from __future__ import annotations

from dataclasses import dataclass
from typing import cast

from flask import current_app
from insolvia_core.ports import FirmStore, JwksProvider, UserDirectory

from insolvia_admin.core.audit import AuditLog
from insolvia_admin.core.config import AppConfig


@dataclass(frozen=True)
class AdminDependencies:
    """Everything the API layer needs, composed by an entrypoint.

    Same pattern as the tenant API's ApiDependencies: the Lambda entrypoint
    supplies the AWS implementations, the development server and tests supply
    the in-memory ones. `jwks_provider` serves GOOGLE's keys here, not a
    Cognito pool's — the one composition-time fact that carries the whole
    trust boundary, which is why the entrypoints name it loudly.
    """

    config: AppConfig
    jwks_provider: JwksProvider | None
    firm_store: FirmStore
    user_directory: UserDirectory
    audit_log: AuditLog


def dependencies() -> AdminDependencies:
    return cast(
        AdminDependencies,
        current_app.extensions["insolvia_admin_dependencies"],
    )
