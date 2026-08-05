from __future__ import annotations

from dataclasses import dataclass
from typing import cast

from flask import current_app

from insolvia_api.core.config import AppConfig
from insolvia_api.core.ports import (
    AccessLog,
    CaseStore,
    FirmStore,
    JwksProvider,
    Mailer,
    WaitlistStore,
)


@dataclass(frozen=True)
class ApiDependencies:
    """Everything the API layer needs, composed by an entrypoint.

    Each core port gets a field: the Lambda entrypoint supplies the AWS
    implementation, and the development server and tests supply the
    in-memory one — mirroring mailer's ApiDependencies.
    """

    config: AppConfig
    waitlist_store: WaitlistStore
    mailer: Mailer
    # Optional for the same reason jwks_provider is: the existing public-route
    # tests build an ApiDependencies without them. The case routes are
    # unreachable without both — api/routes/cases.py answers 503 rather than
    # pretending, and the Lambda entrypoint refuses to boot without them.
    case_store: CaseStore | None = None
    access_log: AccessLog | None = None
    # The tenancy layer. Optional for the same reason as the pair above — the
    # existing public-route tests build an ApiDependencies without one — but it
    # is the field with the shortest fuse: once accessor resolution lands,
    # absent means no authenticated request can establish which firm the caller
    # belongs to, and every case route answers 403 rather than degrading.
    firm_store: FirmStore | None = None
    # None means "this deployment cannot verify tokens" (issue #79). It is a
    # fail-CLOSED default, not a permissive one: api/auth.py answers 401 on
    # every protected route when it is absent, and the Lambda entrypoint
    # refuses to boot without one. It stays optional only so the existing
    # public-route tests can build an ApiDependencies without one.
    jwks_provider: JwksProvider | None = None


def dependencies() -> ApiDependencies:
    return cast("ApiDependencies", current_app.extensions["insolvia_api_dependencies"])
