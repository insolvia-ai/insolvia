# ADR 0026 — Auth is a shared service: the Cognito pools move to `insolvia-services`

- **Status:** Accepted (2026-10-12, by the maintainer). The direction, the
  SSM contract, the shared development pool and the human-applied dev pool
  are all the maintainer's choices, made on review of the first draft. The
  cutover ran the same day: staging and prod moved with no resource changes
  on either side, and the shared dev pool is live on `dev-auth.insolvia.ai`.
- **Date:** 2026-10-11, revised 2026-10-12
- **Amends:** [ADR 0025](0025-shared-infra-and-mailer-move-to-insolvia-services.md).
  Auth joins the list of what moves, and rule 1 gains a fourth cross-repo
  seam. 0025 is otherwise unchanged.
- **Relates to:** [ADR 0020](0020-the-brand-is-a-consumer-owned-override.md)
  (why the branding stays here), [ADR 0023](0023-client-portal-identity-and-isolation.md)
  and [ADR 0016](0016-mcp-server-is-its-own-service.md) (the disjoint client ids
  the contract carries), [ADR 0009](0009-a-case-belongs-to-a-firm.md) (why a
  recreated pool is a data migration, not a redeploy). The cutover procedure
  is insolvia-services'
  [`docs/runbooks/auth-state-migration.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/auth-state-migration.md).

## Decision

**The Cognito user pools, their hosted domains and their app clients leave
this repository for
[`insolvia-ai/insolvia-services`](https://github.com/insolvia-ai/insolvia-services).**
That repository publishes each pool's public identifiers as SSM parameters,
`/insolvia/<env>/auth/*`, and this repository reads them. Development gets
**one shared pool** in place of one per machine.

| Moves to insolvia-services (`module.auth`, same addresses) | Stays here |
|---|---|
| `aws_cognito_user_pool.main` (prod's has deletion protection) | `module.auth`'s `aws_cognito_managed_login_branding.web` / `.portal`, plus the settings JSON and marks `npm run tokens` generates |
| `aws_cognito_user_pool_domain.main` (prefix) and `.custom` (`staging-auth.insolvia.ai`), with `aws_route53_record.auth` | `module.auth`'s `aws_iam_role_policy.api_invite` / `.admin_invite` (one `AdminCreateUser` grant each, on this repo's own roles) |
| `aws_cognito_user_pool_client.web`, `.portal`, `.mcp[*]` | `modules/auth_contract`, the reader, behind `module.auth`'s **unchanged** outputs |
| `aws_ssm_parameter.contract[*]`, the contract itself | the API's SSM namespace, the `auth_*` env outputs, and every consumer of them |
| — | **new:** `infra/envs/dev-shared`, the shared dev pool's branding |

**The shared development pool** is `insolvia-dev-users`, in
insolvia-services' `infra/envs/dev`, signing in on `dev-auth.insolvia.ai`. A
custom domain per machine was never affordable; one shared pool makes it a
single domain, and gives a future identity provider a redirect URI we own. A human applies it there with
`scripts/apply-dev-auth.sh` when its configuration changes. Every
developer machine's `infra/envs/dev` reads it through the same contract as
staging and prod, and keeps its own tables, queues and buckets.

Seven rules follow.

1. **The contract is SSM, published by the owner.** insolvia-services'
   `modules/auth/contract.tf` writes these parameters, and
   `modules/auth_contract` here reads them with `data "aws_ssm_parameter"`:

   | Key | Value |
   |---|---|
   | `user-pool-id` | the pool's id |
   | `user-pool-arn` | the pool's ARN |
   | `client-id` | the web client's id |
   | `portal-client-id` | the portal client's id |
   | `domain` | the sign-in hostname: custom if set, else the prefix domain |
   | `issuer-url` | the issuer tokens are verified against |
   | `mcp-client-ids` | JSON `{harness: id}` |

   A missing parameter fails this repo's plan with the parameter's name in
   the error. Keys and value shapes are an interface: they change there
   first and are released before anything here reads them.
2. **No code crosses, and no checkout is needed.** Nothing in this
   repository runs, sources or reads a file from insolvia-services, locally
   or in CI. Local setup needs only AWS credentials, as it always has.
3. **The addresses do not change on either side.** `module "auth"` is still
   called here as the consumer half, so its branding and grants never move in
   state. Everything else moves under the same address, contract parameters
   included. A small prep PR has this repo's *current* module create those
   parameters, so they exist for the move's verification plan and travel
   with the module. The move uses a backup, plans in both roots, and restores
   automatically if anything looks wrong. A planned create, destroy or
   replace of a pool or client is a hard stop at every step.
4. **Grants live with the consumer.** This repo grants its own roles
   `AdminCreateUser` on the pool ARN it reads from the contract. The shared
   service never learns a consumer's role name.
5. **The brand stays with the consumer.** Branding is set per app client, and
   the clients serve this app. The generator, its inputs (`brand/`) and its
   gate (`npm run tokens:check`, the `Cognito branding` check) do not move.
   The shared dev pool's branding has exactly one owner,
   `infra/envs/dev-shared`, because a client carries one style and every
   machine would otherwise fight over it.
6. **Pool configuration is insolvia-services'.** That covers callback origins
   (staging's and dev's `http://localhost:3000`), the custom domain, deletion
   protection, the SES sender and the MCP inspector client. A new client this
   repo must read is a PR there, released there, before the PR here that
   reads it. This is 0025 rule 5's ordering, applied to auth.
7. **Development identities are shared; development data is not.** Every
   machine signs in against `insolvia-dev-users`, and every machine's tables
   stay its own. Nothing per machine deletes pool users.

## Context

The maintainer decided authentication is a shared service. It is the
slowest-moving part of the account, with the longest-lived data in it: every
account, and every subject our rows are keyed on. It should not share a state
file with code that changes daily.

**What crosses the boundary.** We inventoried every consumer of
`module.auth` before writing code. Every value that crosses is a public
identifier:

| Consumer | Value | Decision |
|---|---|---|
| `aws_ssm_parameter` `/insolvia/<env>/api/auth-{issuer-url,user-pool-id,client-id,portal-client-id}`, `/mcp/auth-{issuer-url,client-ids}` | issuer, pool id, client ids | Stay here; values come from the contract; output names unchanged |
| `module.admin_service` `firm_user_pool_id` | pool id | unchanged (contract-backed output) |
| env outputs `auth_*` → `api-staging.yml`, `app-staging.yml`, `app-prod.yml`, `mcp-staging.yml`, `.github/actions/seed-staging`, `e2e/` | domain, client ids, pool id/ARN | unchanged names, so **no workflow changes** |
| `auth_domain` (app build, E2E) | custom domain if one exists, else the prefix domain | published by the owner, so prod's future `auth.insolvia.ai` needs no change here |
| managed-login branding + generator + `Cognito branding` check | settings JSON, SVG marks | **stays** (rule 5) |
| `api_invite` / `admin_invite` | pool ARN | **stays** (rule 4) |
| insolvia-services `ci-trust` staging seed role, `staging_user_pool_arn` | pool ARN | unchanged value; its documentation points at insolvia-services' output |
| per-machine dev pools, `scripts/dev-aws-*` | pool, clients | **replaced** by the shared dev pool (rule 7) |

**Why SSM and not name lookups.** The first draft looked the pool and its
clients up by name, with `data "aws_cognito_user_pools"` and
`"aws_cognito_user_pool_clients"`. humbugg solved the same problem with
SSM parameters published by the shared stack, and the maintainer chose that
pattern for both products. It has three advantages:
- The contract becomes an explicit, owned list of keys, instead of a naming
  convention spread across both repos.
- A failure names the missing parameter.
- It avoids the edge cases of name lookups: Cognito allows duplicate pool
  names, so name lookups needed exactly-one and unique-client
  postconditions.

**Why a shared development pool.** The first draft moved each machine's pool
to insolvia-services. A per-machine pool has to be applied from the
developer's machine, so this repo's `dev-aws-setup.sh` had to drive the other
repository from a sibling checkout. That is a cross-repo code dependency, and
the maintainer rejected it. humbugg runs one shared dev pool, because a
federated identity provider registers exact redirect URIs. A per-machine
pool means a per-machine hosted domain, and so a console edit at every
provider for every new machine. Neither repo federates today, but the day it
does the shared pool is required anyway. And it removes the cross-repo
dependency outright, because the pool is applied there and only read here.

**What a live check found.** Prod's pool has only `insolvia-prod-app`. The
portal and `mcp-claude` clients from main have never been applied to prod.
**Moving prod therefore waits until a normal promotion applies main** (and
the prep PR) to insolvia's prod. The move script refuses otherwise.

## Consequences

- **A fourth cross-repo ordering.** insolvia-services' env roots apply before
  this repo's, because the contract must exist. In steady state this is
  already true. In a fresh account there is a circle: the mailer in that same
  root looks up this repo's API role. The bootstrap breaks it with a targeted
  first apply of `module.auth` (that repo's `aws-bootstrap.md` § 8).
- **The branding's ordering guarantee is weaker for new pools.** The domain
  used to `depends_on` the branding, so "managed login on, nothing to render"
  could not happen. A `depends_on` cannot cross repositories. Live clients
  all have their style, so nothing changes in steady state. A brand-new pool,
  such as a new environment or the shared dev pool's first apply, shows
  "Login pages unavailable" until this repo applies its branding. For dev,
  that is the first `dev-aws-setup.sh` after `apply-dev-auth.sh`.
- **Development accounts are shared across machines.** A test person seeded
  from one machine exists on every machine, with the password whoever
  created it set, so use the same `DEV_USER_PASSWORD` everywhere.
  `dev-aws-reset.sh` no longer deletes pool users.
- **Each existing machine replaces its pool once.** The next
  `dev-aws-setup.sh` destroys the machine's old `insolvia-dev-<id>-users`,
  because the code no longer declares it, and `dev-aws-seed.sh` recreates
  the account in the shared pool. There is no per-machine state move.
- **`dev-aws-setup.sh` applies a shared root** (`infra/envs/dev-shared`) on
  every run, before the machine's own stack. It is idempotent, and the S3
  lockfile serialises machines.
- **Transitional `removed` blocks.** Staging and prod each carry eight
  `removed { … destroy = false }` blocks, one per address that left, until
  both have applied after the move. They protect the window where the new
  code could plan against a state that still holds the pool, turning a
  mistake from a destroy into a recoverable forget. (Deleted on 2026-10-12,
  once both environments had released after the move with "No changes".)
- **insolvia-services gains its first `dev` root**, applied by a human.
  ADR 0025 said it had none because the mailer runs under Docker. That is
  still true for the mailer; auth has no local stand-in.

## Weighed and rejected

- **Name lookups** (`data "aws_cognito_user_pools"` / `"aws_cognito_user_pool_clients"`).
  This was the first draft. It is workable, but rejected for the
  explicit contract described in Context.
- **Per-machine dev pools moved to insolvia-services.** This was the first
  draft. It makes this repo's local setup drive another repository from a
  sibling checkout, which is a cross-repo code dependency, and it breaks
  federation.
- **Per-machine dev pools kept here, with a copy of the module.** That makes
  two definitions of the pool, and the dev one would drift. It still breaks
  federation.
- **Per-machine dev pools kept here, with insolvia-services' module by git
  ref.** ADR 0025 rejected this for the mailer: this repo would apply code it
  does not own, at a ref it must bump by hand.
- **The shared dev pool applied by CI.** That is a workflow, a job and a
  check for a pool whose configuration almost never changes. humbugg
  applies its shared pool locally too. It is easy to add later.
- **Move the whole module, branding and grants included.** The branding is
  generated here from this repo's brand. Moving its output either makes the
  generator write into another repo, with a gate that can no longer see the
  output, or needs a second `brand/` copy (the "two truths" that ADR 0010
  removed). Moving the grants would make insolvia-services name this repo's
  roles, a second and harder fresh-account cycle.
- **`terraform_remote_state`.** Rejected by 0025 rule 1.
- **Auth in its own insolvia-services root**, separate from the mailer, to
  remove the fresh-account circle. That would be a third root per
  environment, with its own workflow leg and checks, to save one targeted
  apply per account. If the pool ever wants its own apply cadence, this is
  the shape to use, and the addresses would not change.
- **`import` blocks instead of a state move.** That needs the new root's
  configuration to match the live pool exactly on its first apply. The state
  move proves the same thing with a plan before anything is written, and
  restores automatically if it does not.
