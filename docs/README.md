# Insolvia docs

Engineering reference, runbooks, decisions, and the business plan. Repo-wide
conventions live in the root [`../CLAUDE.md`](../CLAUDE.md); the rules for
adding to this directory are in [`CLAUDE.md`](CLAUDE.md).

Documents are filed by **how they are read**: durable explanation, executable
procedure, decision, or business artifact.

## [`plan.md`](plan.md) — the living plan

What shipped, the decisions in force, and the open work. The one document here
that is rewritten as work lands.

## [`reference/`](reference/) — how the system works

| Doc | What |
|---|---|
| [`architecture.md`](reference/architecture.md) | Monorepo shape, toolchain (incl. the Expo free-tier constraint), environment model, hosting topology, CI/CD, PR-gate design + the required-check contract. |
| [`case-data-model.md`](reference/case-data-model.md) | The case schema: entities mapped to B101/B106/B107, per-field provenance and the confirm-before-entry invariant, derived values, the external-system seam (an origin pointer since [ADR 0013](adr/0013-mcp-server-replaces-direct-pms-integration.md)). |
| [`effective-dating.md`](reference/effective-dating.md) | How form revisions and regulatory datasets are versioned: series, immutable effective-dated releases, date resolution, float-then-pin on cases, and the scheduled-refresh pattern. |
| [`mcp-surface.md`](reference/mcp-surface.md) | The MCP tool surface over the case domain: the eight tools with schemas, the candidate-write flow (agent writes land as candidates, confirmed only by humans), permission gating, pagination, error vocabulary. Placement: [ADR 0016](adr/0016-mcp-server-is-its-own-service.md). |
| [`terraform.md`](reference/terraform.md) | Infra state model for this repo's roots, modules, naming, deploy order across the two repositories, and where the mailer's state went. |
| [`package-publishing.md`](reference/package-publishing.md) | How this repo *consumes* the design system and tokens — published from `insolvia-ai/design-system`, installed by version on both surfaces, plus the registry auth and the bundler wiring each consumer owns. |

## [`runbooks/`](runbooks/) — procedures you execute

| Runbook | What | State |
|---|---|---|
| [`december-1-forms-cycle.md`](runbooks/december-1-forms-cycle.md) | The annual Official Forms amendment cycle: watch, re-diff, register, release, verify — with a per-cycle log. | Live; walked against the Dec 1 2026 cycle; run every cycle |
| [`app-deploy-verification.md`](runbooks/app-deploy-verification.md) | Six checks proving a host serves the app, and the right build. | Both envs verified; re-run per deploy |
| [`staging-e2e-setup.md`](runbooks/staging-e2e-setup.md) | One-time setup for the post-deploy suites against staging: the seed role's grants (a human `ci-trust` apply, in insolvia-services), the shared password, and publishing the seed fixture. | **Open** — re-run step 1 for the case grants ([ADR 0021](adr/0021-test-tiers-and-seed-fixtures.md)) |

### Moved to insolvia-services

