from __future__ import annotations

from asgiref.wsgi import WsgiToAsgi
from mangum import Mangum

from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config

# AWS adapters (DynamoDB waitlist store, ...) are composed here as they land,
# mirroring mailer's api_lambda entrypoint.
app = create_app(ApiDependencies(config=load_config()))
handler = Mangum(WsgiToAsgi(app), lifespan="off")
