from __future__ import annotations

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue

from insolvia_admin.api.dependencies import dependencies

blueprint = Blueprint("health", __name__)


@blueprint.get("/health")
def health() -> ResponseReturnValue:
    """Deliberately public — the deploy workflow's smoke check curls it and
    asserts the environment, before any staff token exists in CI."""
    return jsonify({"status": "ok", "environment": dependencies().config.environment})
