from __future__ import annotations

import logging
import time

from flask import Flask, g, jsonify, request
from werkzeug.exceptions import HTTPException

from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.api.routes.health import blueprint as health_blueprint
from insolvia_api.api.routes.unsubscribe import blueprint as unsubscribe_blueprint
from insolvia_api.api.routes.waitlist import blueprint as waitlist_blueprint
from insolvia_api.core.cors import origin_allowed
from insolvia_api.core.errors import ApiError, FieldValidationError, ValidationError

logger = logging.getLogger(__name__)

# One JSON line per request (see insolvia_api.core.logging). Request metadata
# only — method, path, status, duration — never bodies, query strings, or
# headers: this API will carry GLBA-protected client financial data, and for
# the waitlist specifically the submitted field values are PII that must
# never appear in a log line (only the server-generated id may).
request_logger = logging.getLogger("insolvia_api.request")


def create_app(dependencies: ApiDependencies) -> Flask:
    app = Flask(__name__)
    app.extensions["insolvia_api_dependencies"] = dependencies
    app.register_blueprint(health_blueprint)
    app.register_blueprint(unsubscribe_blueprint)
    app.register_blueprint(waitlist_blueprint)
    # Further blueprints register here, one line per module.

    config = dependencies.config

    @app.before_request
    def start_request_timer():
        g.insolvia_request_started = time.perf_counter()

    @app.after_request
    def finalize_response(response):
        # --- CORS (issue #68): config-driven per-environment allowlist. ---
        # A matched Origin is echoed back exactly; anything else — including
        # a missing Origin — gets no Access-Control-* headers at all. The
        # desktop app sends no Origin (native client, CORS not in play) and
        # that must never become a reason to widen this to a wildcard. The
        # marketing SSR Lambda's server-to-server POST /v1/waitlist call
        # likewise sends no Origin, so www origins do not belong in the
        # allowlist either.
        response.vary.add("Origin")
        origin = request.headers.get("Origin")
        if origin and origin_allowed(config, origin):
            response.headers["Access-Control-Allow-Origin"] = origin
            response.headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS"
            response.headers["Access-Control-Allow-Headers"] = (
                "Content-Type, Authorization"
            )
            response.headers["Access-Control-Max-Age"] = "600"

        # --- Request log (issue #69): exactly one JSON line per request. ---
        started = g.pop("insolvia_request_started", None)
        duration_ms = (
            round((time.perf_counter() - started) * 1000, 1)
            if started is not None
            else None
        )
        request_logger.info(
            "request handled",
            extra={
                "method": request.method,
                "path": request.path,
                "status": response.status_code,
                "duration_ms": duration_ms,
            },
        )
        return response

    @app.errorhandler(FieldValidationError)
    def field_validation_error(error: FieldValidationError):
        # The shape the marketing action surfaces per-field: {error, fields}.
        return jsonify({"error": "ValidationError", "fields": error.fields}), 400

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
