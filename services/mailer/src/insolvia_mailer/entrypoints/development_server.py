from __future__ import annotations

import logging
import os

from insolvia_mailer.adapters.mailpit.transport import MailpitTransport
from insolvia_mailer.adapters.memory.auth import RegisteredServiceAuthorizer
from insolvia_mailer.adapters.memory.config import load_service_registry
from insolvia_mailer.adapters.memory.store import MemoryStore
from insolvia_mailer.adapters.memory.worker import MemoryDeliveryWorker
from insolvia_mailer.api.app_factory import create_app
from insolvia_mailer.api.dependencies import ApiDependencies

if os.environ.get("MAILER_RUNTIME", "").lower() != "development":
    raise RuntimeError("the development server requires MAILER_RUNTIME=development")

logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
store = MemoryStore()
app = create_app(
    ApiDependencies(
        services=load_service_registry(),
        store=store,
        authorizer=RegisteredServiceAuthorizer(),
        attachment_receiver=store,
    )
)
MemoryDeliveryWorker(store, MailpitTransport()).start()
