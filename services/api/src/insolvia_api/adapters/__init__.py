"""Environment-specific implementations of core ports.

Empty today — the first adapters arrive with the waitlist endpoint: an aws/
package (DynamoDB store) and a memory/ package (in-memory store for tests and
the development server), mirroring mailer's adapters/ split. This layer may
import boto3; it may never import Flask or the api/entrypoints layers
(enforced by tests/test_architecture.py).
"""
