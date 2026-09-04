# ADR 0019 — Claude runs in the pipeline worker, on the Anthropic API's no-training standing

- **Status:** Accepted
- **Date:** 2026-09-04
- **Relates to:** decides what
  [docs/plan.md](../plan.md) §"Why extraction is its own milestone" says 9.7
  carries for 8.7 — where the model call runs, and the no-training /
  zero-retention configuration. Rides the pipeline shape of
  [ADR 0018](0018-sqs-queue-and-worker-lambda-over-step-functions.md) and
  ADR 0015's heavy-dependency rule. The review's *output* posture is bound
  by the provenance invariants in
  [case-data-model.md](../reference/case-data-model.md). Extraction
  (8.7–8.9) **inherits every decision here**; changing one for extraction
  means amending this ADR, not quietly diverging.

## Decision

The AI review agent (issue #97) — the repo's first Claude surface — lands
with these five decisions, made once here for every later model call:

1. **The model call runs inside `services/api`'s pipeline worker Lambda**,
   as the `petition_review` job kind — not a new service, not an endpoint.
2. **The Anthropic API is called directly** (the `anthropic` SDK, in the
   worker image only), authenticated by an API key in SSM SecureString
   `/insolvia/<env>/api/anthropic-api-key`, surfaced to the worker as
   `ANTHROPIC_API_KEY` by the existing deploy-time namespace derivation.
3. **The model is `claude-opus-5`**, one structured-output Messages call
   per review, constrained to a findings schema — never prose fished for
   JSON.
4. **Data posture: the API's no-training standing, tax identifiers never
   sent, zero-data-retention filed as a business follow-up** (details
   below).
5. **The review's output is advisory prose about existing values, attached
   to the job result — never case data.** No store row, no provenance
   entry, no candidate. A model output that *proposes a value* (extraction)
   enters only through the confirm-before-entry invariant; a review never
   proposes one at all.

## Context — each decision, honestly

### Where the call runs

8.7 was carrying "inside `services/api` vs its own service — latency against
the Lambda's limits". The pipeline resolved the latency half before this ADR
opened: a review is minutes-long work, which ADR 0015/0018 already banned
from the request path, and the worker Lambda's 900-second ceiling comfortably
holds one model call (capped at 240s per attempt, one SDK retry). A separate
service would buy an IAM principal, an image, and a deploy stage to fence a
dependency the worker image exists to carry. The reopen trigger is real
though: extraction over large document sets that cannot fit one worker
attempt is ADR 0018's own 15-minute trigger, and would reopen *that* ADR,
not the location of the SDK.

### Direct API, not Bedrock

Bedrock would keep traffic inside AWS IAM and the existing OIDC story. It
loses on the criterion that decided ADR 0018: the feature surface this
review leans on (structured outputs on current models, and the retention
controls below) reaches the first-party API first, documented against the
first-party SDK — and a maintainer's laptop calls the same API with the same
env var, which is the local story. Revisit if the org ever standardises on
Bedrock for procurement reasons; the seam (`core/ports.ReviewModel`) is one
adapter wide.

### Model choice

`claude-opus-5`: the review's entire value is catching *subtle*
cross-schedule inconsistencies a cheaper tier would miss, volume is tiny
(one call per review press), and — decisive for GLBA — the newest
research-tier model (`claude-fable-5`) is **not available under zero data
retention** (it requires 30-day retention), which would foreclose the
retention follow-up below. The model id lives in one place
(`adapters/anthropic/review_model.py`); extraction reuses or overrides it
there.

### Data posture (GLBA)

What leaves for the API is SSN-and-financials-adjacent by nature, so the
posture is layered:

- **No training, by standing.** Anthropic's commercial terms for API
  traffic: inputs and outputs are not used to train models. This is the
  default the account rides on, not a switch this repo can set — recorded
  here so nobody goes looking for the setting.
- **No tax identifiers, structurally.** The stores cannot hand a worker a
  full SSN/ITIN: `insolvia_core.debtors.parse_debtor` refuses `tax_id`
  outright (field-level encryption is not built), and claims carry account
  **last four** only. The review adds `core/petition_review.scrub` as
  defence in depth — tax-id-named keys dropped, SSN-shaped strings replaced
  — pinned by a test that greps the outbound document.
- **Zero data retention is a business follow-up, not a code change.** ZDR
  is an organization-level agreement with Anthropic (sales-negotiated), and
  the API key inherits it; nothing in this repo changes when it lands. Filed
  as a launch-checklist item: **before a design-partner firm's real case
  data flows, either the ZDR agreement is in place or the firm's engagement
  letter discloses the API's standard retention.** The model choice above
  deliberately stays ZDR-eligible.
- **Logs are metadata only**, both sides: the worker logs counts, token
  usage and identifiers, never document or finding content — the same GLBA
  rule the request log states.

### The output is not case data

A finding is one sentence pointing at a form and line. It lives on the job
result row, is read through the job status endpoint, and expires from
relevance the moment the case is edited (the worker refuses to review a
packet whose bytes no longer match the stored one, so findings always
describe an actual assembled packet). Writing findings into the case —
even as annotations — would create a second, unconfirmed voice inside the
data provenance model that case-data-model.md's invariants exist to keep
single. Extraction's difference: it *does* propose values, which is exactly
why its output enters as candidates under confirm-before-entry.

## Consequences

- **The local story:** the worker, gate, document builder and findings
  parser run under pytest with the memory `ScriptedReviewModel`; a
  maintainer with a real key exports `ANTHROPIC_API_KEY` in
  `services/api/.env` and the local worker poller runs the real call
  against this machine's dev stores. Without a key, `petition_review` jobs
  fail deterministically with `not_configured` — honest, and nothing else
  degrades.
- **The key is the one human-placed secret** (`aws ssm put-parameter
  --name /insolvia/<env>/api/anthropic-api-key --type SecureString ...`,
  one key per environment from the Anthropic Console). It cannot be
  generated by Terraform (unlike the unsubscribe secret) and is deliberately
  not a committed slot: an absent parameter means an absent env var means
  the honest failure above, with no placeholder value to mistake for a
  working one. The next api deploy after the parameter exists picks it up.
- **The SDK lives in the worker image only** (`requirements-worker.txt`,
  Dockerfile `worker` stage) — the API image never grows it, per ADR 0015.
  The API Lambda's environment does carry the variable (one SSM namespace,
  one derivation, same as every sibling) but no API code path reads it.
