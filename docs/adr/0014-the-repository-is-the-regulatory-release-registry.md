# ADR 0014 — The repository is the regulatory release registry

- **Status:** Accepted
- **Date:** 2026-09-01
- **Relates to:** implements the release model in
  [`effective-dating.md`](../reference/effective-dating.md); satisfies the
  `constants_set_id` commitment in
  [`case-data-model.md`](../reference/case-data-model.md); leaves
  [ADR 0001](0001-client-stays-dumb-trust-boundary.md)'s trust boundary
  untouched — clients still see this data only through the API.

## Decision

**Effective-dated regulatory artifacts — official form templates, government
datasets, statutory dollar amounts — are committed files in this repository,
ingested by pull request and shipped inside the deploy artifact of the service
that reads them.** There is no dataset table, no ingestion endpoint, and no
admin upload surface. Merging the PR *is* the release; the ordinary CI deploy
ships it; git history is the audit trail.

The shape of a release (series, effective date, sequence, manifest, the
resolution and pinning rules) is the reference document's; this ADR owns only
where releases live and why.

## Context

The [regulatory source register](../business/regulatory-source-register.html)
demands that each dataset refresh be "a deliberate, auditable release with its
effective date, not a silent overwrite." That demand has two natural
implementations:

1. **A store**: a datasets table (or bucket) plus ingestion endpoints, an
   approval workflow, an audit log of who loaded what, and per-environment
   loading.
2. **The repo**: each release is a directory of files, ingested by PR,
   reviewed as a diff, deployed by the pipeline that already deploys
   everything else.

Option 1 rebuilds, piece by piece, what git and CI already provide: immutable
history, attributed changes, a review gate, a promotion pipeline, and rollback.
It also breaks two standing rules of this repo unless more is built: every
environment would need its own seeding (violating local/staging/prod parity —
a developer machine would compute the means test against whatever was last
loaded into their dev tables), and a solo maintainer would be operating an
approval workflow with one participant.

The data itself is suited to files: it is public (this repo is public, and
nothing here is a secret — these are published government figures and forms),
small (dollar-amount tables, median-income tables by state and household size,
IRS Standards tables, one PDF template per form revision), and slow-moving
(the fastest cadence in the register is Census updates at 2–4 times a year).
Reviewing a release as a line-by-line diff of the numbers is exactly the
review the register asks for.

## Consequences

- **Environment parity is automatic.** Local, staging and prod read the same
  committed releases because they run the same artifact. Nothing to seed,
  nothing to drift, and every release is testable locally by construction.
- **A data correction requires a merge and a deploy.** Accepted: deploys are
  cheap CI runs, and a correction to legally-load-bearing figures *should*
  pass review and leave history. There is no "quick fix in prod" path, which
  is a feature.
- **Append-only becomes a repo invariant.** Cases pin release ids and must
  resolve them forever, so a merged release directory is never edited or
  deleted — corrections are new releases with a bumped sequence. The loader
  that validates the registry in CI is the natural place to also refuse a
  diff that mutates an existing release.
- **The refresh pipeline is CI, not a service.** Scheduled jobs check
  upstream sources and alert on staleness; ingestion lands as a PR. No new
  runtime component exists to operate or secure.
- **Revisit trigger: a dataset that outgrows the diff.** The 50-state
  exemption corpus at full breadth, or any source whose releases stop being
  reviewable as diffs or stop fitting comfortably in the repo and the Lambda
  image, is the signal to move *that series* to object storage with the same
  release model — the ids, manifests and resolution rules are
  storage-neutral on purpose. Until a series hits that trigger, it stays in
  the repo.
