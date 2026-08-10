"""Implementations of `insolvia_core.ports`.

aws/ holds the real backends (DynamoDB firm store, Cognito, the pool's JWKS);
memory/ holds the in-memory stand-ins for tests and the services' plain
development servers. This layer may import boto3; it may never import a web
framework — the services' own architecture tests enforce their side, and this
package's `tests/test_architecture.py` enforces this one.
"""
