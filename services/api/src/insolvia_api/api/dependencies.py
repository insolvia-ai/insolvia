from __future__ import annotations

from dataclasses import dataclass

from flask import current_app

from insolvia_api.core.config import AppConfig


@dataclass(frozen=True)
class ApiDependencies:
    """Everything the API layer needs, composed by an entrypoint.

    Adapter wiring lands here: each core port (e.g. the waitlist store) gets a
    field, the Lambda entrypoint supplies the AWS implementation, and the
    development server and tests supply the in-memory one — mirroring
    mailer's ApiDependencies.
    """

    config: AppConfig


def dependencies() -> ApiDependencies:
    return current_app.extensions["insolvia_api_dependencies"]
