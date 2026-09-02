# ADR 0018 — v1 pipelines are an SQS queue and a worker Lambda, not Step Functions

- **Status:** Accepted
- **Date:** 2026-09-02
- **Relates to:** resolves the orchestration-primitive choice
  [ADR 0015](0015-async-pipelines-beside-the-lambdalith.md) deferred to issue
  [#271](https://github.com/insolvia-ai/insolvia/issues/271) (9.10). Binds 9.6
  (packet assembly), 9.7 (AI review) and 8.7–8.9 (extraction), which are
  specified as workers in this pipeline. The client-visible contract rides on
  [ADR 0001](0001-client-stays-dumb-trust-boundary.md): status is read through
  the API, never from AWS.

## Decision

**One SQS queue per environment, one image-packaged worker Lambda consuming
it, and the job record in the case table as the single source of truth the API
reads status from.** Step Functions is not adopted for v1.

The shape, concretely:

- **The API accepts a job and is the only status authority.** A job is a row
  in its case's partition (`PK=CASE#<id>`, `SK=JOB#<job id>`), written
  *before* the enqueue, so a message can never reference a record that does
  not exist. The client polls the API for `queued → running →
  succeeded | failed`; it never sees the queue, per ADR 0001.
- **The message carries identifiers only** — a versioned envelope of job id,
  case id and kind. Workers re-read everything else from the store, which is
  what makes a retry safe and keeps GLBA-scope case data out of SQS (the
  queue is SSE-SQS encrypted, not under the per-environment case key; a
  message body that never contains case data is what makes that acceptable).
- **Retry is SQS's, terminal failure is ours.** At-least-once delivery plus a
  visibility timeout is the retry loop; `maxReceiveCount` exhausts into a DLQ
  whose depth alarms. The worker records every attempt on the job row with
  conditional writes, so a redelivered message for a finished job is a no-op
  and the preparer sees a failure *category and safe message*, not a stack
  trace.
- **One worker Lambda dispatching by kind**, not one function per job kind.
  9.6 and 9.7 land as entries in a kind→callable registry, in the worker
  image, where their heavy dependencies (PDF assembly, model SDKs) belong per
  ADR 0015 — the API image never grows them.

## Context — the weighing, honestly

Step Functions is the AWS-native answer and it is genuinely better at three
things: retry/timeout/catch semantics are declarative instead of hand-rolled,
the execution history is a visual debugging record, and a real multi-step
workflow (fan-out, joins, per-step retry policies) is its home ground. Cost
does not separate the options — at this product's volume both round to zero.

It loses on the criterion ADR 0015 made decisive, and on two honest
observations about what we would actually use:

- **The local story** (the repo rule: everything testable locally, or written
  down). With a queue, the orchestration semantics we rely on are *code we
  run*: the worker is a plain Python callable, the dispatch loop is a pure
  function exercised by pytest, and the one hop we cannot run — SQS delivering
  into Lambda — is a thin, contract-pinned seam. With Step Functions, the
  semantics we would rely on — the ASL retry/catch/timeout JSON — execute
  *only in the cloud*: Step Functions Local is a lagging emulator with
  documented fidelity gaps (mocked service integrations, divergent error
  handling, no recent feature parity), so the exact part Step Functions is
  adopted *for* is the part a laptop cannot verify. That inverts the rule.
- **The state machine would orchestrate nothing yet.** 9.6 and 9.7 are each
  one worker reading the store, working for minutes, and writing a result.
  A one-state state machine buys the ASL layer, an extra IaC surface, and an
  extra IAM principal to fence, and expresses nothing a queue does not.
- **The API must own status anyway.** The preparer's client reads job status
  through the API (ADR 0001), so a job record with transitions has to exist
  regardless of primitive. Once it does, "what Step Functions tracks" and
  "what we store" are two copies of one truth — the failure shape ADR 0010
  exists to bury.

What the queue makes us hand-roll, and why that is acceptable:

- **Idempotency** — needed under either primitive (at-least-once delivery is
  a property of the message, and duplicate *accepts* are a property of
  clients). One active job per (case, kind); conditional status transitions.
- **The per-job timeout** — the worker Lambda's own timeout (15 min ceiling)
  bounds every attempt. 9.6 and 9.7 are minutes-long; the ceiling holds.
- **Failure surfacing** — the worker writes the failure onto the job row;
  Step Functions would have needed the same write to reach the preparer.

## Consequences

- **The local story, explicitly** (ADR 0015 requires this stated):
  - *Workers* are plain Python callables; unit tests call them and the
    dispatch loop directly, no emulation, no AWS.
  - *The seam* is the message envelope, owned by one core module that both
    the enqueue adapter and the worker entrypoint import, and pinned by a
    contract test on the exact wire shape — the producer and consumer cannot
    drift apart without a red test.
  - *The queue itself runs locally for real*: `infra/envs/dev` carries a
    per-machine queue + DLQ from the same Terraform module, the local API
    enqueues to it, and a poller entrypoint long-polls it and drives the same
    dispatch function the Lambda entrypoint does. Send, delivery, redelivery
    and DLQ behaviour are all exercisable from a laptop.
  - *What genuinely cannot run locally*: the Lambda **event source mapping**
    — the managed SQS→Lambda delivery loop (its batching, its scaling, its
    partial-failure handling). Nearest approximation: the poller above, which
    is the same consume-dispatch-delete contract at batch size 1; the mapping
    itself is configuration small enough to be reviewed rather than executed.
- 9.6, 9.7 and 8.7–8.9 add a worker as: a kind, a callable in the registry,
  its tests, and (when their dependencies arrive) weight in the worker image.
  They must not add endpoints that do the work inline, and must not re-open
  this choice without hitting a trigger below.
- A **chain** of steps is expressed, for now, as a worker enqueueing a
  follow-on job — each hop recorded on its own job row. This is deliberate
  scope, not a claim it scales to DAGs.

## What would reopen this

A job that cannot fit Lambda's 15-minute ceiling; a real DAG — fan-out/join,
per-step retry policies, human-approval states — that queue-chaining renders
unreadable; or retry/backoff logic in the worker growing into a hand-written
state machine. Any of those is the concrete case for Step Functions that this
ADR found absent, and the migration path is contained: the job record and the
API contract stay, only the seam behind the accept endpoint changes.
