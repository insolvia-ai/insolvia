from __future__ import annotations

import logging

from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config

config = load_config()
if config.environment != "local":
    raise RuntimeError("the development server requires INSOLVIA_ENV=local")

logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
# In-memory adapters are composed here as they land, mirroring mailer's
# development server.
app = create_app(ApiDependencies(config=config))
