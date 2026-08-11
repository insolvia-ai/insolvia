# services/admin

Cross-tenant firm administration — the service the internal admin portal
talks to ([#212](https://github.com/insolvia-ai/insolvia/issues/212), closing
the gap [#178](https://github.com/insolvia-ai/insolvia/issues/178) described:
onboarding a firm was a hand-run script with no audit trail).

Staff authenticate with their **Google Workspace** account (direct OIDC —
no separate credentials, no staff Cognito pool); every mutation is recorded
in an append-only audit table naming who did what to which firm.

## Surface

| Route | Does |
|---|---|
| `POST /v1/firms` | Provision a firm + its first administrator (Cognito invite email carries the temp password) |
| `GET /v1/firms` | Every firm: status, seat count, provisioning provenance |
| `GET /v1/firms/<id>` | One firm |
| `PATCH /v1/firms/<id>` | Suspend / reactivate (`{"status": ...}`) |
| `GET /v1/firms/<id>/users` | A firm's staff list |
| `POST /v1/firms/<id>/users/<subject>/resend-invite` | Fresh temp-password email for an un-onboarded user |
| `GET /health` | Public; smoke checks assert the environment |

## Running

```bash
./services/admin/scripts/dev-setup.sh   # venv with pinned deps
./services/admin/scripts/dev-test.sh    # ruff + mypy + pytest (the CI gate)
./services/admin/scripts/dev-up.sh      # compose stack on :8090
./services/admin/scripts/dev-down.sh    # stop it (containers outlive Ctrl-C)
```

The root `./scripts/dev-up.sh` includes this service (and the portal) when
bringing the whole system up.

The suite and bare dev server run fully in-memory without
`services/admin/.env`; `./scripts/dev-aws-setup.sh` writes it to point at this
machine's real dev tables and pool.

The seeder (`insolvia_admin.entrypoints.seed`) also lives here — the
dev/staging fixture loader `scripts/dev-aws-seed.sh` and the staging e2e use.
It refuses prod by design; prod provisioning is this service's job.
