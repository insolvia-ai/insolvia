# Effective dating: forms and regulatory data

How every versioned regulatory artifact in this product — official form
revisions, government datasets, statutory dollar amounts — is identified,
released, resolved against a date, and pinned to a case. One model, three
consumers, so that the forms engine
([9.3](https://github.com/insolvia-ai/insolvia/issues/93)), the exemptions
dataset ([9.5](https://github.com/insolvia-ai/insolvia/issues/95)) and the UST
pipeline ([10.1](https://github.com/insolvia-ai/insolvia/issues/99)) build on
the same mechanics instead of inventing three.

The [regulatory source register](../business/regulatory-source-register.html)
owns *which* sources exist, their authority, and their refresh calendars; this
document owns *how* a release of any of them is modelled. The register's demand
is the design constraint: scheduled refreshes with an effective-date field,
each update a deliberate release — because a filing computed on stale data is
the failure mode that hurts clients.

## Series and releases

Everything effective-dated belongs to a **series**: one named, append-only
sequence of releases. A series is the stable identity ("the B101 form", "Census
median family income"); a **release** is one immutable revision of it with the
date it takes legal effect.

```
series_id:  <domain>/<name>          lowercase kebab, e.g. form/b101,
                                     ust/census-median-family-income,
                                     code/dollar-amounts, exemptions/federal

release {
  series_id
  effective_date   YYYY-MM-DD — when the authority says it applies
  sequence         1 unless a correction; see below
  source: { url, published, sha256 } // the upstream artifact this was taken from
  notes                              // what changed; anything odd in the source
  ...payload                         // owned by the consumer: template + field
}                                    // spec, dataset rows, named dollar figures
```

The canonical release id is `<series_id>@<effective_date>`, with `+<sequence>`
appended when the sequence is above 1 — `code/dollar-amounts@2025-04-01`,
`ust/census-median-family-income@2026-04-01+2`. Release ids are deliberately
**not** UUIDs: the case-data-model's opaque-identifier rule exists to keep PII
out of keys, and a release id is public configuration whose whole job is to be
legible in an audit trail.

Three dates, kept distinct on purpose:

| Date | Meaning | Who sets it |
|---|---|---|
| `effective_date` | When the law or data applies — a calendar fact, no time, no zone | The authority |
| `source.published` | When the authority published the artifact | The authority |
| ingested | When we took it — the release's git history | The merge |

`effective_date` may be in the future, and routinely is: the whole point of a
scheduled refresh is ingesting the December 1 forms or the April 1 §522
amounts *before* they take effect, so the switchover on the day itself is a
non-event that no one deploys for.

## Resolution

```
resolve(series, as_of)  → the release with the greatest effective_date ≤ as_of;
                          ties broken by highest sequence
get(series, release_id) → that exact release; must succeed for any id ever pinned
latest(series)          → newest by (effective_date, sequence), even if future
```

Two hard edges:

- **No fallback past the beginning.** If `as_of` predates the series' earliest
  release, resolution fails and the computation refuses to run. Wrong data is
  worse than no answer.
- **Corrections are new releases, not edits.** When the authority reissues an
  artifact (the UST fixing a spreadsheet) or we ingested one wrongly, the fix
  is a new release with the same `effective_date` and the next `sequence`. The
  tie-break makes it win future resolutions; cases pinned to the flawed release
  still resolve it by id, which is what makes "what data did this filing use"
  answerable forever.

## Float, then pin

The `as_of` date is the case's **filing date** — the data a case uses is the
data effective on the day it files, not the newest. But most of a case's life
is before filing, so resolution has two phases:

- **Floating (intake, drafting):** `as_of` is today's date. A case being
  prepared always sees the currently-effective release.
- **Pinned (assembly onward):** packet assembly resolves once and records the
  release ids it used on the case — that is what
  [`case-data-model.md`](case-data-model.md)'s `form_revisions` map and
  `constants_set_id` are. `form_revisions` keys form series to the pinned
  release's `effective_date[+sequence]`; `constants_set_id` is the pinned
  release id of `code/dollar-amounts`. Computations with their own dataset
  inputs (the means test) record their pins the same way; where, is the
  means-test milestone's call.

Re-assembly before filing re-resolves and re-pins — a packet assembled in
November and filed after December 1 must be re-checked against the new forms,
and whether the preparer is shown the delta is the forms engine's UX call. A
**filed** case never re-resolves: every read goes through `get` on the pinned
ids, so the packet a court holds stays reproducible against exactly the data
that produced it.

## The registry is the repository

Releases are committed files in this repo, reviewed as pull requests and
shipped inside the deploy artifact of the service that reads them — there is
no dataset table, no ingestion endpoint, no admin upload UI.
[ADR 0014](../adr/0014-the-repository-is-the-regulatory-release-registry.md)
owns that decision and its trade-offs; the mechanics are:

```
<registry root>/<series_id>/<effective_date>[+<sequence>]/
  manifest.json        # series_id, effective_date, sequence, source, notes
  ...payload files     # shape owned by the consumer
```

- A registry root lives beside the service that reads it (today that means
  `services/api`; the exact path is the first consumer's call) and ships in its
  image, so local, staging and prod read identical data by construction — no
  per-environment seeding, no drift.
- **Append-only is an invariant, not a habit:** a merged release directory is
  never edited or deleted, because pinned cases resolve ids by `get` forever.
- The first consumer that lands a registry owes a **loader that validates it
  in CI** — every directory has a well-formed manifest, ids match paths,
  payloads parse — so a malformed release fails a pull request, not a filing.

## The refresh process

One pattern, instantiated per source by the issue that owns it (the December 1
forms cycle runbook is
[9.8](https://github.com/insolvia-ai/insolvia/issues/98); the UST/Census
pipeline is [10.1](https://github.com/insolvia-ai/insolvia/issues/99)):

1. **The check is automated.** A scheduled CI job fetches the canonical
   source and compares it (checksum, latest stated effective date) against the
   newest committed release. A difference alerts a human; silence means
   current.
2. **Ingestion is a reviewed PR.** A human (or an agent, reviewed) commits the
   new release directory with its manifest. The diff *is* the review surface —
   what changed in the data is visible line by line.
3. **Merge is the release; deploy ships it.** No further ceremony: the
   existing CI pipeline is the release process, and git history is the audit
   trail of who ingested what, when, from where.
4. **Staleness is an incident.** A release the authority has made effective
   that we have not ingested means the product is computing on stale data —
   the alert in step 1 is the tripwire, and it must reach a human, not a log.

## What this document does not own

- **Payload shapes.** What a form template, an exemption scheme, or an IRS
  Standards table looks like inside a release belongs to 9.3, 9.5 and 10.1.
- **Dataset contents and current figures.** The
  [register](../business/regulatory-source-register.html) owns sources and
  calendars; the releases themselves own the numbers.
- **Means-test arithmetic** and which series it consumes — the means-test
  milestone.
- **Case pinning fields' storage** — [`case-data-model.md`](case-data-model.md)
  owns the case schema; this document only defines what the pin values are.

## Related

- [ADR 0014](../adr/0014-the-repository-is-the-regulatory-release-registry.md)
  — why the repo is the registry
- [`case-data-model.md`](case-data-model.md) — `form_revisions`,
  `constants_set_id`, and why forms are projections of facts
- [`docs/business/regulatory-source-register.html`](../business/regulatory-source-register.html)
  — the sources, their authority, their cadence
