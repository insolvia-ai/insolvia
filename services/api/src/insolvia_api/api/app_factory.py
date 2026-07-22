from __future__ import annotations

import logging

from flask import Flask, jsonify
from werkzeug.exceptions import HTTPException

from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.api.routes.health import blueprint as health_blueprint
from insolvia_api.core.errors import ApiError, ValidationError

logger = logging.getLogger(__name__)


def create_app(dependencies: ApiDependencies) -> Flask:
    app = Flask(__name__)
    app.extensions["insolvia_api_dependencies"] = dependencies
    app.register_blueprint(health_blueprint)
    # Further blueprints (waitlist, ...) register here, one line per module.

    @app.errorhandler(ValidationError)
    def validation_error(error: ValidationError):
        return jsonify({"error": "ValidationError", "message": str(error)}), 400

    @app.errorhandler(ApiError)
    def api_error(error: ApiError):
        return jsonify({"error": error.__class__.__name__, "message": str(error)}), 400

    @app.errorhandler(Exception)
    def unexpected_error(error: Exception):
        if isinstance(error, HTTPException):
            return error
        logger.exception("unexpected Insolvia API failure")
        return jsonify({"error": "InternalError", "message": "request failed"}), 500

    return app
