"""Environment-specific implementations of core ports.

aws/ holds the real backends (DynamoDB waitlist store); memory/ holds the
in-memory stand-ins for tests and the development server, mirroring mailer's
adapters/ split. This layer may import boto3; it may never import Flask or
the api/entrypoints layers (enforced by tests/test_architecture.py).
"""
