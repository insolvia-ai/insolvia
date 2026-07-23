from __future__ import annotations

from asgiref.wsgi import WsgiToAsgi
from mangum import Mangum

from insolvia_api.adapters.aws.waitlist_store import DynamoDbWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_api.core.logging import configure_logging

configure_logging()

config = load_config()
if not config.waitlist_table_name:
    raise RuntimeError("WAITLIST_TABLE_NAME must be set for the Lambda entrypoint")

# AWS adapters are composed here, mirroring mailer's api_lambda entrypoint.
app = create_app(
    ApiDependencies(
        config=config,
        waitlist_store=DynamoDbWaitlistStore(config.waitlist_table_name),
    )
)
handler = Mangum(WsgiToAsgi(app), lifespan="off")
