# ADR 0022 — A client is not a case

- **Status:** Proposed — accepted by merging; the build is
  [#353](https://github.com/insolvia-ai/insolvia/issues/353) (model, store,
  API), [#354](https://github.com/insolvia-ai/insolvia/issues/354) (screens)
  and [#355](https://github.com/insolvia-ai/insolvia/issues/355) (lifecycle).
- **Date:** 2026-09-23
- **Relates to:** [ADR 0009](0009-a-case-belongs-to-a-firm.md) owns the
  permission axes this extends; [ADR 0012](0012-shared-python-domain-package.md)
  is why the entity lands in `insolvia_core`;
  [ADR 0013](0013-mcp-server-replaces-direct-pms-integration.md)'s
  confirm-before-entry invariant governs the agent side;
  [`case-data-model.md`](../reference/case-data-model.md) owns the debtor this
  splits and gains a pointer here; the tax id's bytes are
  [#382](https://github.com/insolvia-ai/insolvia/issues/382)'s and are only
  referenced.

## Decision

**A `client` is a firm-scoped person; a `debtor` is that person's identity as
copied into one case.** One client has many cases over time; a joint case is
two clients on one matter; a prospect is a client with no case yet.

```
client {                            // firm table, PK FIRM#<firm_id>, SK CLIENT#<id>
  id, firm_id
  name, other_names_used            // fields.PersonName; debtors.OtherName
  date_of_birth                     // form date — never on the debtor; no form prints it
  residence_address, mailing_address, phone, mobile, email
  tax_id_ref, tax_id_last_four      // a POINTER to #382's encrypted item; never the value
  lead_source, referred_by          // free text in v1; a firm pick-list is 14.8's
  first_retained_at                 // form date, set by hand or by #355's retained transition
  status: active | archived
  created_at, updated_at, created_by
}
```

**Copy, not reference.** A case's debtors carry a server-owned `client_id` and
a copy of the client's identity fields, each with a provenance entry
`{ source: "client", client_id }` — a fifth source beside `library`, which is
the same shape for the same reason (`insolvia_core.provenance.SOURCES`,
`library_creditor_id`). A filed petition never changes because a client record
did. Divergence is a computed, visible state: `debtor_json` gains
`differs_from_client: [<field_path>…]`, and the debtor screen offers two
explicit acts — *re-copy from client* and *update client from this case* —
each an ordinary whole-record write with `client` provenance. Re-copy is
refused on a `filed` case; amending a filed petition is 17.4's.

**The store is the firm table**, beside the library creditors:
`PK FIRM#<firm_id>`, `SK CLIENT#<id>`
(`insolvia_core.library_creditors.SK_PREFIX` is the precedent), no GSI. The
reverse edge — a client's cases — is a sparse `by-client` index on the
**case** table, fed by the debtor item (`GSI3PK CLIENT#<client_id>`,
`GSI3SK <caseCreatedAt>#<caseId>`), exactly as assignment rows feed
`by-assignee` (`insolvia_core.cases.assignment_item`). A debtor with no client
is simply not in it; a joint case appears once per client — #354's row model.

**Permissions: a `clients` feature** appended to `insolvia_core.firms.FEATURES`.
Reaching a client is *same firm AND `clients ≥ view_only`*; reaching a case
stays `access.may_see_case`, unchanged. A client's case list is filtered per
case and shows **no count of the rest** — ADR 0009's 404 covers "an in-firm
case you are not linked to", and a count would be the enumeration it hides.

**Migration: every existing debtor becomes a client, once.** A `backfill`
command beside `seed` in `services/admin/…/entrypoints/`, deriving the client
id as `uuid5(target table, case id, filing role)` so re-runs converge, writing
the client with `attribute_not_exists(SK)` and stamping the debtor's
`client_id` with `attribute_not_exists(clientId)`. Dev runs it from
`scripts/dev-aws-seed.sh`; staging from a step beside
`.github/actions/seed-staging`; prod from the release run behind the same
`promote` gate the deploy sits behind — and unlike the seeder it does **not**
refuse a prod table, because prod is where a migration is for.

## Context

The person is modelled once, inside a case: `insolvia_core.debtors.Debtor` is
`PK CASE#<id>, SK DEBTOR#<role>`, keyed by filing role, written whole by
`PUT /v1/cases/<id>/debtors/<role>` from the questionnaire's autosave
(`services/api/…/routes/debtors.py`; `apps/insolvia_app/src/screens/intake/`).
`POST /v1/cases` takes a chapter and a district and nothing about a person
(`routes/cases.py`; `screens/cases/index.tsx`). So a refiled Chapter 7 is a
stranger to the dismissed one; a converted 13 retypes its debtor; a
consultation that has not become a case has nowhere to exist; lead source,
referral and the retained date — the facts a firm reports on — have no home.
[#354](https://github.com/insolvia-ai/insolvia/issues/354) wants a client
list as the front door and
[#355](https://github.com/insolvia-ai/insolvia/issues/355) a prospect funnel,
and both need the person to outlive the matter.

Three properties of the current model shape the decision, and each is code
rather than preference:

- **Whole-record writes with per-field provenance.** Invariant 1 (every
  populated field carries an entry) is checked by `require_provenance` against
  a complete record. A debtor that *referenced* its identity would have fields
  it does not hold — nothing to check, nothing for the forms engine, the MCP
  record tools (`services/mcp/…/core/tools.py`, `_find_record`) or the packet
  to read without a join every reader must remember.
- **The library precedent.** `library_creditors.py` already answers "firm-owned
  record reused across cases": a copy with a provenance pointer, "never a live
  reference: editing the library entry later must never rewrite a filed
  schedule". The client is the same problem with a person in it.
- **The tax id is being solved once.** `parse_debtor` refuses `tax_id` today;
  #382 gives it envelope encryption, a last-four view and a logged full read.
  A second encrypted copy on the client would double the grant, the audit
  action and the KMS posture for one number.

## Weighed and rejected

**Live reference, materialised on read.** Cleanest for "one person, one
truth"; rejected because a petition is a signed statement about a date, the
store's own invariant is that a confirmed value travels with its record, and
"which client version did B101 print" becomes unanswerable. The cost of copy —
divergence — is made visible instead of prevented.

**Snapshot-with-version** (the debtor records `client_version` and the API
serves the diff from a client history). Rejected for v1: it needs a versioned
client store nothing else wants, and a field-path diff computed on read gives
the screen the same "differs" state from the two records it already holds.

**Clients in the case table** (`PK CLIENT#<id>`). It would put a person under
the case key's stricter deny and beside the access log; rejected because the
firm table already reuses the case key (`infra/modules/firm_store/main.tf`
takes `module.case_store.kms_key_arn` on purpose), is what the MCP and admin
roles already hold read grants on, and is where "one firm's reusable records"
already live. A `by-client` GSI on the case table is added online
([`terraform.md`](../reference/terraform.md)); a cross-table transaction is not
needed because the debtor item is both the copy and the index entry.

**A link item per (case, client)** beside the debtor, like `case_assignment`.
Rejected: a second row that can disagree with the debtor's `client_id`, for no
read the debtor item cannot serve.

**Folding `clients` into `cases` or `intake`.** Rejected for
`CREDITOR_LIBRARY`'s reason in `firms.py`: a firm may want a paralegal keeping
the client directory without every matter, or the reverse. `hidden` for every
existing row by default, per ADR 0009 — see Consequences.

**Migrating lazily on read.** Rejected: a `GET` under a `view_only` caller
would write firm-table rows, and the API's firm grant does not include that
for a reason.

## Consequences

- **`POST /v1/cases` takes `client_ids` (one or two) and creates the debtors
  in the same transaction** as the case and the creator's assignment —
  `CaseStore.create` grows from a pair to the case, its assignment and its
  debtor copies, for the reason `create_case` gives for the pair: a partial
  write is an invisible matter. `debtor_1` must name a client; `debtor_2` must
  when present; `non_filing_spouse` may. The questionnaire's role picker stops
  minting `debtor_2` from nothing and instead links a client. `client_id`
  joins `_SERVER_OWNED`: the questionnaire's PUT keeps the stored value, and
  re-linking is its own route.
- **`/v1/firm/clients`** mirrors `/v1/firm/creditors`: no firm id in the URL,
  whole-record POST/PUT, snake_case wire, `PersonName`/`Address` reused
  unmodified, `name.surname` or `name.given` the one required field. Archive
  is a status write, not a delete; a client with cases cannot be deleted at all.
- **The tax id is one item, addressed by `tax_id_ref`.** #382 owns the bytes,
  the KMS grant, the last-four view and the `taxid.*` access-log action. This
  ADR asks one thing of it: address the encrypted item by an opaque id rather
  than `(case_id, filing_role)`, so a refiled case reuses it. If #382 ships
  keyed by debtor first, the backfill gives each item a ref and repoints. The
  full-value read stays a case operation (B121, e-filing); a client screen
  shows last four only. #382's "the MCP read tool stays last-four only" holds
  for client records too.
- **Access log.** `AccessEvent.case_id` generalises to a subject key —
  `CASE#<id>` today, `CLIENT#<id>` for `client.create` / `client.read` /
  `client.update` — in the same table, under the same PutItem-only grant. The
  list is not logged, matching `GET /v1/cases`; a single-record read is, because
  a client record is PII with no case to be logged under.
- **MCP.** `client` joins `ENTITY_TYPES` for `list_case_records` /
  `get_case_record` within a reachable case (gated `clients: view_only` — the
  first entity type not under `intake`), and `whoami` reports the feature. A
  firm-wide client list is not a tool in v1, and a `client` proposal lands as
  a candidate like any other write
  ([`mcp-surface.md`](../reference/mcp-surface.md) gains the row).
- **Seeds.** A changed fixture is a new version: `fixtures/v2/cases.json` adds
  a top-level `clients` list keyed by handle, and each debtor names one; the
  loader derives the client id from (target table, firm, version, handle) as
  it derives the case id. Rows seeded from v1 are left alone by the seeder's
  rule and receive their client from the backfill — which is also the local
  test of the backfill.
- **Fail-closed lands as invisibility.** Every existing `firm_user` row lacks
  `clients` and so has it `hidden`; admins see the list, nobody else does until
  granted. `default_permissions` gives it `add_edit` to attorney and paralegal
  and `view_only` to staff for *new* rows. The staging `paralegal` in
  `seeds/staging.json` predates the feature; the e2e admin grants it, or the
  fixture bumps.
- **Two people, one row, one merge.** The backfill cannot know that two
  debtors are one person; it makes two clients. *Merge clients* is a #354
  follow-up, not a migration concern, and the number of duplicates in staging
  is one (one seeded case).
- **[`case-data-model.md`](../reference/case-data-model.md)** stays the owner
  of the debtor and gains `client_id` and the `client` source when the build
  lands; it is not rewritten here.

## Build breakdown

Ordered; each is one PR, one responsibility, all three environments.

| # | PR | Done when | Size |
|---|---|---|---|
| 1 | `core+api: client entity, store and /v1/firm/clients behind a clients feature` | `FEATURES` has `clients`; the five routes pass unit tests over the memory adapter; `client.*` access actions recorded; nothing references a client yet | M |
| 2 | `core+api+api-client: cases are opened for clients — client_id on the debtor, the client source, the by-client index` | `POST /v1/cases` requires `client_ids`, writes debtors with `client` provenance in one transaction; `GET /v1/firm/clients/<id>/cases` lists reachable cases; `differs_from_client` served; Terraform adds `by-client` on dev, staging, prod | L |
| 3 | `admin+seeds+ci: the client backfill and fixture v2` | `backfill` converges on a dev stack twice with the same ids; runs on staging deploy and in the prod release; `fixtures/v2` seeds clients and the e2e suite finds them | M |
| 4 | `app: the client list, the client record, and "add client" as the front door` (#354) | A preparer creates a client, starts two cases, finds either from the list; joint cases one row per client; `/cases` "new case" picks a client | L |
| 5 | `app: the debtor screen reads its client — differs-from-client, re-copy, update client` (#354) | The intake debtor section shows the linked client, the diff, both acts; role picker links rather than mints | M |
| 6 | `mcp: client records and the clients gate` | `client` in `ENTITY_TYPES`; `whoami` reports the feature; harness round-trip in 12.5's checklist | S |
| 7 | `core+api+app: lifecycle as data, post-filing fields, archive, delete, copy` (#355) | Funnel to `filed` with case number, dates, judge, trustee; archived cases leave the default list; `first_retained_at` set by the retained transition; copy-case names its source in provenance | L |

**Risks.** *The joint-case row model*: a `debtor_2` linked to the same client
as `debtor_1` is a valid write today and must be refused (one client, one
role per case), or the `by-client` index shows one case twice. *Existing
seeded cases*: the fixture's `chapter-7-sample` is v1 with no client; until PR
3 runs, PR 2's `POST /v1/cases` works and the seeded case shows a debtor with
no client — the app must render that state rather than assume the link. *#382
ordering*: whichever of #382 and PR 2 merges second repoints the other's key;
the ADR above says how, and the backfill is where it happens.
