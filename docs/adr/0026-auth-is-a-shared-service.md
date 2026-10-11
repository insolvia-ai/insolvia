# ADR 0026 — Auth is a shared service: the Cognito pool moves to `insolvia-services`

- **Status:** Proposed (2026-10-11). The direction is the maintainer's decision,
  taken after [ADR 0025](0025-shared-infra-and-mailer-move-to-insolvia-services.md)
  merged. The split below, what crosses the boundary and where each piece
  lands, is this ADR's proposal. It is accepted when the maintainer merges it.
- **Date:** 2026-10-11
- **Amends:** [ADR 0025](0025-shared-infra-and-mailer-move-to-insolvia-services.md).
  Auth joins the list of what moves, and rule 1 gains a third name lookup.
  0025 is otherwise unchanged.
- **Relates to:** [ADR 0020](0020-the-brand-is-a-consumer-owned-override.md)
  (why the branding stays here), [ADR 0023](0023-client-portal-identity-and-isolation.md)
  and [ADR 0016](0016-mcp-server-is-its-own-service.md) (the disjoint client ids
  that are now looked up by name), [ADR 0009](0009-a-case-belongs-to-a-firm.md)
  (why a recreated pool is a data migration, not a redeploy). The cutover
  procedure is insolvia-services'
  [`docs/runbooks/auth-state-migration.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/auth-state-migration.md).

## Decision

**The Cognito user pool, its hosted domains and its app clients leave this
repository for
[`insolvia-ai/insolvia-services`](https://github.com/insolvia-ai/insolvia-services).
This includes the per-machine dev pools.** What only this app needs stays here:
its sign-in branding, and the grants that let its Lambdas invite users.

| Moves to insolvia-services (`module.auth`, same addresses) | Stays here (`module.auth`, same addresses) |
|---|---|
| `aws_cognito_user_pool.main` (prod's has deletion protection) | `aws_cognito_managed_login_branding.web` / `.portal`, plus the settings JSON and the marks `npm run tokens` generates |
| `aws_cognito_user_pool_domain.main` (prefix) and `.custom` (`staging-auth.insolvia.ai`), with `aws_route53_record.auth` | `aws_iam_role_policy.api_invite` / `.admin_invite` (one `AdminCreateUser` grant each, on this repo's own roles) |
| `aws_cognito_user_pool_client.web`, `.portal`, `.mcp[*]` | name lookups for the pool and its clients, behind the module's **unchanged** outputs |
| per-machine dev pools: a new insolvia-services `infra/envs/dev` root, applied by its `scripts/dev-aws-auth.sh` | the SSM parameters, the `auth_*` env outputs, and every consumer of them |

Six rules follow from that split.

1. **This repo finds the pool and its clients by name.** These names are now
   the contract between the two repos, alongside the zone, the cert, the ECR
   repositories and the API role from 0025:

   | Name | Read as |
   |---|---|
   | `insolvia-<env>-users` | the pool, which must be the **only** pool with that name (a postcondition checks this) |
   | `insolvia-<env>-app` | the web client (`AUTH_CLIENT_ID`) |
   | `insolvia-<env>-portal` | the portal client (`AUTH_PORTAL_CLIENT_ID`) |
   | `insolvia-<env>-mcp-*` | every MCP harness, which together make up `AUTH_CLIENT_IDS` |

   A rename, a missing client or a duplicate pool fails this repo's plan
   before anything applies.
2. **The addresses do not change on either side.** `module "auth"` is still
   called here, as the consumer half, and its branding and grants never move
   in state. The other seven resource blocks move to insolvia-services under
   the same address. The move uses a backup, plans in both roots, and restores
   automatically if anything looks wrong. A planned create, destroy or replace
   of a pool or client is a hard stop at every step: the migration script
   refuses it, and insolvia-services' deploy workflows refuse it unless a
   human dispatches with an explicit override.
3. **Grants live with the consumer.** This repo grants its own roles
   `AdminCreateUser` on the pool, which it finds by ARN through the lookup.
   The shared service never learns a consumer's role name.
4. **The brand stays with the consumer.** Managed-login branding is set per
   app client, and the client belongs to this app. The generator
   (`tool/reconcile-cognito-branding.ts`, `tool/render-brand-marks.ts`), its
   inputs (`brand/`) and its gate (`npm run tokens:check`, the
   `Cognito branding` check) do not move and do not change.
5. **Pool configuration is insolvia-services'.** That covers callback origins
   (including staging's `http://localhost:3000`), the custom domain, deletion
   protection, the SES sender and the MCP inspector client. A new client this
   repo must read is a PR there, released there, before the PR here that reads
   it. This is 0025 rule 5's ordering, applied to clients.
6. **Local development uses the same lookup path as staging and prod.**
   `scripts/dev-aws-setup.sh` applies this machine's pool from the sibling
   insolvia-services checkout first, then this repo's dev root, which looks the
   pool up by name.

## Context

The maintainer decided that authentication is a shared service. It is one of
the slow-moving things in the account, with the longest-lived data in the
system: every account, and every subject our rows are keyed on. It should not
share a state file with the code that changes daily. A prod apply here
touches about a hundred resources, and keeping the pool out of that state
narrows what a bad plan can reach.

Before choosing a shape, we inventoried every consumer of `module.auth`.
Coupling was confined to a few places, and every value that crosses is a
public identifier that can be read back from AWS by name:

| Consumer | Value | Decision |
|---|---|---|
| `aws_ssm_parameter` `/insolvia/<env>/api/auth-{issuer-url,user-pool-id,client-id,portal-client-id}`, `/mcp/auth-{issuer-url,client-ids}` | issuer, pool id, client ids | Stay here. The values come from the lookups, and the output names are unchanged |
| `module.admin_service` `firm_user_pool_id` | pool id | unchanged (lookup-backed output) |
| env outputs `auth_*` → `api-staging.yml`, `app-staging.yml`, `app-prod.yml`, `mcp-staging.yml`, `.github/actions/seed-staging`, `e2e/` | domain, client ids, pool id/ARN | unchanged names, so **no workflow changes** |
| `auth_domain` (app build, E2E) | custom domain if one exists, else the prefix domain | read from the pool (`custom_domain`/`domain`), so prod's future `auth.insolvia.ai` needs no change here |
| managed-login branding + generator + `Cognito branding` check | settings JSON, SVG marks | **stays** (rule 4) |
| `api_invite` / `admin_invite` | pool ARN | **stays** (rule 3) |
| insolvia-services `ci-trust` staging seed role, `staging_user_pool_arn` | pool ARN (default hard-codes it) | unchanged value. Its documentation now points at insolvia-services' staging output |
| deploy role's Cognito statements | `userpool/*` | unchanged (one role, both repos) |
| per-machine dev pools, `scripts/dev-aws-*` | pool, clients | **move** (rule 6, see below) |

**What a live check found.** Prod's pool has only `insolvia-prod-app`. The
portal and `mcp-claude` clients from main have never been applied to prod. If
prod were moved now, its first plan in insolvia-services would create them,
which is exactly the kind of planned client change rule 2 refuses. A read-only
prod rehearsal confirms this. **Moving prod therefore waits until a normal
promotion applies main to insolvia's prod.** This is the same lag ADR 0025's
mailer move ran into.

## Consequences

- **A third cross-repo ordering.** insolvia-services' env root must apply
  before this repo's, because the lookups need the pool to exist. In steady
  state this is already true. In a fresh account it creates a circle, because
  the mailer in that same root looks up this repo's API role. The
  bootstrap breaks it with a targeted first apply of `module.auth` (that
  repo's `aws-bootstrap.md`).
- **The branding's ordering guarantee is weaker for new pools.** The domain
  used to `depends_on` the branding, so "managed login on, nothing to render"
  could not happen. A `depends_on` cannot cross repositories. Live clients
  all have their style, so nothing changes in steady state. A brand-new
  pool, such as a new environment or a new developer machine, shows "Login
  pages unavailable" between the two applies. In every such case nobody is
  signed in yet, and `dev-aws-setup.sh` runs the two applies back to back. A
  replaced client would lose its style the same way, which is one more reason
  replacement is a hard stop.
- **Local sign-in needs the insolvia-services checkout.** The mailer can fall
  back to recording mail in memory. Auth has no stand-in, so
  `dev-aws-setup.sh` stops and gives the clone command when the checkout is
  missing. It uses the same sibling-checkout lookup `dev-up.sh` already uses
  for the mailer (`scripts/services-checkout.sh`).
- **Each developer machine moves once.** `migrate-module-state.sh dev` moves
  the machine's pool, users included, under the same backup and verification
  as staging and prod. Until it runs, insolvia-services'
  `dev-aws-auth.sh apply` refuses to create a second pool.
- **Transitional `removed` blocks.** All three env roots carry seven
  `removed { … destroy = false }` blocks, one per address that left, until
  they have applied after the move. They protect the window where the new
  code could plan against a state that still holds the pool. In that window a
  forgotten pool can be recovered, and a destroyed one cannot.
- **insolvia-services gains a `dev` root, its first.** ADR 0025 said it had
  none because the mailer runs under Docker. That remains true for the
  mailer.

## Weighed and rejected

- **Move the whole module, branding and grants included.** This keeps the
  state move to one address and keeps the domain-after-branding
  `depends_on`. It was rejected because the branding is generated here from
  this repo's brand, and putting the output in another repo leaves two bad
  options. One is a generator that writes into a sibling checkout, with a CI
  gate that can no longer see its output. The other is a `brand/` copy in
  insolvia-services, which recreates the "two truths" that
  [ADR 0010](0010-design-system-moves-to-its-own-repository.md) removed.
  Moving the grants would make insolvia-services name this repo's roles,
  and in a fresh account that is a second, harder cycle (pool ↔ API role).
- **An SSM-published contract** (insolvia-services writes
  `/insolvia-services/<env>/auth/*`, and this repo reads it). This is a
  second copy of values AWS already holds by name. It can go stale if the
  publisher forgets, and it would be the only cross-repo seam that is not a
  name lookup. The deciding argument is that a lookup reads the pool itself.
  SSM would remain the right shape for something not addressable by name,
  and nothing here is.
- **`terraform_remote_state`.** Rejected by 0025 rule 1, for the same reason.
- **Keep the dev pools here and use insolvia-services' module by git ref.**
  ADR 0025 rejected this for the mailer: this repo would apply code it does
  not own, at a ref it must bump by hand. It is also worse for local
  testing, because the dev root would wire the module's outputs directly. A
  developer machine would then never exercise the by-name lookups that
  staging and prod depend on, and a broken lookup would first fail on
  staging.
- **Keep the dev pools here with a copy of the module.** That makes two
  definitions of the pool, and the dev one would drift from what staging and
  prod run.
- **Point local development at the staging pool** (staging already registers
  `http://localhost:3000`). Per-machine isolation is the point of
  `infra/envs/dev`. A shared pool means shared test accounts and subjects
  that collide across machines, and the dev firm tables would key rows on
  staging identities.
- **Auth in its own insolvia-services root**, separate from the mailer.
  This would remove the fresh-account circle (the mailer's API-role lookup)
  and give the pool its own state. It was rejected for now because it would
  mean a third root per environment, plus a workflow leg and a required check
  for each, to save one targeted apply that runs once per account. If the
  pool ever wants its own apply cadence or approver, this is the shape to
  use, and the addresses would not change.
- **`import` blocks instead of a state move.** About eight instances per
  environment, all importable by id. Unlike the mailer, the ids are known.
  Rejected anyway: an import needs the new root's configuration to match the
  live pool exactly on the first apply. The state move proves the same thing
  with a plan before anything is written, and restores automatically if it
  does not.
