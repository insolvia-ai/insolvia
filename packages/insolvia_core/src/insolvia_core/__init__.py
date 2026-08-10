"""The shared domain of Insolvia's Python services (issue #208).

Extracted from `insolvia_api` when the admin service needed the same firm
domain, item shapes, and token verification the tenant API composes. The item
shapes live in ONE place so the DynamoDB and in-memory stores cannot drift —
`firms.firm_item`'s docstring owns that argument, and a second service
consuming them is exactly the case it anticipated.
"""

__all__ = ["__version__"]

__version__ = "0.1.0"
