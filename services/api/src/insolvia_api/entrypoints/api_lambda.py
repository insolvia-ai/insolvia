from __future__ import annotations

from asgiref.wsgi import WsgiToAsgi
from mangum import Mangum

from insolvia_api.adapters.aws.mailer_client import SigV4MailerClient
from insolvia_api.adapters.aws.waitlist_store import DynamoDbWaitlistStore
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_api.core.logging import configure_logging
from insolvia_api.core.ports import Mailer

configure_logging()

config = load_config()
if not config.waitlist_table_name:
    raise RuntimeError("WAITLIST_TABLE_NAME must be set for the Lambda entrypoint")

# AWS adapters are composed here, mirroring mailer's api_lambda entrypoint.
# mailer_api_url is unset only if the mailer's SSM param hasn't been deployed
# yet in this environment — fall back to the in-memory client rather than
# fail the whole Lambda, mirroring the waitlist store's fallback shape.
mailer: Mailer
if config.mailer_api_url:
    mailer = SigV4MailerClient(config.mailer_api_url)
else:
    mailer = InMemoryMailerClient()

app = create_app(
    ApiDependencies(
        config=config,
        waitlist_store=DynamoDbWaitlistStore(config.waitlist_table_name),
        mailer=mailer,
    )
)
handler = Mangum(WsgiToAsgi(app), lifespan="off")
