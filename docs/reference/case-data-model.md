# Case data model

The server-side shape of a bankruptcy case: what we store, how each value knows
where it came from, and which values we refuse to store because they are
arithmetic. This is the logical model. It is deliberately engine-neutral — the
store that holds it is chosen in the encrypted-case-store work, not here.

Three consumers pull in different directions, and every decision below is a
trade between them:

| Consumer | Wants |
|---|---|
| **The forms** — B101, B106A–J, B107 | Field coverage close enough that the official forms almost fill themselves |
| **AI extraction** — credit reports, pay stubs | Records that can arrive as unconfirmed candidates and be promoted, not overwritten |
| **CM/ECF e-filing**, later | Shapes that survive translation into the bankruptcy IEPD without a rewrite |

## The forms are lists, not documents

Almost every schedule is a repeating entity with a handful of singular fields
wrapped around it: each creditor, each asset, each transfer, each dependent.
Modelling per-form or per-page would produce a schema shaped like a PDF. The
model below is shaped like the underlying facts, and the forms engine projects
those facts onto whichever revision of the form is current.

That projection direction matters, because **the forms move and the facts do
not**. Official forms revise on roughly an annual cycle under FRBP 9009, and
the set is not revised in lockstep — some schedules are a decade older than
others. The
[regulatory source register](../business/regulatory-source-register.html) owns
which revision is current and when it is checked; this document does not
restate it. A case records the revisions it was prepared against — the
float-then-pin rule in [`effective-dating.md`](effective-dating.md) — so that a
case begun before a revision keeps answering the questions it was actually
asked.

## Core entities

Twenty-six case-scoped types. Two more — the tax-identifier access log and
the effective-dated statutory constant sets — are referenced here but live
outside the case store; see below.

