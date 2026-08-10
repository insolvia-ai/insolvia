# insolvia_core

The shared domain of Insolvia's Python services: firms and tenancy, Cognito
access-token verification, and the AWS + in-memory adapters behind both.
Extracted from `services/api` ([#208](https://github.com/insolvia-ai/insolvia/issues/208))
so the tenant API and the admin service compose one firm domain instead of two
drifting copies — the DynamoDB item shapes live here in exactly one place.

Not published anywhere. Each consumer installs it by relative path from its
`requirements.txt` (`../../packages/insolvia_core`), and the service Docker
builds use the repo root as context so the same line resolves there.

## Layout

```
src/insolvia_core/
├── firms.py     the firm domain: entities, parsing, permissions, item shapes
├── auth.py      JWT verification for Cognito access tokens (issuer passed in)
├── errors.py    the error vocabulary consumers map to HTTP statuses
├── ports.py     FirmStore · UserDirectory · JwksProvider
└── adapters/
    ├── aws/     DynamoDB firm store · Cognito directory · JWKS fetcher
    └── memory/  in-memory stand-ins for tests and local dev servers
```

## Developing

The API's venv installs this package with everything pinned — set up via
`./services/api/scripts/dev-setup.sh`, then from this directory:

```bash
../../services/api/.venv/bin/pytest
```

The install is not editable: after changing this package, re-run
`pip install -r requirements.txt` in a consumer before running *its* tests.

CI gate: the `Core package` job in `.github/workflows/core-pr.yml` (ruff,
mypy `--strict`, pytest), plus every consumer's own suite — `api-pr.yml`
re-runs on any change here, because the package ships inside that image.
