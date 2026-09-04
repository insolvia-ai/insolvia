# insolvia_core

The shared domain of Insolvia's Python services: firms and tenancy, Cognito
access-token verification, the case domain (cases, debtors, documents, the
generic case collections, provenance), and the AWS + in-memory adapters behind
all of it. The firm domain was extracted from `services/api`
([#208](https://github.com/insolvia-ai/insolvia/issues/208)) so the tenant API
and the admin service compose one firm domain instead of two drifting copies;
the case domain followed when the MCP service became its second importer
([ADR 0016](../../docs/adr/0016-mcp-server-is-its-own-service.md),
[#262](https://github.com/insolvia-ai/insolvia/issues/262)) — the DynamoDB
item shapes live here in exactly one place.

Not published anywhere. Each consumer installs it by relative path from its
`requirements.txt` (`../../packages/insolvia_core`), and the service Docker
builds use the repo root as context so the same line resolves there.

## Layout

```
src/insolvia_core/
├── firms.py           the firm domain: entities, parsing, permissions, item shapes
├── auth.py            JWT verification for Cognito access tokens (issuer passed in)
├── errors.py          the error vocabulary consumers map to HTTP statuses
├── cases.py           the case root: parsing, item shape, cursors, access scoping
├── access.py          Accessor + may_see_case — the one owner of who sees which case
├── access_log.py      the who-read-which-case audit event and item shape
├── case_entities.py   the generic case-collection record (issue #249)
├── case_collections.py  the entity-kind registry the generic routes/tools serve
├── debtors.py · documents.py · petitions.py · creditors.py · claims.py ·
│   assets.py · exemption_claims.py · codebtors.py · contract_leases.py ·
│   income.py · expenses.py · sofa.py    per-entity parse functions and bodies
├── fields.py          the shared field parsers those entities are built from
├── provenance.py      per-field provenance: parsing and the confirm-before-entry invariants
├── ports.py           FirmStore · UserDirectory · JwksProvider · CaseStore ·
│                      DocumentStore · DocumentBlobStore · DebtorStore ·
│                      CaseEntityStore · AccessLog
└── adapters/
    ├── aws/     DynamoDB stores · Cognito directory · JWKS fetcher · S3 blobs
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
