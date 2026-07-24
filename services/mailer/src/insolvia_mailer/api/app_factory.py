from __future__ import annotations

import logging

from flask import Flask, jsonify

from insolvia_mailer.api.dependencies import ApiDependencies
from insolvia_mailer.api.routes.attachments import blueprint as attachments_blueprint
from insolvia_mailer.api.routes.messages import blueprint as messages_blueprint
from insolvia_mailer.api.routes.suppressions import (
    blueprint as suppressions_blueprint,
)
from insolvia_mailer.core.errors import (
    AuthorizationError,
    ConflictError,
    MailerError,
    RetryableError,
    ValidationError,
)

logger = logging.getLogger(__name__)


def create_app(dependencies: ApiDependencies) -> Flask:
    app = Flask(__name__)
    app.extensions["mailer_dependencies"] = dependencies
    app.register_blueprint(attachments_blueprint)
    app.register_blueprint(messages_blueprint)
    app.register_blueprint(suppressions_blueprint)

    @app.get("/health")
    def health():
        return jsonify({"status": "ok"})

    @app.errorhandler(AuthorizationError)
    def authorization_error(error: AuthorizationError):
        return jsonify({"error": "Forbidden", "message": str(error)}), 403

    @app.errorhandler(ConflictError)
    def conflict_error(error: ConflictError):
        return jsonify({"error": "Conflict", "message": str(error)}), 409

    @app.errorhandler(ValidationError)
    def validation_error(error: ValidationError):
        return jsonify({"error": "ValidationError", "message": str(error)}), 400

    @app.errorhandler(RetryableError)
    def retryable_error(_error: RetryableError):
        logger.exception("retryable Mailer admission failure")
        return jsonify(
            {"error": "Unavailable", "message": "admission is unavailable"}
        ), 503

    @app.errorhandler(MailerError)
    def mailer_error(error: MailerError):
        return jsonify({"error": error.__class__.__name__, "message": str(error)}), 400

    @app.errorhandler(Exception)
    def unexpected_error(_error: Exception):
        logger.exception("unexpected Mailer API failure")
        return jsonify({"error": "InternalError", "message": "request failed"}), 500

    return app
