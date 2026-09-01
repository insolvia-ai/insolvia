# ADR 0015 — Async pipelines beside the lambdalith; SAM rejected

- **Status:** Accepted
- **Date:** 2026-09-01
- **Relates to:** upholds D6 (Flask + Mangum on Lambda) and
  [ADR 0001](0001-client-stays-dumb-trust-boundary.md)'s single trust
  boundary; shapes 9.6 (packet assembly), 9.7 (AI review) and 8.7–8.9
  (extraction). The orchestration-primitive choice and walking skeleton are
  issue [#271](https://github.com/insolvia-ai/insolvia/issues/271) (9.10).
  Numbered after the pending ADR 0014
  ([#270](https://github.com/insolvia-ai/insolvia/pull/270)).

## Decision

**The Flask lambdalith stays for the synchronous API. Work that cannot finish
inside a request — assembling a Chapter 7 packet, an AI agent reviewing a
petition, document extraction — runs in asynchronous pipelines beside it:**
the API accepts a job and reports its status; separate worker Lambdas, each
its own container image, do the long work. The orchestration primitive
(Step Functions vs. SQS + workers) is chosen in
[#271](https://github.com/insolvia-ai/insolvia/issues/271) with the local
story as a first-class criterion.

Two rules ride along:

- **Every Lambda is image-packaged** (`package_type = "Image"`). This is
  already true of all five functions in the fleet; it is now a rule for new
  ones. Images carry a 10 GB limit instead of zip's 250 MB unzipped — and the
  heavy dependencies ahead (PDF assembly, model SDKs) belong in *worker*
  images, not the API's.
- **No SAM.** Terraform remains the only IaC. If high-fidelity local
  emulation of API Gateway + Lambda is ever wanted, `sam local` supports
  Terraform projects directly (`--hook-name terraform`) — the emulator
  without the deployment system.

## Context

The concern that triggered this: Flask-on-Lambda would "eventually exhaust"
the function-size limit, and doesn't use Step Functions or shared layers the
way Lambda is "supposed" to work. Examined:

- **The size ceiling is not near.** The services deploy as container images,
  so the limit is 10 GB, not 250 MB. Shared layers would not help anyway:
  layers count *inside* a zip function's 250 MB and don't apply to
  image-packaged functions at all — they are a code-sharing convenience, not
  headroom, and `insolvia_core` plus Docker layers already do that job here.
- **The real pressure is the request path's shape, not its size.** The API
  sits behind a 30-second gateway timeout. The forms milestone adds multi-step,
  retryable, minutes-long jobs; those are what Step Functions-style
  orchestration is actually for. The lambdalith is the wrong home for them —
  and the right home for everything else it does today: one cold-start pool,
  one deploy, and a local story that is just Flask.
- **SAM was considered and rejected as a deployment system.** SAM is
  CloudFormation. Adopting it means either two IaC systems each owning part of
  the truth — the "two simultaneous truths" failure mode
  [ADR 0010](0010-design-system-moves-to-its-own-repository.md) exists to
  bury — or migrating a working Terraform estate (modules, the OIDC deploy
  role's IAM, promote-not-rebuild) for tooling that duplicates CI. Its
  emulator is also slower feedback than running Flask directly, which is the
  current, better local loop.

## Consequences

- 9.6, 9.7 and 8.7–8.9 are specified as **workers in the pipeline**, not
  endpoints — they must not re-open this architecture. The API's job records,
  status reads, and idempotency rules come from #271's job model.
- The local rule extends to pipelines: workers are plain Python callables
  testable without emulation; the orchestration seam is contract-pinned; what
  genuinely cannot run locally is written down per the repo rule, with the
  nearest approximation named. If #271 picks Step Functions, this criterion
  is the one it must satisfy explicitly (Step Functions Local has known
  fidelity gaps).
- A new Lambda proposed as a zip package, or infra proposed in SAM/
  CloudFormation, is a violation of this ADR, not a style choice.

## What would reopen this

The API image approaching its ceiling (an alarm on image size, not a guess);
or a pipeline need that neither Step Functions nor queue-plus-workers under
Terraform can express, which would reopen the tooling question with a concrete
case rather than a hypothetical.
