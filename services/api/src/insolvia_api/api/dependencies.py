from __future__ import annotations

from dataclasses import dataclass

from flask import current_app

from insolvia_api.core.config import AppConfig
from insolvia_api.core.ports import Mailer, WaitlistStore


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


def dependencies() -> ApiDependencies:
    return current_app.extensions["insolvia_api_dependencies"]
