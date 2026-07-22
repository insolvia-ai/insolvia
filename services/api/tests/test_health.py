from insolvia_api import __version__
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config


def test_health_reports_service_identity(client):
    response = client.get("/health")

    assert response.status_code == 200
    assert response.content_type == "application/json"
    assert response.get_json() == {
        "status": "ok",
        "service": "insolvia-api",
        "version": __version__,
        "environment": "local",
    }


def test_health_reports_the_configured_environment():
    app = create_app(ApiDependencies(config=load_config({"INSOLVIA_ENV": "staging"})))

    response = app.test_client().get("/health")

    assert response.status_code == 200
    assert response.get_json()["environment"] == "staging"


def test_unknown_route_stays_a_404(client):
    # The catch-all error handler must pass HTTP errors through untouched
    # rather than repackaging them as 500s.
    assert client.get("/nope").status_code == 404
