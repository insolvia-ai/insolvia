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
restate it. A case records the revisions it was prepared against, so that a
case begun before a revision keeps answering the questions it was actually
asked.

## Core entities

Twenty-three case-scoped types. Two more — the tax-identifier access log and
the effective-dated statutory constant sets — are referenced here but live
outside the case store; see below.

| Entity | Cardinality | Feeds |
|---|---|---|
| `case` | root | B101 header, chapter, venue district |
| `petition` | one | B101 Pt.2–6 case-level answers |
| `debtor` | 1–2, plus an optional non-filing spouse | B101 Pt.1, 106I |
| `prior_case` | many | B101 line 9 |
| `related_case` | many | B101 line 10 |
| `sole_proprietorship` | many | B101 line 12 |
| `filing_professional` | 0–2 | B101 Pt.7 |
| `creditor` | many, deduplicated | Creditor matrix |
| `claim` | many, references a `creditor` | 106D, 106E/F |
| `asset` | many | 106A/B |
| `exemption` | many, references an `asset` | 106C |
| `contract_lease` | many | 106G |
| `codebtor` | many | 106H Pt.2 |
| `community_household_member` | many | 106H line 2, B107 Q3 |
| `employment` | many | 106I Pt.1 |
| `pay_period_record` | many, references an `employment` | Means test |
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
  id, owner_principal
  chapter: 7 | 11 | 12 | 13
  district, status: intake | ready_to_file | filed, filed_at
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
}
```

`petition` is separate from `case` because it is the answers to one form,
churned during intake and untouched afterwards, while `case` holds identity and
lifecycle that everything else references.

## Identity, and why joint debtors are two records

**A joint filing is two debtor records under one case, not one record with
spouse-suffixed columns.** The IEPD models it this way — `BankruptcyDebtor2` is
declared as a substitution for `BankruptcyDebtor1`, each carrying its own name,
own tax identification, own signature — and the forms follow. B101 prints a
full second column for credit counseling *and for venue*; 106I's second column
may belong to a spouse who is not filing at all.

```
debtor {
  id, case_id
  filing_role: debtor_1 | debtor_2 | non_filing_spouse
  name: { given, middle, surname, suffix }
  other_names_used: [ { id, given, middle, surname, business_name } ]  // 8-year lookback
  tax_id: { kind: ssn | itin, value }                                  // encrypted; see below
  employer_ids: [ ein ]
  residence_address, mailing_address
  phone, mobile, email
  venue: { basis: lived_longest_180_days | other, explanation }        // per debtor, B101 line 6
  credit_counseling: {
    status,                                                            // four-way, B101 line 15
    exemption_reason: incapacity | disability | active_duty
  }
  signed_at
}
```

Widening ownership later — a firm with staff, rather than one signed-in user —
should not require touching this. Ownership lives on `case` as a single
`owner_principal`, and nothing else references the owner.

`filing_professional` holds the attorney block (printed name, firm, address,
phone, email, bar number **and** bar state, signature date) or a bankruptcy
petition preparer (→ Form 119). It is not `owner_principal`: the person who
signs the petition and the account that owns the record are different facts.

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
  collateral_description, collateral_value
  lien_nature: [ agreement | statutory | judgment | other(+text) ]   // check all that apply
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
secured claim (claim amount less collateral value), and a priority claim's
total (priority plus nonpriority). Only the three priority categories printed
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
    source: staff_typed | ai_extracted | imported,
    confirmed_by, confirmed_at,
    document_id, locator,
    extraction_id,          // the extraction_candidate.id this value came from
    confidence
  }
}
```

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
signing the form.

Together those make "nothing extracted enters the case until a human confirms
it" a property of the store rather than a promise about the UI. Unconfirmed
output therefore lives outside the case entirely:

```
extraction_candidate {
  id, case_id, document_id, kind, payload,     // payload mirrors its target entity
  confidence, locator,
  status: pending | accepted | corrected | rejected,
  confirmed_by, confirmed_at,                  // the same act as the provenance fields
  corrected_payload,                           // what the human changed it to
  resulting_record_id
}
```

Corrections and rejections are retained after review. They are the only
measurement of extraction quality we will ever get, and deleting them on accept
throws that away.

## Documents and locators

```
document { id, case_id, kind, content_type, byte_size, page_count,
           uploaded_by, uploaded_at, storage_ref, sha256 }

locator  { document_id, page,                    // 1-based
           region: { x, y, width, height } }     // fractions of the page box,
                                                 // origin top-left, 0.0–1.0
```

Fractional coordinates rather than points, because the review UI renders pages
at whatever width the viewport gives it and a point-based box would need the
render scale to be stored alongside. `storage_ref` is opaque here — how bytes
are stored, and how access is brokered, belong to the document-upload work.

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
| 106D | Column A total; the unsecured portion of each claim |
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
problem and is settled there, not here.

## The MyCase sync seam — designed, not built

Room for sync, and nothing more. No sync code, no polling, no webhook handler:
those wait on the MyCase spike answering write coverage and push-versus-poll.

```
external_refs: [ { system, external_id, external_url, last_seen_at } ]
sync_state:    { last_pushed_at, last_pulled_at, content_hash }
```

Every case-scoped entity carries both fields, unused. `extraction_candidate`
does not — it is scratch space that never leaves this system.

`content_hash` is record-level, and this is a **deliberate narrowing** of the
per-field sync state the milestone asked for. Per-field state is only worth its
complexity once we know whether MyCase writes are field-granular, which is
exactly what the spike is for; widening a record-level hash into a field map
later is additive, and the reverse is not.

## What this demands of the store

Handing these constraints, not a decision, to the encrypted-case-store work:

- Every entity is case-scoped, and every read is both case-scoped and
  owner-scoped. There is no cross-case read path in this model.
- The dominant access patterns are *fetch a whole case* and *list one entity
  type within a case*. Cross-case queries are administrative and rare.
- Twenty-three entity types, all small, most of them lists — no single record
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

## Not here, on purpose

- **Means-test forms 122A/B/C.** Their own milestone. `pay_period_record`
  exists so they are implementable; their calculations are not modelled.
- **The forms-engine field mapping.** Which entity attribute lands on which
  form line, per revision — including the completeness check and the constant
  sets above. That is the forms milestone's artifact.
- **MyCase sync itself** — seam only, above.
- **Firms, staff, and shared case access.** Ownership is a single principal.
  Not built speculatively, but nothing else references the owner.
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
