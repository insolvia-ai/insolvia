"""Inbound mail forwarder for the insolvia.ai support addresses.

This package holds the AWS Lambda that SES invokes for mail received on the
inbound addresses (``hello@``, ``support@``, ``security@``). It parses the raw
MIME safely, builds a brand-new forwarded message (never relaying untrusted
headers), and delivers it from no-reply@insolvia.ai to a private destination
supplied through a secret.
"""

from .handler import lambda_handler

__all__ = ["lambda_handler"]
