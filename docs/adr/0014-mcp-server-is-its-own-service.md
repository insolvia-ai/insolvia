# ADR 0014 — The MCP server is its own service

- **Status:** Accepted
- **Date:** 2026-09-01
- **Relates to:** implements the surface of
  [`docs/reference/mcp-surface.md`](../reference/mcp-surface.md) (issue
  [#260](https://github.com/insolvia-ai/insolvia/issues/260)); sits behind
  [ADR 0001](0001-client-stays-dumb-trust-boundary.md)'s trust boundary either
  way; triggers [ADR 0012](0012-shared-python-domain-package.md)'s admission
  rule; follows [ADR 0011](0011-cross-tenant-administration-is-a-separate-principal-class.md)'s
  precedent for a second surface over shared domain code.

## Decision

**The MCP server of [ADR 0013](0013-mcp-server-replaces-direct-pms-integration.md)
is a separate Python service, `services/mcp` — its own Lambda, its own image,
its own hostname — not a set of routes inside `services/api`.** It shares
domain code the way `services/admin` does: through `insolvia_core`, and the
case domain (stores, parse functions, provenance enforcement) **moves from
`insolvia_api` into `insolvia_core` in the PR where `services/mcp` first
imports it** — which is exactly the event ADR 0012 named as the admission
rule's trigger.

Both candidates were fully behind ADR 0001's trust boundary; this was a
question of packaging, not of who may touch the stores.

## Context

The alternative was attractive: mount the MCP endpoint inside `services/api`.
Zero refactor — the case stores, parse functions, and provenance enforcement
live there today — one deploy, one Lambda, one CloudFront/API Gateway path,
and no new infra in three environments. For a team of one, that is not a
strawman; it is the default the decision had to beat.

It was beaten on four grounds:

1. **It is a different protocol with its own server obligations.** The API is
   REST: Flask routes, one request in, one JSON out, errors as statuses. MCP
   is JSON-RPC over Streamable HTTP with protocol-version negotiation, spec
   error codes, header/body validation, and era compatibility for older
   harnesses — the reason to build on the official MCP SDK rather than Flask.
   The resource server also owes the world well-known documents (RFC 9728
   protected-resource metadata) at *its* canonical URI. Interleaving two
   protocol stacks in one app factory makes `tests/test_architecture.py`'s
   layering — the thing that keeps `api` depending only on `core` — describe
   two services pretending to be one.

2. **Token audience separation stays structural.** `services/api` verifies
   exactly one `client_id` — the app's — and that exactness is the audience
   check, because Cognito access tokens carry no `aud`. MCP clients are
   different OAuth clients with different scopes and different lifetimes.
   One service accepting both would widen the human app's own gate to admit
   agent tokens; two services each verify their own client-id set, and an app
   token presented to the MCP endpoint (or vice versa) fails closed with no
   code asked to distinguish the cases.

3. **Blast radius and release cadence.** Agent traffic is bursty, retry-happy,
   and shaped by harness behaviour we do not control; 12.5 is explicitly an
   experiment against third-party clients. A misbehaving harness should be
   able to exhaust *its* Lambda's concurrency without an attorney's intake
   autosave failing beside it — and throttles/alarms tuned for agents should
   not sit on the human path. The same isolation applies at deploy time: the
   MCP surface will churn while harnesses are verified, and each of those
   deploys should not redeploy the API the app depends on. CI's changed-path
   filters and the release pipeline already work per-service; a merged
   service would couple the cadences permanently.

4. **The repo already decided this pattern twice.** ADR 0011 gave
   cross-tenant administration its own service so that "what may this surface
   do" is answered by what its dependency exports, not by discipline inside a
   shared codebase. ADR 0012 then built `insolvia_core` precisely so a
   sibling Python service is cheap and drift-free, and wrote the admission
   rule this decision now triggers: code moves in when a second service
   actually imports it. A second *protocol* surface over the same domain is
   the same shape as a second *principal* surface; declining to use the
   machinery built for it would need a better reason than saving a directory.

## Consequences

- **The case domain graduates to `insolvia_core`.** Stores, entity parsers,
  `provenance.py`, `access.py` — moved verbatim in 12.3's opening PR, proven
  by the API's own test suite passing unedited except for imports (the
  ADR 0012 standard). The confirm-before-entry invariants then live in one
  package both services structurally share, which is where invariants
  described as "enforced in the core layer's parse functions, so that every
  write path inherits them" always wanted to be. Until that PR lands, nothing
  moves — no speculative extraction.
- **New service scaffolding, three environments.** `services/mcp` follows the
  `insolvia-new-package` shape: its own image built from the repo root, its
  own CI legs, its own Terraform (`insolvia-<env>-mcp-*` naming), and a
  `dev` wiring so the server runs against this machine's real dev table like
  the API does. That is the cost accepted; it is bounded and it is paid once.
- **A core change redeploys three services, not two.** `services/mcp` joins
  the changed-path filters and release regexes that already name
  `packages/insolvia_core/`, in the PR that adds the dependency (the ADR 0012
  consequence, now ×3).
- **The MCP hostname is a real name.** The canonical resource URI in the
  protected-resource metadata, the OAuth `resource` parameter harnesses send,
  and the URL in every MCP directory listing (12.6) are the same string;
  giving the service its own hostname keeps that string stable however the
  API's routing evolves.
- **Two Lambdas read the same tables.** The IAM story stays reviewable — two
  execution roles with data-store access instead of one, each scoped to what
  its service exports, matching what ADR 0011 already did for the admin
  service.

## What would reopen this

The MCP surface converging on the API's shape — if MCP tooling someday mounts
cleanly as plain HTTP routes with no protocol machinery of its own, the
duplicate scaffolding stops buying isolation and starts being drag. Or the
opposite failure: the case-domain move proving too entangled to extract
verbatim, in which case the honest fallback is the in-API mount with this ADR
superseded, not a half-moved domain package.