Shared infrastructure and the mailer left this repo on 2026-10-11
([ADR 0025](adr/0025-shared-infra-and-mailer-move-to-insolvia-services.md)),
and their documents went with them:
[`reference/email.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/reference/email.md) (`insolvia.ai` mail — address
map, DNS, SES outbound),
[`runbooks/aws-bootstrap.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/aws-bootstrap.md) (one-time
AWS/GitHub bootstrap and the ci-trust anchor),
[`runbooks/iam-mfa-rotation.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/iam-mfa-rotation.md),
[`runbooks/ses-production-access.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/ses-production-access.md)
(the SES sandbox exit), and
[`runbooks/state-migration.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/state-migration.md) (the
cutover).

## [`adr/`](adr/) — decisions and their reasoning

Durable decisions that stay expensive to re-litigate. See
[`adr/README.md`](adr/README.md) for the register and the numbering rule.
Shorter-lived planning decisions live as `D<n>` entries in [`plan.md`](plan.md).

## [`research/`](research/) — dated audits and investigations

Point-in-time findings, each named with its date and citing the owner of
every fact it reports. They own nothing: what they find is fixed in its owner
(usually `plan.md` or an ADR), and the file stays as the record of what was
found when.

| Doc | What |
|---|---|
| [`decision-status-2026-10-13.md`](research/decision-status-2026-10-13.md) | What is decided, superseded, half-built and open across `plan.md`, the ADRs, the business plan and the open issues — with the inconsistencies between them, since fixed in their owners. |
| [`self-filer-regulatory-exposure-2026-10-13.md`](research/self-filer-regulatory-exposure-2026-10-13.md) | Whether a paid, non-attorney product for debtors filing their own bankruptcy is a §110 petition preparer or unauthorized practice of law — *Reynoso*, Upsolve, the postures that avoid it, and its impact on Insolvia feature by feature and on ADRs 0009–0024. **Outcome: a non-starter — the self-filer pivot is not going ahead.** Not legal advice. |
| [`single-tenancy-assumptions-2026-10-21.md`](research/single-tenancy-assumptions-2026-10-21.md) | Every place the model assumes one firm, client, district or chapter (#468) — the portal binding's one-case/one-firm keying, the shared pool's one-firm-per-person rule, the Chapter-7-only district `opening` record and launch-state loader, Chapter 11's missing forms, the `client` naming collision, the thin seed data — each with file:line and its reversal cost. Pool case sensitivity: already resolved. |
| [`chapter-11-13-form-gaps-2026-10-21.md`](research/chapter-11-13-form-gaps-2026-10-21.md) | For an individual's Chapter 13 and Chapter 11 case (small business and Subchapter V included), which official forms apply beyond the Chapter 7 set, and what each needs that the case model lacks. Chapter 13's gaps are inside B113 (plan versions, classes, lien avoidance, nonstandard provisions); Chapter 11's are whole entities (plan classes and votes, insiders, the operating business, the 425C monthly report). For #469. Not legal advice. |
| [`conversion-paperwork-2026-10-25.md`](research/conversion-paperwork-2026-10-25.md) | What a debtor's attorney files after a 7 → 13, 13 → 7 or → 11 conversion and by when, which deadlines reset and what each counts from (the conversion date, or the first date set for the post-conversion § 341 meeting), what the case model can produce, and the new records it needs (post-petition debts, post-petition property and contracts). Checks ADR 0027's Context against the restyled rules. For #497. Not legal advice. |
| [`dotnet-mcp-and-anthropic-sdks-2026-10-27.md`](research/dotnet-mcp-and-anthropic-sdks-2026-10-27.md) | Whether the official C# MCP SDK (2.3.0) and Anthropic SDK (13.3.0) can replace what `services/mcp` and the Anthropic adapters do (#515, plan 18.4): the `2026-07-28` stateless protocol and bearer hook against the pinned `test_protocol.py` cases, and request-body parity with the Python adapters on `claude-opus-5`. **Outcome: both usable**; MCP needs a JSON-response shim and Host/Origin checks. |
| [`dotnet-pdf-engine-2026-10-27.md`](research/dotnet-pdf-engine-2026-10-27.md) | Spike #514 (plan 18.3): PDFsharp against pypdf on the fill engine. All 22 forms, the widget-state escape hatch and B2010's stamps are field-equivalent, with byte-equal appearance streams and a 0.0000% render diff. The .NET fill is 4 to 18 times faster. **PDFsharp ≥ 6.5 is AGPL; 6.2.4 is the last MIT release, so pin it.** Covers the pypdf quirks that were mirrored, the determinism workarounds and the recommendation for 18.16. |
| [`cmecf-training-access-2026-10-21.md`](research/cmecf-training-access-2026-10-21.md) | What ADR 0024 PR 10 needs from each of the ten launch districts' CM/ECF training databases: which publish one, who may request access, lead time, whether it mirrors live, whether XML case opening is exposed (PR 12), and the login-sharing rules — per district, each claim marked primary, secondary or not found. |

## [`agents/`](agents/) — config for the engineering skills

Read by the mattpocock skills (`triage`, `to-tickets`, `to-spec`, `wayfinder`, …), not
by people: [`issue-tracker.md`](agents/issue-tracker.md) (GitHub Issues),
[`triage-labels.md`](agents/triage-labels.md) (the five defaults),
[`domain.md`](agents/domain.md) (single-context: root `GLOSSARY.md`, created lazily,
plus [`adr/`](adr/)). Edit them directly; re-run `/setup-matt-pocock-skills` only to switch trackers.

## [`business/`](business/) — company artifacts

| Doc | What |
|---|---|
| [`business-plan.html`](business/business-plan.html) | What we're building and why. |
| [`regulatory-source-register.html`](business/regulatory-source-register.html) | Authoritative regulatory source per feature, with its refresh cadence. |
| [`switching-cost-and-import.html`](business/switching-cost-and-import.html) | What a firm switching to Insolvia can bring from its existing bankruptcy software: per-vendor export capabilities, three candidate import paths, and a recommendation to sequence a real importer after the client portal (issue 17.4). |

## Not in `docs/`

- **Area rules** — how to change a specific app/package/service — live in that
  area's own `CLAUDE.md` and `README.md`.
- **Which script to run when** — the `insolvia-scripts` skill and
  [`../scripts/README.md`](../scripts/README.md).
- **AWS auth, deploys, deploy-role permissions, branch protection,
  design-system PRs** — the `insolvia-*` skills. Docs here name them rather
  than restating them.
