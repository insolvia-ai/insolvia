# ADR 0025 — Shared infrastructure and the mailer move to their own repository, `insolvia-services`

- **Status:** Accepted (2026-10-11, at the maintainer's request)
- **Date:** 2026-10-11
- **Relates to:** [ADR 0010](0010-design-system-moves-to-its-own-repository.md)
  (the last time something left this repo, and the reasoning about
  "two truths" that this move is careful not to recreate);
  [ADR 0001](0001-client-stays-dumb-trust-boundary.md) (unchanged: the API is
  still the one trust boundary, and it is still here). The cutover procedure
  is insolvia-services'
  [`docs/runbooks/state-migration.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/state-migration.md).

## Decision

**The account-wide infrastructure and the mailer leave this repository for
[`insolvia-ai/insolvia-services`](https://github.com/insolvia-ai/insolvia-services)
(public).** This repository becomes the Insolvia app, the marketing site, and
the app's own backend.

| Moves to insolvia-services | Stays here |
|---|---|
| `infra/envs/shared`: zone, wildcard cert, SES domain identity, **every** service's ECR repository, the fixture bucket | `apps/*`, `packages/*`, `forms/`, `seeds/`, `e2e/` |
| `infra/envs/ci-trust`: the OIDC provider, the deploy role, the staging seed role | `services/api`, `admin`, `mcp`, `filing` |
| `infra/envs/account-access`: human IAM users and groups | `infra/envs/dev`, `staging`, `prod`: every app stack |
| `services/mailer`, plus its Terraform module and its staging/prod stacks, in new mailer-only roots | `infra/modules/*` except `mailer`, `email`, `dev_fixtures` |

Five rules keep the two repositories honest:

1. **They reference each other by name, never by state.** This was already
   this repo's rule between Terraform layers (`data` lookups, never
   `terraform_remote_state`). It now also spans repositories:
   - insolvia's env roots look up the zone, the cert and the ECR repositories.
   - the mailer looks up `insolvia-<env>-api-role`.
   - insolvia constructs `MAILER_API_URL` from the mailer's hostname instead of
     reading the mailer module's output.

   Nothing in one repo's code imports the other's.
2. **One deploy role, trusted by both repositories.** They deploy into one
   account and one state bucket. Splitting the role would mean two copies of
   about forty scoped statements, which drift, for no isolation gain: the
   boundary between the repos is which state keys and workflows each holds,
   not IAM. The **seed role trusts insolvia only**, because it writes app data.
3. **The shared roots kept their state keys.** Moving `shared`, `ci-trust` and
   `account-access` was a code move, not a state move.
4. **The mailer's addresses did not change.** It is `module.mailer` in its new
   roots exactly as it was here. The state moved between keys under a backup,
   verified by plans in both roots; no resource was recreated.
5. **New service order spans repos.** A new service's ECR repository is
   created in insolvia-services' `shared`, and must release there before the
   insolvia PR that deploys the service.

## Context

The maintainer asked for the split. The reason is to separate the slow-moving,
account-wide layer, plus a platform service with no app-specific code, from
the product that changes daily. Three things made it cheap:

- **The mailer was already a platform.** It has no imports from this repo, no
  `insolvia_core`, its own CI job, and exactly one caller, which is registered
  by IAM role name.
- **The shared roots were already a separate layer.** They have their own state
  keys, and were already reached only through name lookups.
- **The coupling that did exist was one Terraform edge.** Inside one root, the
  mailer read the API module's role name, and the API's SSM parameter read the
  mailer module's URL. Both are names that can be written down. The "cycle"
  comment that justified the env-level SSM parameter no longer applies.

## Consequences

- **Two release pipelines, one account.** insolvia's `release.yml` lost its
  `shared` and `mailer` legs, and the API no longer waits on the mailer at
  deploy time. insolvia-services runs its own release: shared, then mailer
  staging, a staging-green stamp, the production gate, then mailer prod.
  Its commit status is `insolvia-services/staging-release`, distinct from
  `insolvia/staging-release`, so neither repo can promote on the other's
  evidence.
- **Local development still runs the whole system.** `scripts/dev-up.sh`
  finds the mailer in a sibling `insolvia-services` checkout, via
  `scripts/services-checkout.sh` or `$INSOLVIA_SERVICES_DIR`. Without one it
  says so and leaves the mailer out, and the API records mail in memory, as it
  always did when `MAILER_API_URL` was unset. The mailer's own local stack
  (Mailpit) lives with it.
- **The trust anchor is edited there now.** An AccessDenied in an insolvia
  deploy is fixed by a PR to insolvia-services' `infra/envs/ci-trust`,
  followed by a human apply from that repo. The skill
  `insolvia-deploy-role-permissions` here is now a pointer.
- **A transitional `removed` block.** insolvia's staging and prod roots carry a
  `removed { from = module.mailer; lifecycle { destroy = false } }` block until
  both have applied after the move. It turns the one dangerous ordering — this
  code applied while the state still held the mailer — into "forgotten" instead
  of "destroyed". Delete it after that.
- **Required checks moved.** `Mailer service` and `Terraform validate (shared)`
  leave this repo's ruleset; insolvia-services requires them, plus
  `(ci-trust)`, `(account-access)`, `(staging)` and `(prod)` for its own roots.
- **Fresh history.** insolvia-services starts from a single import commit that
  names this repo's commit it was cut from. `git log` for the moved files
  before 2026-10-11 is here.

## Weighed and rejected

- **Moving every backend service** (api, admin, mcp, filing, `insolvia_core`),
  which the name "services" might suggest. Rejected for now. Those are the
  app's own backend: they share the case domain through `insolvia_core`, are
  released together with the app, and change in the same PRs as the screens
  that call them. Moving them would turn most feature PRs into cross-repo pairs.
- **Two deploy roles.** Rejected for rule 2's reason. If the repos ever deploy
  into separate accounts, that is the time.
- **Keeping the mailer's resources in insolvia's state, and pulling the module
  from insolvia-services by git ref.** Rejected: one repo would apply code it
  does not own, at a ref it has to bump by hand. Moving the state is a one-off
  with a backup; the indirection would be permanent.
- **`import` blocks instead of a state move.** About 60 resources per
  environment, several of whose ids (API Gateway integrations and routes,
  event-source-mapping UUIDs, SNS subscription ARNs) are not derivable from
  names. That is more surface to get wrong than moving state that is already
  correct.
