from __future__ import annotations

from insolvia_api.adapters.aws.waitlist_store import DynamoDbWaitlistStore
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_api.core.logging import configure_logging
from insolvia_api.core.ports import WaitlistStore

config = load_config()
if config.environment != "local":
    raise RuntimeError("the development server requires INSOLVIA_ENV=local")

configure_logging()

# Adapter composition, mirroring mailer's development server. The plain dev
# server defaults to the in-memory store (echo=True logs each submission so
# local marketing-site dev can see them arrive); docker-compose sets
# WAITLIST_TABLE_NAME + DYNAMODB_ENDPOINT_URL to exercise the real DynamoDB
# adapter against dynamodb-local instead.
waitlist_store: WaitlistStore
if config.waitlist_table_name:
    waitlist_store = DynamoDbWaitlistStore(
        config.waitlist_table_name, endpoint_url=config.dynamodb_endpoint_url
    )
else:
    waitlist_store = MemoryWaitlistStore(echo=True)

app = create_app(ApiDependencies(config=config, waitlist_store=waitlist_store))
