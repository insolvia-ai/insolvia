"""Implementations of `insolvia_core.ports`.

aws/ holds the AWS backends (Cognito, the pool's JWKS, S3, KMS); postgres/
holds the stores (ADR 0030); memory/ holds the in-memory stand-ins for tests
and the services' plain development servers. This layer may import boto3; it
may never import a web framework — the services' own architecture tests
enforce their side, and this package's `tests/test_architecture.py` enforces
this one.
"""