- 8.7 starts from: same worker Lambda, same adapter, same key, same scrub,
  same no-training standing — and its candidates flow through
  confirm-before-entry rather than this ADR's "advisory only" rule.

## What would reopen this

A workload that cannot fit one worker attempt (reopens ADR 0018's
primitive, which drags the call's location with it); an organisational move
to Bedrock; ZDR terms that exclude the chosen model tier; or extraction
needing a capability the direct API cannot offer.

---

> **Amended 2026-09-04 (extraction, 8.7/8.8).** Extraction lands on every
> decision above — same worker Lambda, same direct API, same model id (one
> owner, `adapters/anthropic/review_model.py`), same key, same no-training
> standing — with one input-posture divergence this amendment records rather
> than letting it happen quietly: **extraction's model input is the uploaded
> document itself** (PDF/image, base64), and a credit report carries the
> debtor's tax identifiers on its face. No scrub can remove them from bytes
> that must be sent whole — that is what reading the document means. What
> holds instead: the layer that made "no tax identifiers" structural for the
> review still governs everything extraction *constructs* — the prompts
> carry no case data, and every extracted payload passes through the same
> `scrub` (plus a structural last-four coercion for account numbers) before
> storage, so a tax id never lands in a candidate row. The document bytes
> themselves are covered by the no-training standing and by the ZDR
> follow-up above, whose launch-checklist condition now reads on extraction
> with extra force: it is the surface where full identifiers actually flow.
> One more deliberate divergence: a `max_tokens` stop is **deterministic**
> for extraction (the same document overflows the same ceiling every time)
> and fails the job honestly instead of retrying — the review's transient
> treatment stays as it was.