| Entity | Cardinality | Feeds |
|---|---|---|
| `case` | root | B101 header, chapter, venue district |
| `petition` | one | B101 Pt.2–6 case-level answers |
| `debtor` | 1–2, plus an optional non-filing spouse | B101 Pt.1, 106I, B121 |
| `prior_case` | many | B101 line 9 |
| `related_case` | many | B101 line 10 |
| `sole_proprietorship` | many | B101 line 12 |
| `filing_professional` | 0–2 | B101 Pt.7, B2030 (the attorney's compensation disclosure) |
| `creditor` | many, deduplicated | Creditor matrix |
| `claim` | many, references a `creditor` | 106D, 106E/F, B108 Pt.1 |
| `asset` | many | 106A/B |
| `exemption` | many, references an `asset` | 106C |
| `contract_lease` | many | 106G, B108 Pt.2 |
| `codebtor` | many | 106H Pt.2 |
| `community_household_member` | many | 106H line 2, B107 Q3 |
| `employment` | many | 106I Pt.1 |
| `pay_period_record` | many, references an `employment` | Means test |
| `other_income_record` | many, references a `debtor` | Means test |
| `means_test_input` | one | B122A-2 (Ch. 7) · B122C-1, B122C-2 (Ch. 13) |
| `plan` | one, Ch. 13 | The plan calculator; Official Form 113 |
| `income_summary` | one per debtor column | 106I Pt.2 |
| `household` | 1–2 (106J-2 adds a second) | 106J Pt.1 |
| `expense` | many, references a `household` | 106J Pt.2 |
| `dependent` | many, references a `household` | 106J Pt.1 |
| `sofa_entry` | many, typed | B107 |
| `document` | many | Source material |
| `extraction_candidate` | many, **outside the case** | The review queue |

Every entity carries `id` and `case_id`, without exception — including the ones
that hang off a debtor or a household. Under a case-partitioned store that is
the partition key, and a nested-only reference would make the record
unaddressable.

## The case and the petition

```
case {
  id, firm_id, created_by
  chapter: 7 | 11 | 12 | 13
  court, division              // a reference into the court registry (below)
  district                     // the printed name, DERIVED from the reference
  status: intake | ready_to_file | filed | discharged | dismissed | closed
                                       // a case STARTS retained; the funnel before it is
                                       // the client's (below)
  filed_at, meeting_341_at            // form dates: the petition (the order for relief in a
                                       // voluntary case) and the FIRST date set for the §341
                                       // meeting — the anchors the deadline engine counts from
                                       // (issue 14.6 / #358)
  case_number, judge, trustee         // the docket's facts, typed from the notice of filing
                                       // until 17.2 reads them from the court (#355)
  office_file_number                  // the firm's own number for the matter
  archived_at, archived_by            // out of the working list, still the firm's record
  deleted_at, deleted_by              // soft delete — see "The lifecycle" below
  exemption_set: state_and_federal_nonbankruptcy | federal   // 106C line 1
  is_amended, ch13_supplement_date                           // the header box on every form
  form_revisions: { <form>: <revision> }
  constants_set_id
}

petition {
  id, case_id
  fee_handling: full | installments | waiver          // → Forms 103A / 103B
  rents_residence, eviction_judgment_against_you      // → Form 101A
  small_business_status                               // incl. the Subchapter V election
  hazardous_property: { description, why_immediate, address }
  debt_character: consumer | business | other(+text)
  ch7_funds_available_for_creditors
  estimated_creditors, estimated_assets, estimated_liabilities   // banded enums, self-selected
  expected_filing_date        // not a B101 line — the petition screen's own
                               // planning figure (issue #342), the base the
                               // §109(h) counseling window and the 8-year
                               // prior-case lookback are computed from
}
```

`petition` is separate from `case` because it is the answers to one form,
churned during intake and untouched afterwards, while `case` holds identity and
lifecycle that everything else references.

**The court is a reference, not a string** (issue #360). `court` is a
district's CM/ECF code and `division` one of its divisions' codes, both
validated on write against the `courts/us-bankruptcy` series in
`insolvia_core.courts` — a release of the regulatory registry
([ADR 0014](../adr/0014-the-repository-is-the-regulatory-release-registry.md))
whose record shape [ADR 0024](../adr/0024-electronic-filing-path.md)
specifies: identity, divisions with their CM/ECF office code and the FIPS
counties they serve, the court's PDF and creditor-matrix rules, the signature
instrument and B121 handling, Case Upload status, and a source with a date on
every fact. `district` survives as the printed name the registry gives the
court ("Middle District of Florida" — the B101 dropdown's own spelling),
written from the reference on every write and never typed. A case written
before the registry existed carries its typed `district` and no reference;
it still reads and prints, and is asked for a court on its next edit.

## The lifecycle

The case's status is its lifecycle as data (issue 14.3 / #355), owned by
`insolvia_core.cases` (`STATUSES`, `TRANSITIONS`, `apply_changes`):

```
            client (prospect, prospect_stage) ──► case opened = RETAINED
                                                     │
intake ◄──► ready_to_file ──► filed ──► discharged ──► closed
                                │  ▲ └──► dismissed ───► closed
                                │  └───── closed (reopened, § 350(b))
                                └───────► closed
```

- **The funnel is the client's, and a case starts retained** — the
  maintainer's decision of 2026-10-01, recorded in
  [ADR 0022](../adr/0022-a-client-is-not-a-case.md): "a prospect is a
  client with no case yet". A firm client is a prospect while
  `first_retained_at` is unset, and `prospect_stage` (`possible`,
  `consultation_scheduled`, `awaiting_signed_agreement`, `exhausted`) is
  their funnel position, set through `PUT /v1/firm/clients/<id>/prospect-stage`
  and never by the whole-record PUT. Opening a case for the client — or
  copying one — is the **retained transition**: one conditional write
  (`FirmStore.mark_client_retained`) stamps `first_retained_at` when unset
  and REMOVES the stage, which is cleared rather than kept as history (the
  case's own status history and `lead_source` are the records). A stage
  write is conditioned on `first_retained_at` still being absent, so the two
  cannot interleave into a retained client with a funnel position; a stage
  for a retained client is a 409. "Not retained" is read from the client
  record, never from the `by-client` index, which would answer for cases
  the caller may not see. A non-filing spouse is never stamped.
- **Moves are forward only**, with two exceptions: `intake` ↔
  `ready_to_file`, and `closed` → `filed` (a reopened case). A filed
  petition does not go back to preparation — changing it is an amendment.
  A move off the map is a 409.
- **"Filed" means on the docket**: `filed`, `discharged`, `dismissed` and
  `closed` are all filed petitions (`cases.is_filed`), and every rule that
  protects a filed petition — no re-copy from the client, no plain packet
  re-assembly, `amended` only once filed — reads that, never the literal.
  Reaching it needs `filed_at` and `case_number`; once there, neither may be
  cleared.
- **Every move is recorded**: a `STATUS#<changedAt>` row in the case's
  partition (who, when, from and to status and stage), written in the same
  transaction as the case — and the case write is conditioned on the status
  it was read at, so a stale whole-record save cannot put an old status back
  (`GET /v1/cases/<id>/status-history`).
- **Archive** is an attribute, not a status: an archived case keeps its
  lifecycle, leaves the working list (`GET /v1/cases`) and lists under
  `?archived=true`. Any status may be archived; archived cases stay editable.
- **Delete is soft, and the retention posture is this:** a firm admin may
  delete a case that never reached the court (a filed one is refused —
  archive it). Deleting stamps `deleted_at`/`deleted_by` and nothing else:
  `access.may_see_case` refuses a deleted case for everyone, so it and
  everything reached through it — debtors, schedules, documents, tasks,
  events, the portal — answer 404, while every row and every document's bytes
  stay where they are, under the same case-key encryption. **Documents follow
  the case** by construction, because a document is only ever reached through
  its case. Nothing is purged; there is no restore in the product. A purge
  job, if ever wanted, is its own decision about retention periods (the
  regulatory register's, like the access log's), not a side effect of this
  one.
- **Copy case** (`POST /v1/cases/<id>/copy`) opens a new retained case from
  an existing one's preparation data — the chapter, court and exemption
  election, every debtor with its client link and tax-id pointer, and every
  generic collection — keeping record ids, so cross-references survive.
  Not copied: status, history, docket facts, pins, documents, candidates,
  notes, tasks, events, portal bindings. Each copied value keeps its own
  provenance entry and gains `copied_from_case_id` (below). The module
  docstring of `insolvia_core.case_copy` owns the list.

## Identity, and why joint debtors are two records

**A joint filing is two debtor records under one case, not one record with
spouse-suffixed columns.** The IEPD models it this way — `BankruptcyDebtor2` is
declared as a substitution for `BankruptcyDebtor1`, each carrying its own name,
own tax identification, own signature — and the forms follow. B101 prints a
full second column for credit counseling *and for venue*; 106I's second column
may belong to a spouse who is not filing at all.

A debtor is one case's **copy** of a firm-scoped `client` — the person, who
outlives the matter. [ADR 0022](../adr/0022-a-client-is-not-a-case.md) owns
that split (the `client_id` on the debtor, the `client` provenance source, and
what "differs from client" means); this section gains those fields when its
build lands.

```
debtor {
  id, case_id
  filing_role: debtor_1 | debtor_2 | non_filing_spouse
  name: { given, middle, surname, suffix }
  other_names_used: [ { id, given, middle, surname, business_name } ]  // 8-year lookback
  tax_id: { kind: ssn | itin, last_four, ref }                         // the number is a sealed item; see below
  employer_ids: [ ein ]
  residence_address, mailing_address
  phone, mobile, email
  venue: { basis: lived_longest_180_days | other, explanation }        // per debtor, B101 line 6
  credit_counseling: {
    status,                                                            // four-way, B101 line 15
    exemption_reason: incapacity | disability | active_duty
  }
  signed_at
  client_id                                                            // the firm client this debtor was copied from — server-owned
}
```

**Every Debtor 1 and Debtor 2 is a firm client, copied**
([ADR 0022](../adr/0022-a-client-is-not-a-case.md)). `POST /v1/cases` takes
`client_ids` — one, or two filing jointly — and writes the case, its
creator's assignment and a debtor per client in one transaction; each copied
field (name, other names, both addresses with the residence county, phone,
mobile, email) carries `client` provenance. `client_id` is server-owned: the
questionnaire's whole-record PUT keeps it, and
`PUT /v1/cases/<id>/debtors/<role>/client` is the only thing that sets or
moves it — copying the client in when the role is empty, moving only the
link when it is not. A questionnaire save cannot create Debtor 1 or Debtor 2
from nothing; a non-filing spouse, who need not be the firm's client, still
can. One client holds at most one role per case. The copy is never synced:
`differs_from_client` (the field paths where the case and the client now
disagree) is computed on every read for a caller who may see the client
directory, and nothing resolves it silently. The debtor item carries
`GSI3PK CLIENT#<client_id>` / `GSI3SK <case createdAt>#<case id>`, which is
the `by-client` index a client's case list reads. "One client, one role" is
the link write's own condition (a transaction that checks the case's other
roles), not a read before it.

Divergence is resolved only by one of two explicit acts, each a whole-record
write: `POST …/debtors/<role>/copy-from-client` re-copies the client's
identity fields with `client` provenance (refused on a `filed` case — that is
an amendment), and `POST …/debtors/<role>/copy-to-client` writes the case's
values onto the client record, leaving the debtor as it was. **A copied field
keeps `client` provenance until a person changes that field**: the
questionnaire sends each save's map per field against the record it loaded
(the api-client's `revisedProvenance`), and the PUT refuses a `client` entry
that the stored record does not carry on that same, unchanged value — so
`client` is only ever kept, never minted, by a save.

**Merging two clients moves the link and nothing else.**
`POST /v1/firm/clients/<survivor>/merge` (`insolvia_core.client_merge`)
re-points every debtor naming the merged client — its `client_id` and its
`by-client` entry, one conditional write per case — to the survivor, and
archives the merged client with `merged_into`. The copied identity fields and
their provenance are untouched: a debtor's `client` entries keep naming the
client they were copied from, which is why a merged client is archived rather
than deleted, and why `differs_from_client` may now show paths where the
copy and the survivor disagree. Two clients who are both debtors on one case
are refused (one client, one role per case); a merged client cannot be
opened for a case, linked, edited, restored or merged again.

**A case belongs to a FIRM.** `firm_id` is the tenant; `created_by` is the
Cognito subject of whoever opened the matter and is an audit fact rather than a
permission — it grants nothing on its own. Reaching a case means being in its
firm **and** being an administrator, or carrying `access_all_cases`, or being
linked to that particular matter through a `case_assignment`. See
[ADR 0009](../adr/0009-a-case-belongs-to-a-firm.md).

This paragraph used to say the opposite — that ownership was a single
`owner_principal` and widening it later would not require touching the model.
Half of that was true and the half that was not is worth recording: ownership
is not in the primary key, so it *was* an attribute rewrite rather than a
re-key. But the `by-owner` index **was** the list path, and renaming an index
replaces it. Empty tables made that free; it would not have been.

`filing_professional` holds the attorney block (printed name, firm, address,
phone, email, bar number **and** bar state, signature date) or a bankruptcy
petition preparer (→ Form 119). It is not `created_by`, and it is not the
firm: the person who signs the petition, the person who opened the record, and
the tenant that owns it are three different facts. It also carries the
attorney's § 329(a) compensation disclosure (→ B2030): the fee agreed and
received, who paid and who will pay, whether it is shared outside the firm,
and the services covered or excluded — facts about this engagement, so they
live with the signer rather than on the petition. The balance due is derived.

### Value types

The IEPD's choices here are cheap to adopt now and expensive to retrofit.

| Type | Representation | Note |
|---|---|---|
| Money | Fixed-scale decimal, 2 places, carried as a string | Never a float. Currency is implicitly USD — the IEPD's currency attribute exists and is unused |
| Form date | `YYYY-MM-DD`, no time, no zone | "Date debt incurred" is a calendar fact, not an instant |
| System timestamp | RFC 3339, UTC | `created_at`, `confirmed_at`. Distinct from the above on purpose |
| Person name | Four discrete parts | Never one string; the IEPD has no single-string fallback for names |
| Address | Structured parts **and** a `raw` fallback | The IEPD itself carries a free-text fallback for addresses that will not parse |
| Identifier | UUIDv4, opaque, no PII | The same rule document storage applies to object keys. Ordering comes from `created_at`, not from the id |

**Tax identifiers are a special case.** B101 asks only for the last four
digits, but the IEPD's own published sample petitions carry the full, unmasked
SSN. So the full value must be stored, encrypted, with the last four served as
the default representation and the full value behind an explicit read that
writes an audit record. That audit log is not case data and does not live in
the case store. Designing for last-four-only would have to be undone at the
e-filing milestone.

Built as issue 13.12 ([#382](https://github.com/insolvia-ai/insolvia/issues/382));
`insolvia_core.tax_ids` owns the design and its docstring is the full
argument. The shape it settled on:

- **The debtor carries a reference, never the number**: `tax_id: {kind,
  last_four, ref}`. The number is its own **sealed item**, `SK=TAXID#<ref>`,
  envelope-encrypted under the environment's case KMS key — a fresh data key
  per sealed value, the key returned KMS-wrapped and stored beside the
  ciphertext — with an encryption context binding the **firm and the
  reference** (`{purpose, firm_id, tax_id_ref}`), never the case.
- **The ref, not the location, is the contract.** The item sits in the case's
  partition today; ADR 0022 (a client is not a case) needs a client's later
  matter to point at the *same* sealed identifier rather than copy it, so the
  ref is an opaque generated id a later case can re-use, and the
  firm-plus-ref context is what lets one ref serve two cases of one client
  while refusing replay onto another firm or another identifier.
- **Two reads.** The last four are on the plain record and travel with every
  debtor read (screens, B101, the generic routes, the MCP record tools). The
  full value is `tax_ids.read_tax_id`: it writes a `taxid.read` row — who,
  which case, which debtor (`filing_role`), why (`purpose`: `b121`, or
  `petition_review` for the review's byte-exact re-assembly) — and is called
  by nothing that answers a client. B121 is the only form that prints it.
- **Provenance is one entry, at `tax_id`.** The kind and the digits are one
  identifier entered in one act, and the last-four echo a client sends to
  keep a stored number carries no fact of its own.
- **A fixture's number comes from the SSA's never-issued advertising block**
  (987-65-4320..4329) — the tax-id analogue of the `.test` addresses, and
  the one exception the shape rules make. There is no reserved ITIN block.

## Creditors and claims

`creditor` and `claim` are separate. The creditor matrix wants one deduplicated
name-and-address per creditor; a debtor may owe the same creditor twice; and
credit-report extraction routinely yields several claims naming one issuer.
There is no reliable external key to dedupe on — the IEPD's creditor identifier
is optional, and consumer credit reports mask account numbers — so the match
key is name plus structured address, and it is a *suggestion to a human*, never
an automatic merge.

One `claim` entity spans all three schedules, discriminated by class:

```
claim {
  id, case_id, creditor_id
  class: secured | priority_unsecured | nonpriority_unsecured
  account_last4, date_incurred
  amount
  contingent, unliquidated, disputed        // independent booleans, not exclusive
  subject_to_offset                         // 106E/F, both parts
  who_incurred: debtor_1 | debtor_2 | both | at_least_one_plus_another
  community_debt
  notice_parties: [ { id, name, address, account_last4 } ]

  // class: secured
  asset_id                                  // the Schedule A/B asset the lien encumbers
  lien_position                             // 1 is the senior lien; explicit, never creation order
  collateral_description, collateral_value  // the override: collateral not on A/B, or a preferred value
  lien_nature: [ agreement | statutory | judgment | other(+text) ]   // check all that apply
  unsecured_amount_override                 // present = "entered manually"; its provenance is the record
  intention: surrender | retain_redeem | retain_reaffirm | retain_other(+explanation)   // B108
  // class: priority_unsecured
  priority_amount, nonpriority_amount
  priority_type: domestic_support | tax_and_government
               | death_or_injury_while_intoxicated | other(+text)
  // class: nonpriority_unsecured
  nonpriority_type: student_loan | separation_or_divorce
                  | pension_or_profit_sharing | other(+text)
}
```

Two amounts here are arithmetic and are not stored: the unsecured portion of a
secured claim, and a priority claim's total (priority plus nonpriority). The
unsecured portion is derived from three records — the claim's amount, the
collateral's value (the linked asset's, unless the claim types its own), and
the liens senior to it on the same asset: `amount − max(collateral − senior
liens, 0)`, floored at zero. The one stored exception is
`unsecured_amount_override`: a figure a preparer typed because the arithmetic
is wrong for this claim, whose presence is what "entered manually" means and
whose provenance entry records who entered it. The asset's secured total is
the sum of the claims linked to it. Only the three priority categories printed
on 106E/F are enumerated; the fuller §507 taxonomy lives in the instruction
booklet and belongs to the forms engine's mapping, not here.

## Assets and exemptions

```
asset {
  id, case_id
  category                          // the 106A/B line set
  property_types: [ ... ]           // Part 1 "check all that apply" — distinct from category
  description, county
  value_entire, value_portion_owned // both; 106C copies the portion owned
  ownership_interest: debtor_1 | debtor_2 | both | at_least_one_plus_another
  ownership_interest_description    // free text: fee simple, tenancy by the entireties, life estate
  community_property
  detail                            // category-specific: make/model/year/mileage, institution
}                                   // and account type, percentage ownership for entity interests
```

`value_entire` and `value_portion_owned` are two boxes on the form and neither
is derivable from the other — a half-owned house has no fixed relationship
between them once liens and tenancy are involved.

`exemption` references an `asset` and holds the statute citation and an amount
that is *either* a dollar figure *or* the "100% of fair market value up to the
statutory limit" election — mutually exclusive, so one nullable amount plus a
`claims_full_fmv` boolean, not two amount fields. It also carries
`acquired_within_1215_days`, which is a fact the debtor supplies, not a
threshold we configure.

## Income: 106I is not the income model

This is the one place the schema deliberately refuses to mirror the form.

106I asks for *current monthly* figures as of the filing date — the form says
"Estimate monthly income as of the date you file this form", and directs the
filer to convert non-monthly pay themselves. There is no pay-period date
anywhere on it. The means-test calculation needs the opposite: dated,
per-paycheck history across a six-month lookback. Pay-stub extraction produces
exactly that dated history. Storing only the 106I shape would discard the dates
on the way in and make the means test unimplementable.

So the model stores the history and treats the form as a projection:

```
employment      { id, case_id, debtor_id, employer_name, employer_address,
                  occupation, status, employed_since }

pay_period_record {
  id, case_id, employment_id
  period_start, period_end, pay_date     // all three; pay_date drives the lookback window
  gross, net
  deductions: [ { id, category, amount, description } ]
  frequency: weekly | biweekly | semimonthly | monthly | other
}

other_income_record {                    // dated non-wage receipts — CMI's other half
  id, case_id, debtor_id
  category                               // B122A-1's line taxonomy, plus the
                                         // § 101(10A)(B)(ii) EXCLUDED kinds, which are
                                         // stored like any receipt so the CMI derivation
                                         // shows the exclusion instead of dropping it
  received_on                            // drives the lookback window, like pay_date
  amount, expenses                       // expenses only on business/rental receipts
  payer, description
}

means_test_input {                       // the means test's entered figures, one per case
  id, case_id                            // WHATEVER its chapter: B122A-2 on Ch. 7,
                                         // B122C-1/C-2 on Ch. 13 read the same record
  ...the actual-expense and adjustment answers only the debtor can supply,
  plus the four only the Chapter 13 forms ask (the commitment-period
  contention, lines 40, 41 and 43's rows)
  (insolvia_core.means_test_inputs owns the field list, named by subject)...
}                                        // entered and confirmed, like income_summary;
                                         // one-per-case is the packet gate's check

income_summary  {
  id, case_id, debtor_id
  ...the 106I monthly lines...
  household_contributions, household_contributions_specify   // line 11, case-level on the form
  change_expected, change_explanation                        // line 13
}
```

`income_summary` is **entered and confirmed, not computed.** Pay-period records
inform it — the UI should offer the arithmetic — but 106I's question is an
estimate of what income *will be*, which a run of past pay stubs cannot answer
on its own. Treating it as derived would put an unreviewed number on a signed
form.

Deduction categories follow 106I's eight named lines (tax/FICA, mandatory
retirement, voluntary retirement, retirement-loan repayment, insurance,
domestic support, union dues, other) so that a stub's itemization maps without
a lossy translation. Line 11 is one value for the household rather than one per
debtor column; it is carried on the debtor-1 summary and the forms engine
renders it in its single box.

## The Chapter 13 plan

```
plan {                                   // one per case, like means_test_input
  id, case_id
  term_months                            // absent = the means test's commitment period
  payment_source: fixed | schedule_j_excess | disposable_income
  monthly_payment, step_payments: [ { id, start_month, monthly_payment } ]
  lump_sums: [ { id, month, amount, description } ]
  trustee_percentage                     // ≤ 10 (28 U.S.C. § 586(e))
  attorney_fees, attorney_fee_monthly
  secured_treatments: [ { id, claim_id,
      treatment: cure_and_maintain | cramdown | surrender,
      arrearage, arrearage_interest_rate, maintenance_payment,
      cramdown_value, interest_rate, monthly_payment } ]
  priority_percentage, priority_interest_rate
  unsecured_treatment: pot | percentage | amount
  unsecured_percentage, unsecured_amount, unsecured_interest_rate
  chapter_7_other_costs(+description), present_value_rate   // the § 1325(a)(4) side
}
```

**The record is the proposal only** (issue
[#366](https://github.com/insolvia-ai/insolvia/issues/366)); every figure it
produces — the waterfall per class, feasibility, the liquidation floor — is
derived, and lives in the API's calculator (`services/api`
`core/chapter13_plan.py`, whose docstring owns the rules). Two choices keep it
from restating another record: the term *overrides* the commitment period
rather than copying it, and the two non-typed payment sources *name* Schedule
J's line 23c or B122C-2's line 45 rather than storing the figure. Rates are
percentages carried as strings (`insolvia_core.fields.percentage`), for
money's reason. Alternatives compared on the plan screen are never stored:
they are unsaved proposals POSTed to the calculator, and the one a preparer
adopts is written through the ordinary confirmed save.

## Expenses and household

106J's roughly thirty expense lines are rows, not columns, keyed by a
`category` enum from the form's line set with an optional `specify_text`. Two
reasons: 106J-2 repeats the entire set for a second household, and a column
model would make that a schema change instead of a second `household` row.
`household` also carries the separate-household boolean and 106J's
`change_expected` narrative.

`dependent` records relationship, age, and whether they live with the debtor.
The form does not ask for dependents' names, so we do not store them.

## The SOFA is one typed-entry table

B107 is twenty-eight questions covering some two dozen unrelated repeating
shapes — prior addresses, income by period, payments to creditors, payments to
insiders, lawsuits, repossessions, setoffs, gifts, charitable contributions,
losses, payments to bankruptcy consultants, transfers, self-settled trusts,
closed accounts, safe deposit boxes, storage units, property held for others,
environmental notices and proceedings, business connections, financial
statements given — plus a few singletons (marital status; the consumer-debt
question).

Two dozen tables sharing nothing but provenance is not a model, it is a
transcription. Instead:

```
sofa_entry { id, case_id, entry_type, payload, provenance }
```

`entry_type` is a closed enum. `payload` is a frozen dataclass — one per entry
type, a discriminated union — produced by one `parse_<entry_type>` function per
type behind a dispatch table, in the API's core layer, server-side, as the
source of truth. It is not a loose dict: an untyped payload will not cross the
core boundary under the repo's strict typing, and the parse functions are the
only thing standing between a generic column and unvalidated data. The cost is
honest — this is roughly two dozen hand-written parsers, and they need per-type
tests. The benefit is that the annual form cycle adds or retires a question
without a migration.

## Provenance: every value knows where it came from

The review flow depends on this, and so does the AI posture. Provenance is
per-field, carried on every record as a map keyed by field path:

```
provenance: {
  "<field_path>": {
    source: staff_typed | ai_extracted | imported | library | client | client_answered,
    confirmed_by, confirmed_at,
    document_id, locator,
    extraction_id,          // the extraction_candidate.id this value came from
    confidence,
    library_creditor_id,    // the library_creditor.id this value was copied from
    client_id,              // the firm client this value was copied from
    copied_from_case_id     // the case a copied record came from (copy case, #355)
  }
}
```

`client` ([ADR 0022](../adr/0022-a-client-is-not-a-case.md)) is `library`'s
twin for a person: a debtor's identity copied from the firm's client
directory when the case was opened for that client, or when the client was
linked to the role. A copy, never a live link — a filed petition must not
change because the client record did — and, like `library`, outside the
confirmation rule, because a person chose the client.

`client_answered` ([ADR 0023](../adr/0023-client-portal-identity-and-isolation.md),
issue #363) is the debtor's own say-so: a value they gave through the
client portal's questionnaire, which a person at the firm accepted through
the review queue. It is **not** `client`, though the ADR first called it
that: `client` already meant "copied from the firm's directory, chosen by
staff, no confirmation", and an answer is the opposite on both counts —
so it gets its own name and sits **under the confirmation rule** below,
with `ai_extracted` and `imported`. `extraction_id` names the candidate it
was accepted from, and the candidate names the question. Like `client`, it
is **keep-only** on a whole-record save: a save may echo the entry on a
value nobody touched and is refused if it claims one
(`provenance.require_server_sources_kept`).

`copied_from_case_id` is not a source: a record copied into a new case
(see "The lifecycle") keeps the entry it had — a value a person confirmed
from a document is still that, and `document_id` still names the source
case's document — and gains the case it was copied from. Laundering every
copied origin into one new source would lose exactly the audit the entry
exists for.

`library` (issue 13.9 / #350) names a value copied from the firm's reusable
creditor library (`insolvia_core.library_creditors`) onto a case record — a
COPY, never a live link, so a later edit to the library entry never rewrites a
filed schedule. It sits with `staff_typed` rather than in the confirmation
rule below: a person chose the library entry, which is the same kind of act
as typing the value themselves, not something machine-supplied.

Field paths are dotted, with embedded list elements addressed by their `id`
rather than their position — `other_names_used[<id>].surname` — so that
reordering a list does not silently reattach provenance to the wrong value.
That is why embedded list elements carry an `id` at all.

**Two invariants, both enforced in the core layer's parse functions, so that
every write path inherits them rather than each endpoint remembering:**

1. Every populated field carries a provenance entry. A record with a value and
   no entry for it is rejected — otherwise the rule below is trivially evaded
   by omitting the key.
2. A field whose `source` is `ai_extracted` and whose `confirmed_at` is null
   cannot exist on a case record. Not "should not" — the write is rejected.

`imported` is subject to the same confirmation requirement as `ai_extracted`.
Machine-supplied is machine-supplied; the source system does not change who is
signing the form. So is `client_answered`: nothing a debtor types is case
data until someone at the firm confirms it.

Together those make "nothing extracted enters the case until a human confirms
it" a property of the store rather than a promise about the UI. Unconfirmed
output therefore lives outside the case entirely:

```
extraction_candidate {
  id, case_id, kind, payload,                  // payload mirrors its target entity
  document_id,                                 // optional — an MCP proposal has no source document
  origin: { channel: mcp | extraction | client, // which surface wrote this row, and as whom —
            client_id, subject },              // from the verified token, never an argument
  confidence, locator,
  status: pending | accepted | corrected | rejected | withdrawn,
  confirmed_by, confirmed_at,                  // the same act as the provenance fields
  corrected_payload,                           // what the human changed it to
  resulting_record_id
}
```

The MCP surface generalised this shape when it became the second writer
([mcp-surface.md](mcp-surface.md), issue
[#262](https://github.com/insolvia-ai/insolvia/issues/262)): `document_id`
became optional (an agent proposal has no source document), `origin` records
which OAuth client and which subject proposed the row — attribution the way
`uploaded_by` attributes a document — and `withdrawn` joined the status
vocabulary as a second terminal state: the proposer's own retraction of a
still-`pending` row, so a wrong batch does not sit in a paralegal's review
queue. One review queue, one status vocabulary, one confirmation act; the
review UI (8.9) reads both streams without knowing which is which beyond the
origin it displays. The store implementation lives in
`insolvia_core.candidates` (it owns the item shape), where it graduated from
`services/mcp` when the review flow became its second importer (ADR 0012's
admission rule; issues 8.7-8.9).

Corrections and rejections are retained after review — and so are
withdrawals. They are the only measurement of extraction (and agent) quality
we will ever get, and deleting them on accept throws that away.

### Answers from the client portal

The third writer ([ADR 0023](../adr/0023-client-portal-identity-and-isolation.md)
PR 4, issue #363) is the debtor, answering the portal questionnaire. Its
rows are the same `extraction_candidate`, in the same queue:

- `origin = {channel: client, client_id: <the portal app client>, subject}`,
  from the verified portal token.
- `locator = {kind: question, section_id, question_id, filing_role}` — a
  QUESTION, not a page. `section_id` is one of
  `insolvia_core.questionnaire`'s section ids; `question_id` one of
  `insolvia_core.questions`' (both are stable contracts: added to, never
  renamed); `filing_role` is who the answer is for, bounded by the
  binding's roles. The locator stays on the candidate; it is not copied
  into provenance, whose `locator` means a place on a page.
- `payload` is a fragment of the target's own body shape, built from the
  question's inputs and validated by the target's own parse function. A
  question names its target: `debtors` (the debtor's own fields — name,
  other names, both addresses, phone, mobile, email) or one generic
  collection (one new record per answer: an asset, a creditor, an
  employment, an expense, a contract or lease).
- **Edit-while-pending.** The client may change (`PUT`) or withdraw
  (`DELETE`) their own `pending` row; once staff have acted, the row is
  immutable and a change is a new candidate. That is also the
  questionnaire's resume state — there is no second draft store. A
  non-repeating question takes one pending answer per client and role at a
  time; a client holds at most 200 pending answers.

**Acceptance.** A record answer is accepted exactly as an extracted record
is: a new record, its fields `client_answered` with the confirmation pair
and `extraction_id`, `staff_typed` where the reviewer corrected them. An
employment's `debtor_id` and an expense's `household_id` are filled from
the case at acceptance (the answering debtor; the main household), on the
staff side — the portal never reads case records, so it knows no record
ids. A **debtor answer is merged into that debtor's record**, which exists
from the case's opening (ADR 0022): the answered paths are minted
`client_answered` (or `staff_typed` where corrected), every other field
keeps its entry, and an appended other name is added by its id rather than
replacing the list. A Debtor 2 not yet linked is a 409.

**What the client reads.** Only their own rows (`origin.subject == self`),
in sections the firm still shows, as `{questionId, sectionId, filingRole,
value, status}` — never the corrected payload, the reviewer or the
resulting record id. A section switched off stops taking answers and hides
its answers from the client; staff keep reviewing them.

**What the first catalogue leaves out**, on purpose: the tax id (a sealed
item — a candidate payload is no place for the digits); anything that is a
second record's meaning (a claim needs its creditor, so `debts.creditor`
asks for the creditor alone, not the amount or class); and anything that
is the attorney's judgement rather than the debtor's fact (venue, credit
counselling status, exemptions, intentions, priority, the means test).
The SOFA, Schedule I's monthly figures and the household and dependents
are the next to add.

## Documents and locators

```
document { id, case_id, kind, file_name, content_type, byte_size, page_count,
           uploaded_by, uploaded_at, storage_ref, sha256,
           status,                               // pending | stored
           etag }                                // set once the bytes are seen

locator  { document_id, page,                    // 1-based
           region: { x, y, width, height } }     // fractions of the page box,
                                                 // origin top-left, 0.0–1.0
```

Fractional coordinates rather than points, because the review UI renders pages
at whatever width the viewport gives it and a point-based box would need the
render scale to be stored alongside. `storage_ref` is opaque here — how bytes
are stored, and how access is brokered, belong to the document-upload work
(issue 8.6), which settled both: the object key is
`cases/<case_id>/<document_id>` in the bucket
[`infra/modules/case_documents`](../../infra/modules/case_documents/main.tf)
creates, and access is brokered by short-lived, server-minted presigned URLs —
`services/api/src/insolvia_api/api/routes/documents.py` argues that against
[ADR 0001](../adr/0001-client-stays-dumb-trust-boundary.md).

The identifier rule above applies to that key without exception: **the
uploader's file name is metadata on the record and never appears in the key**,
because a key is visible in bucket inventories, S3 access logs, CloudTrail data
events and every presigned URL, none of which are access-controlled the way the
record is. That is what `file_name` is for, and it is one of only two fields
here the caller supplies — `kind` is the other. Both are the uploader's own
claim about a file they are handing us, which is why a document record carries
no provenance map: `uploaded_by` already attributes them, and a document is
provenance's object rather than its subject.

**A document row is created before its bytes exist, and `status` is what says
so.** An upload is two steps — the API records the document and mints a
presigned PUT, the client uploads, and a second call confirms it — so a row is
`pending` until this server has done a HeadObject and seen the object, then
`stored`. The distinction is load-bearing in three places: `byte_size` is the
client's *declared* size while pending and the size S3 counted once stored;
`etag` exists only in the second state; and the bucket expires objects still
carrying an `upload=unconfirmed` tag after a day, which is safe only because
confirmation is the one thing that clears it. Pending rows are returned by the
listing like any other — a document whose upload failed is something the user
needs to see and retry, not something to hide.

## Storage validation is not filing completeness

Intake is progressive: a half-finished questionnaire must persist, so the
storage layer validates **shape and type only** and accepts absent values
everywhere. Completeness — every field a given chapter's forms actually require
— is a separate pre-filing check against the form mapping, and it belongs to
the forms engine, not to these parse functions. Conflating them would make it
impossible to save an intake in progress, which is the one thing the
questionnaire must never fail at.

## Derived values are computed, never stored

Storing a total means owning a reconciliation bug. Every one of these is
arithmetic over records above, computed server-side when a form is rendered —
never in the client, which does not hold the data to check it (ADR 0001):

| Form | Derived |
|---|---|
| 106Sum | The entire form — every line is copied forward from another schedule |
| 106A/B | All seven part subtotals and the Part 8 rollup |
| 106D | Column A total; each claim's collateral value and description through `asset_id`, and the unsecured portion (senior liens included) |
| 106E/F | Each priority claim's total; Part 4's statistical rollup, lines 6a–6j |
| 106I | Gross income, total deductions, take-home pay, total other income, combined monthly income |
| 106J | Total expenses, the 106J-2 carry-forward, and net monthly income |

The forms that quote a figure from another form — 106Sum pulling current
monthly income from Form 122A-1/122B/122C-1 — are cross-form projections and
belong to the forms engine and the means-test work, not to storage.

## Statutory constants are configuration

Several thresholds on these forms — the homestead cap question on 106C, B107's
payment-reporting floors — are dollar figures that adjust on a three-year
cycle. They are values with effective dates, not constants in code and not
columns. The
[regulatory source register](../business/regulatory-source-register.html) owns
the current figures and the adjustment calendar.

What this model commits to is only that **a case records which constant set
applied when it was prepared**, via `constants_set_id`. Where those sets live
and how they are versioned is shared with the forms engine's effective-dating
problem and is settled in [`effective-dating.md`](effective-dating.md), not
here: `constants_set_id` is the pinned release id of the `code/dollar-amounts`
series, and `form_revisions` pins each form series the same way.

## The external-system seam — an origin pointer, not sync

[ADR 0013](../adr/0013-mcp-server-replaces-direct-pms-integration.md) ended
direct practice-management integration: an attorney's AI harness moves data
between their PMS and us through our MCP server, so there is no sync engine on
our side — no polling, no webhooks, no push/pull bookkeeping. What survives of
the old seam is provenance:

```
external_refs: [ { system, external_id, external_url, last_seen_at } ]
```

Every case-scoped entity may carry it: a record a harness sourced from MyCase
(or any other system) should say where it came from, the same way a confirmed
value says who confirmed it. `extraction_candidate` does not — it is scratch
space that never leaves this system.

`sync_state` (`last_pushed_at` / `last_pulled_at` / `content_hash`) was
specified alongside it and is **deleted from the model** — nothing ever
implemented it, and it described the sync engine ADR 0013 decided against.

The seam's real successor is behavioural, not a field: **an MCP client writes
candidate records, never confirmed case data** — the confirm-before-entry
invariant above governs agent writes exactly as it governs extraction. The
tool surface that enforces this is the MCP milestone's design issue
([#260](https://github.com/insolvia-ai/insolvia/issues/260)).

## Amendments — a per-item flag, not a copy of the case

A schedule changed after filing is filed again as an amendment (Fed. R.
Bankr. P. 1009) naming only what changed. The model carries that as one
generic attribute beside `provenance` on every case-scoped entity (issue
#370):

```
amended: bool          // false unless set; always present on the wire
```

- **It is not body data.** It needs no provenance entry, is never projected
  as a value, and a PUT that omits it keeps the stored flag — "replace the
  record whole" is about the body.
- **It is only meaningful once the case is `filed`.** The API route refuses
  `true` on any other status (409), rather than storing a flag that has
  nothing to amend. The core parser checks shape only; the route is the
  layer that holds the case's status.
- **It drives one render, `OutputOptions.amended_only`.** Only the schedules
  with an amended item print, each showing only those items and its own
  "amended filing" caption ticked, followed by B106Sum and B106Dec and a
  generated cover sheet (not an official form). It is the one packet a filed
  case may still assemble, it resolves today's form revisions, and it writes
  no pins to the case: the case's `form_revisions` go on describing the
  original filing, and each amendment packet records its own.

The case header's `is_amended` is a different fact (the whole-case box on
every caption) and is still unset by anything.

## What this demands of the store

Handing these constraints, not a decision, to the encrypted-case-store work:

- Every entity is case-scoped, and every read is case-scoped and
  **firm-scoped** — plus, for a user without `access_all_cases`, restricted to
  the matters they are linked to. The only cross-case read paths are the two
  listings, and which one a caller uses depends on their permissions.
- The dominant access patterns are *fetch a whole case* and *list one entity
  type within a case*. Cross-case queries are administrative and rare.
- Twenty-five entity types, all small, most of them lists — no single record
  is large, but a fully-populated case is many records read together.
- Tax identifiers need protection distinct from the surrounding record, and
  reads of them need to be individually auditable, into a log that is not case
  data.
- Provenance travels with its record and must be written in the same operation
  as the value it describes. A confirmed value with lost provenance is worse
  than no value.
- `extraction_candidate` is high-churn and short-lived relative to case data,
  and is never read by the forms engine.

The last point is the one worth weighing hardest: the model is relational in
shape, while the only precedent in this codebase is a single-partition
DynamoDB table. The access patterns above are compatible with a single-table
design keyed on the case — but that compatibility depends on there being no
ad-hoc cross-case reporting, and that assumption should be made explicitly
rather than inherited.

## Tenancy

A case belongs to a firm, and the entities that express that are:

```
firm {
  id, name, status: active | suspended, created_at, updated_at
  default_court, default_division   // a registry reference, for new cases (#360)
  default_chapter
  letterhead: { name, address, phone, email }
}

firm_user {                        // keyed (firm_id, subject) — no id of its own
  firm_id, subject                 // subject is the Cognito sub
  email
  first_name, last_name                     // either may be "" — see below
  role: attorney | paralegal | staff        // drives DEFAULTS, decides nothing
  is_admin: bool                            // every feature, every case, the staff list
  access_all_cases: bool                    // every case, without per-case linking
  permissions: { <feature>: add_edit | view_only | hidden }
  status: active | disabled, created_at, updated_at
  signature_block: { bar_number, bar_state, firm_name, address, phone, email }   // #360
}

case_assignment {                  // an item in the CASE's partition
  case_id, subject, case_created_at, assigned_at, assigned_by
}
```

`firm_user` has no id of its own because `subject` already is one:
server-minted, unique, immutable, and the only thing an access token carries. A
surrogate would be a third name for one row. That is **not** the debtor
situation in reverse — a debtor arrives with no server-owned identifier, so a
key there has to be invented from the data.

**A name is two fields and no stored display string.** The one a screen renders
is composed on read, so nothing can store a whole that disagrees with its
halves. `""` on either half is a real value meaning "never recorded": rows
written before the split carry one display string, and the read path derives
what it can from it by splitting on the last space — so a lone "Cher" yields an
empty surname rather than a guess. The client asks such a user for their name
before letting them work, which is the only reason an empty half is worth
distinguishing from a blank one. `displayName` remains on the **wire**, derived,
because most callers only ever render a name.

`role`, `is_admin`, `access_all_cases` and `permissions` are four independent
axes on purpose. Collapsing role into access is the trap where "attorney"
quietly comes to mean "can see everything"; collapsing `is_admin` into
`access_all_cases` would mean the only way to see every matter is also to be
able to change everyone's permissions.

**The firm's defaults prefill; they never decide** (issue #360). A new case's
create form preselects `default_court`/`default_division`/`default_chapter`,
and the petition's signer block copies an attorney's `signature_block` (keyed
exactly as `filing_professional` is, so the copy needs no mapping) with the
`letterhead` filling the firm lines a block leaves blank — onto the form, not
onto the record. The preparer saves, and the record carries `staff_typed`
provenance like anything else they confirmed. A `signature_block` is the
attorney's own fact and self-service on `PATCH /v1/me`; every colleague may
read it from the directory, because the prefill is case work and a bar number
is printed on every filing.

The feature list is ours — `insolvia_core.firms.FEATURES` owns it — and the
default for anything not in a user's map is `hidden`. That is what lets a
feature be listed before it exists (`extraction_review` is) without arriving
already granted to every row written before it was named. `clients`
([ADR 0022](../adr/0022-a-client-is-not-a-case.md)) arrived that way on
purpose: every existing row has it hidden until an admin grants it.

**A client is not a debtor.** The firm's client directory
(`insolvia_core.firm_clients`, `/v1/firm/clients`) is a firm-scoped person
record in the firm table, beside the library creditors; a debtor is that
person's identity as copied into one case (see "Identity" above for the
copy, `client_id` and the `by-client` index). Opening a case therefore needs
`clients` at `view_only` as well as `cases` at `add_edit`, and a client's own
case list (`GET /v1/firm/clients/<id>/cases`) is filtered per case through
`may_see_case` and shows no count of the rest.

## Not here, on purpose

- **Plan arithmetic.** The waterfall, feasibility and the § 1325(a)(4)
  liquidation test are the API's calculator (`core/chapter13_plan.py`),
  computed from `plan` and the records above and never stored. Official
  Form 113 prints the calculator's figures (issue #367); which districts
  take a local plan form instead is the court registry's
  `chapter_13_plan` fact, not a case attribute.
- **Means-test arithmetic (122A, 122C).** The calculations — § 707(b) on
  Chapter 7, § 1325(b) on Chapter 13 — are the API's engine
  (`services/api` `core/means_test.py`), computed from the records above and
  never stored. B122B (Chapter 11) is not built.
- **The forms-engine field mapping.** Which entity attribute lands on which
  form line, per revision — including the completeness check and the constant
  sets above. That is the forms milestone's artifact.
- **The MCP tool surface.** Which tools expose these entities to a harness,
  and the candidate-write flow — the MCP milestone's design artifact
  ([#260](https://github.com/insolvia-ai/insolvia/issues/260)); the seam it
  builds on is above.
- **Claims filing.** A separate CM/ECF specification from the petition IEPD,
  and a later concern.

## Related

- [`architecture.md`](architecture.md) — env model, hosting, the PR-gate contract
- [`terraform.md`](terraform.md) — state, naming, deploy order
- [ADR 0001](../adr/0001-client-stays-dumb-trust-boundary.md) — the API brokers
  every read and write; server-side validation is the source of truth
- [`docs/business/regulatory-source-register.html`](../business/regulatory-source-register.html)
  — the forms, their authority, their revision cadence, and the statutory figures
- The bankruptcy IEPD packages are published openly on
  [PACER's developer resources page](https://pacer.uscourts.gov/file-case/developer-resources)
  (NIEM 2.0-derived; current package `NGRel1.7`, dated 2021-10-01)
