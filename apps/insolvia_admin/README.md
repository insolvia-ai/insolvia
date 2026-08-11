# apps/insolvia_admin

The internal staff portal — provisioning and administering law firms
([#214](https://github.com/insolvia-ai/insolvia/issues/214), the visible half
of closing [#178](https://github.com/insolvia-ai/insolvia/issues/178)).

Staff sign in with their Insolvia **Google Workspace** account; the portal
talks to `services/admin`. Screens: the firm list (status, seats,
provisioning provenance), a firm's detail (people, suspend/reactivate,
resend a stranded invite), and the provision form that replaces the shell
session #178 was written about.

## Running locally

```bash
./apps/insolvia_admin/scripts/dev-setup.sh   # packages auth + npm ci, once
./apps/insolvia_admin/scripts/dev-up.sh      # http://localhost:3100 — pinned
```

(Or the whole system at once: `./scripts/dev-up.sh` from the repo root now
includes this portal and the admin service.)

Sign-in works against the real dev Google client out of the box; the firm
data needs the admin service running (`./services/admin/scripts/dev-up.sh`,
port 8090) against your dev AWS layer (`./scripts/dev-aws-setup.sh`).

Gate (same as CI): `npm run typecheck && npm run lint && npm test && npm run build`.

Hosting (`admin.insolvia.ai` / `staging-admin.insolvia.ai`) lands with
[#215](https://github.com/insolvia-ai/insolvia/issues/215).
