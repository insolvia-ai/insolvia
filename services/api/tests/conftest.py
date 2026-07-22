import pytest

from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config


@pytest.fixture
def client():
    app = create_app(ApiDependencies(config=load_config({})))
    return app.test_client()
