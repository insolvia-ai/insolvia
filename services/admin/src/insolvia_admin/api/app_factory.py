from __future__ import annotations

import logging
import time

from flask import Flask, Response, g, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.errors import (
    ApiError,
    ConflictError,
    FieldValidationError,
    ForbiddenError,
    NotFoundError,
    ValidationError,
)
from werkzeug.exceptions import HTTPException

from insolvia_admin.api.dependencies import AdminDependencies
from insolvia_admin.api.routes.firms import blueprint as firms_blueprint
from insolvia_admin.api.routes.health import blueprint as health_blueprint
from insolvia_admin.core.cors import origin_allowed

logger = logging.getLogger(__name__)

# One JSON line per request, metadata only — same GLBA-shaped rule as the
# tenant API, and stricter stakes: this service's requests name firms across
# every tenant.
request_logger = logging.getLogger("insolvia_admin.request")


def create_app(dependencies: AdminDependencies) -> Flask:
    app = Flask(__name__)
    app.extensions["insolvia_admin_dependencies"] = dependencies
    app.register_blueprint(firms_blueprint)
    app.register_blueprint(health_blueprint)

    config = dependencies.config

    @app.before_request
    def start_request_timer() -> None:
        g.insolvia_request_started = time.perf_counter()

    @app.after_request
    def finalize_response(response: Response) -> Response:
        # CORS: exact-origin allowlist, echoed never wildcarded — the tenant
        # API's app_factory owns the full argument. The method list matches
        # this service's surface (no PUT or DELETE routes exist here yet;
        # adding one means adding its method HERE too, or the browser never
        # sends the request at all).
        response.vary.add("Origin")
        origin = request.headers.get("Origin")
        if origin and origin_allowed(config, origin):
            response.headers["Access-Control-Allow-Origin"] = origin
            response.headers["Access-Control-Allow-Methods"] = (
                "GET, POST, PATCH, OPTIONS"
            )
            response.headers["Access-Control-Allow-Headers"] = (
                "Content-Type, Authorization"
            )
            response.headers["Access-Control-Max-Age"] = "600"

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
    def field_validation_error(error: FieldValidationError) -> ResponseReturnValue:
        return jsonify({"error": "ValidationError", "fields": error.fields}), 400

    @app.errorhandler(ValidationError)
    def validation_error(error: ValidationError) -> ResponseReturnValue:
        return jsonify({"error": "ValidationError", "message": str(error)}), 400

    @app.errorhandler(ConflictError)
    def conflict_error(error: ConflictError) -> ResponseReturnValue:
        return jsonify({"error": "ConflictError", "message": str(error)}), 409

    @app.errorhandler(ForbiddenError)
    def forbidden_error(error: ForbiddenError) -> ResponseReturnValue:
        return jsonify({"error": "ForbiddenError", "message": str(error)}), 403

    @app.errorhandler(NotFoundError)
    def not_found_error(error: NotFoundError) -> ResponseReturnValue:
        # Registered above the ApiError handler on purpose: Flask dispatches
        # to the most specific class, and NotFoundError IS an ApiError.
        return jsonify({"error": "NotFoundError", "message": str(error)}), 404

    @app.errorhandler(ApiError)
    def api_error(error: ApiError) -> ResponseReturnValue:
        return jsonify({"error": error.__class__.__name__, "message": str(error)}), 400

    @app.errorhandler(Exception)
    def unexpected_error(error: Exception) -> ResponseReturnValue:
        if isinstance(error, HTTPException):
            return error
        logger.exception("unexpected Insolvia admin failure")
        return jsonify({"error": "InternalError", "message": "request failed"}), 500

    return app
