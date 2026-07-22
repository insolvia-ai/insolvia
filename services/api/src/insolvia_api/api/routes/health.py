from __future__ import annotations

from flask import Blueprint, jsonify

from insolvia_api import __version__
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.config import SERVICE_NAME

blueprint = Blueprint("health", __name__)


@blueprint.get("/health")
def health():
    return jsonify(
        {
            "status": "ok",
            "service": SERVICE_NAME,
            "version": __version__,
            "environment": dependencies().config.environment,
        }
    )
