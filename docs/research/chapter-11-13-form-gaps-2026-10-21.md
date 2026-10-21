# Chapter 11 and 13 form gaps — 2026-10-21

> **Research for [#469](https://github.com/insolvia-ai/insolvia/issues/469)**,
> part of the launch map [#467](https://github.com/insolvia-ai/insolvia/issues/467):
> *"Model for N, launch at one. Chapters 11 and 13 are modelled and checked
> against their official forms and fields; their packets are not built yet."*
> The question: for an **individual** debtor, which official forms do Chapter 13
> and Chapter 11 add to the Chapter 7 set already in `forms/`, and what do those
> forms need that the case model in
> [`case-data-model.md`](../reference/case-data-model.md) has no place for?

## 1. Summary

- **Chapter 13 is mostly modelled already.** `forms/` already specs B113 (the
  national plan), B122C-1 and B122C-2. The model has a `plan` record, a
  calculator and `means_test_input`. What is missing is **in the gaps B113's own
  spec admits**: a plan that has versions; how payments reach the trustee; tax
  refunds; § 3.3 claims (the 910-day vehicle claims); § 3.4 § 522(f) lien
  avoidance; more than one unsecured class (§§ 5.2, 5.3); a contract cure paid
  through the trustee; vesting (Part 7); and nonstandard provisions (Part 8).
  B113's spec has 15 non-signature fields marked `unmapped`, and 16 more that
  always print `None` / `Not included` / 0.00 because the model has no place
  for what they ask (§3.2).
- **Chapter 13 also adds after-filing facts the model does not hold**:
  confirmation, a post-confirmation modification, the B2830 discharge
  certification, debtor-education completion, and the claims register. The
  claims register is the proofs of claim filed against the scheduled claims,
  which a Chapter 13 plan pays out on.
- **Chapter 11 for an individual is mostly unmodelled.** Its filing set adds
  B104 (the 20 largest unsecured creditors who are not insiders) and B122B
  (except under Subchapter V). A small-business or Subchapter V case adds the
  § 1116(1) financial statements with the petition, a Form 425C monthly
  operating report, and a plan that substantially conforms to Form 425A.
  Non-Subchapter-V small-business cases add a Form 425B disclosure statement.
  An estate holding ≥ 20% of an entity adds Form 426.
- **The Chapter 11 gaps are whole entities, not attributes**:
  - the operating business, as more than B101 line 12's name, address and type;
  - the debtor-in-possession role;
  - insiders;
  - plan **classes**, each impaired or unimpaired, with a treatment and claims
    as members;
  - unclassified claims;
  - votes and ballots;
  - a versioned plan and disclosure statement, with a proponent who need not be
    the debtor;
  - multi-year projections and a per-class liquidation analysis;
  - and a **monthly, post-petition reporting series**: cash, receipts,
    disbursements, payables, receivables, employees and professional fees.
    It is the first case data dated *after* the petition.
- **The Chapter 7 model carries most of the petition-day packet for both
  chapters**: B101 (chapter, line 13's small-business and Subchapter V
  election), every B106 schedule, B107, B121 and B2030. B122B's lines are the
  same income records the 122A/122C pair already reads.
- **Chapter 12** (family farmers and fishermen, § 109(f)) has no official plan
  form and sits closest to Chapter 13. It is the Chapter 13 shape plus farm
  operations and Rule 2015(c)'s debtor-in-possession duties. It is out of scope
  here.
- **Three drifts found in passing** (§6). The B113, B122C-1 and B122C-2 specs
  still say the model has no case-number attribute; `case.case_number` exists.
  Form 423 was abrogated on 2024-12-01. B122B is published as a flat PDF with no
  AcroForm.

**This is a snapshot, not an owner of any fact, and it is not legal advice.**
Each claim is marked by source (see [Sources](#sources)). **[P]** means a
primary source was read: a form, a rule, a statute, or a repo file. **[S]**
means secondary, or primary but not read in this session.

---

## 2. Method, and which fields were read from a PDF

The official PDFs were downloaded from uscourts.gov on 2026-10-21. Each was
walked with pypdf for AcroForm widgets and its text extracted. That is the same
method `forms/acroform/` uses, but nothing was committed under `forms/`.

| Form | Rev. | AcroForm | Read |
|---|---|---|---|
| 104 | 12/15 | **371 widgets**, 5 pages | Widgets and text. 20 creditor blocks, each with: name; address; contact; contact phone; nature of claim; contingent / unliquidated / disputed / none-apply checkboxes; a lien yes/no; total claim; value of security; unsecured claim |
| 122B | 12/21 | **none** (flat) | Text: Part 1 lines 1–11 and the signature |
| 425A | 02/20 | none (a word-processing template) | Text: all 10 articles and the Subchapter V background |
| 425B | 12/17 | none | Text: table of contents, Parts I–VI, Exhibits A–G |
| 425C | 12/17 | **90 widgets**, 4 pages | Widgets and text. Names are meaningless (`fld.1.N`, `Check Box.N`). There is one checkbox widget per questionnaire line against three printed columns (Yes / No / N/A); how a choice is meant to be encoded was not resolved |
| 426 | 12/17 | none | Text: pages 1–2 (the entity list, Exhibits A–E, the verification) |
| B2830 | 04/25 | none | Text: Parts I–IV |
| 423 | — | none | The abrogation notice |
| 101 | 06/24 | (already dumped in `forms/acroform/b101.json`) | Text of line 13 only |

The B113, B122C-1 and B122C-2 rows below come from the specs already in
`forms/specs/`. They were made from their PDFs under issues #365 and #367. No
PDF was re-read for them here.

---

## 3. Chapter 13 (individual)

### 3.1 Forms, and when each is required

| Form | When | Source | In `forms/`? |
|---|---|---|---|
| 101, 106Sum/Dec, 106A/B–J, 107, 121 | With the petition or within 14 days (Rule 1007(b)(1), (c)) | [P11] | yes |
| 122C-1 | Every Chapter 13 debtor (Rule 1007(b)(6)(A)) | [P11] | yes |
| 122C-2 | When current monthly income exceeds the state median (Rule 1007(b)(6)(B)) | [P11] | yes |
| 113, the plan | With the petition or within 14 days (Rule 3015(b)), on Form 113 unless the district adopted a local form under Rule 3015.1 (Rule 3015(c)) | [P13] | yes. The local-form fact is the court registry's `chapter_13_plan` [R1] |
| B2030 | The attorney's § 329 disclosure, as on Chapter 7 | [R1] | yes |
| B2010 | § 342(b) notice, as on Chapter 7 | [R1] | yes |
| 103A | Installment fee application; no waiver outside Chapter 7 (103B is "Chapter 7 only") | [P1], B101 line 8 | not specced; `petition.fee_handling` exists |
| B108 | **Not** filed: Chapter 7 only | [P1] | — |
| A Rule 1007(b)(8) statement | When a § 522(b)(3)(A) homestead-type exemption exceeds the § 522(q)(1) figure: a statement about any pending proceeding under § 522(q)(1)(A)/(B). No official form | [P11] | no |
| Debtor-education certificate | Before discharge, unless the provider has notified the court (Rule 1007(b)(7)). Form 423 is **abrogated** (2024-12-01); the provider's certificate is filed instead | [P9], [P11] | n/a: a document, not a form |
| B2830 | At discharge: the § 1328(a) domestic-support certification and the § 1328(h) § 522(q) certification | [P8] | no |
| A modified plan | After confirmation, under § 1329 / Rule 3015(h): proponent, proposed modification, 21 days' notice | [P13] | no |
| 410C13-M1/M1R, M2/M2R, N/NR; 4100N/4100R | Rule 3002.1 home-mortgage claim forms: status motions, responses, the trustee's notices of disbursement and final cure. Debtor-side responses only | [P1]. The effective date of the 410C13 set was not checked [S] | no |
| 309I, 2300B, 2310B, 3180W, 3180WH | Court-issued: notice of case, confirmation order, modification order, discharge, hardship discharge | [P1] | n/a (court) |
| Rule 2015(d) duties | A Chapter 13 debtor **engaged in business** keeps the (a)(2)–(4) records of receipts and disbursements, and gives the notices | [P12] | no |

### 3.2 Gaps: fields, entities and relationships the model has no place for

**From B113 itself.** The spec marks these lines `unmapped`, or derives them as
*always* `None` [R2]:

| B113 line | What the form asks | What the model lacks |
|---|---|---|
| Caption "Amended plan", sections changed | An amended plan names the sections it changes | **A plan with versions.** `plan` is "one per case" [R1]. A filed plan changes in three ways: before confirmation (§ 1323), the confirmed version, and after confirmation (§ 1329, Rule 3015(h)) [P13]. Each is a dated document that must be kept: the confirmed one is what binds (Rule 3015(g)), and a modification is filed against it. The per-item `amended` flag [R1] is Rule 1009's schedule amendment and does not fit a plan. **Need:** `plan_version { plan_id, sequence, kind: original \| pre_confirmation_amendment \| post_confirmation_modification, filed_at, sections_changed, status: proposed \| confirmed \| superseded \| withdrawn }`, with the confirmed version pointed to from the case |
| § 2.2 | How payments reach the trustee: payroll deduction, direct, or other | `plan.payment_method` (+ the employer for a wage order, which is an `employment` reference) |
| § 2.3 | Income tax refunds: retain, turn over, or other | `plan.tax_refund_treatment` (+ text) |
| § 3.3 | Secured claims excluded from § 506: the 910-day vehicle and the one-year purchase-money claims | A `secured_treatments[].treatment` value (`paid_in_full_506_excluded`), and a `claim` fact that drives it (the purchase date and whether the collateral is a vehicle for personal use). `claim.date_incurred` exists; the purpose does not |
| § 3.4 / Part 1 line 1.2 | Lien avoidance under § 522(f) | **A relationship that does not exist yet:** a judicial lien or nonpossessory, non-purchase-money lien (`claim`), on exempt property (`asset` + `exemption`), with the amount avoided. The lien-impairment arithmetic is derived; the election to avoid is a stored choice |
| § 4.5 | Domestic-support obligations assigned to a government unit, paid less than in full (§ 1322(a)(4)) | A priority treatment below 100% on a per-claim basis. `plan.priority_percentage` is one figure for the whole class |
| § 5.2 | Maintenance of payments and cure on long-term unsecured debt (§ 1322(b)(5)) | A treatment on an unsecured claim |
| § 5.3 | Separately classified nonpriority unsecured claims (§ 1322(b)(1); for example, co-signed consumer debt) | **Classes.** `plan.unsecured_treatment` is one class. This is the Chapter 13 echo of the Chapter 11 gap in §4.2 |
| § 6.1, disbursed by the trustee | A contract or lease cure paid through the trustee | Cure amount and payments on an assumed `contract_lease` |
| Part 7 | When property of the estate vests: confirmation, discharge, or other | `plan.vesting` (+ text). The district's practice often decides it [R2] |
| Part 8 / Part 1 line 1.3 | Nonstandard provisions. Rule 3015(c) makes them effective only in that section [P13] | `plan.nonstandard_provisions: [ { id, text } ]`. Part 1's box is then derived. This is also where "more rows than the form prints" would go [R3] |

**From B122C-2** [R2]: the itemised secured-debt and cure rows are not modelled
(only totals are), and nor is Part 3's post-petition change narrative. These are
known gaps, already noted in the spec.

**Outside the plan:**

- **Lifecycle.** `status` runs intake → ready_to_file → filed → discharged /
  dismissed → closed [R1]. A Chapter 13 needs **confirmed** (and
  confirmation-denied) as a state or dated fact. It also needs **conversion**
  (13 → 7 is common) as an event that changes `case.chapter` on a filed case,
  and the **hardship discharge** (3180WH) as a distinct outcome [P1].
- **Trustee payments.** § 1326(a)(1) payments start within 30 days. The
  model has a proposal, not a ledger of what was paid. It is not needed for the
  forms; it is needed for a modification and for the Rule 3002.1 responses.
  [S: § 1326 not read]
- **The claims register.** Proofs of claim (Form 410) filed against the
  scheduled `claim`s, with the allowed amounts the plan actually pays out. The
  model lists "claims filing" as *not here, on purpose* [R1]. For Chapter 13
  that deferral reaches the plan's arithmetic once the case is filed.
- **B2830.** Whether a domestic-support obligation was owed and paid in full.
  If so, the debtor's current address and current employer with the employer's
  address. And a § 522(q) yes/no, which is derivable from `exemption` + `asset`
  + the constant set [P8]. The address and employer are `debtor` and
  `employment` facts *as of discharge*, so they need a date.
- **Debtor education.** A completion fact (provider, date, certificate
  `document`) [P9].
- **Rule 1007(b)(8).** Whether any § 522(q)(1) proceeding is pending [P11]. No
  attribute exists.

### 3.3 What the Chapter 7 model already covers for Chapter 13

- **The whole petition-day schedule set.** B101 with `case.chapter = 13`,
  106A/B–J, 107, 121, 2030 and 2010 [R1].
- **Eligibility under § 109(e)'s debt limits.** These are $526,700 noncontingent
  liquidated unsecured and $1,580,125 secured, effective 2025-04-01 [P21]. The
  test is derivable from `claim.amount`, the `contingent`/`unliquidated` flags
  and `class`, plus a constant set. There is no new storage.
- **The means test.** The 122C pair reads the same `pay_period_record`,
  `other_income_record` and `means_test_input` the 122A pair does [R1].
- **The plan proposal and calculator.** Term, step payments, lump sums, the
  trustee percentage, attorney fees, secured treatments (cure-and-maintain,
  cramdown, surrender), the priority class, the unsecured pot or percentage,
  and the § 1325(a)(4) liquidation side [R1]. B113 §§ 2.1, 2.4, 2.5, 3.1, 3.2,
  3.5, 4.2–4.4, 5.1 and 6.1 (assume) and Exhibit A print from it [R2].
- **The supplement header.** `case.ch13_supplement_date` (106I/106J filed as a
  post-petition supplement) [R1].

---

## 4. Chapter 11 (individual, including small business and Subchapter V)

### 4.1 Forms, and when each is required

An individual files under Chapter 11 as **B101 + the B106 set**. The B2xx
"non-individual" series (201, 201A, 202, 204, 206, 207) is not this debtor's,
even when they run a sole proprietorship. The forms page groups those forms
under non-individuals, and B104 says it is the individual's list [P1], [P2].
The 2xx forms come in only if the debtor's LLC or corporation files its own
affiliated case: a separate case, related through B101 line 10.

B101 line 13 selects one of three paths [P10]:

| Form | Every individual Ch. 11 | Small business, not Subchapter V | Subchapter V | Source |
|---|---|---|---|---|
| 101 · 106Sum/Dec · 106A/B–J · 107 · 121 · 2030 · 2010 | ✓ with the petition / 14 days | ✓ | ✓ | [P11], [R1] |
| **104**: the 20 largest unsecured creditors who are not insiders | ✓ **with the petition** (voluntary case, Rule 1007(d)) | ✓ | ✓ | [P2], [P11] |
| **122B**: Chapter 11 current monthly income | ✓ | ✓ | **✗**: Rule 1007(b)(5), and the form itself say "other than under Subchapter V" | [P3], [P11] |
| **§ 1116(1) documents**: the most recent balance sheet, statement of operations, cash-flow statement and federal tax return, or a sworn statement that none exist. Appended to the petition | — | ✓ | ✓ (§ 1187(a)) | [P10], [P15], [P16] |
| **§ 1188(c) report** on efforts toward a consensual plan, ≥ 14 days before the status conference (≤ 60 days after the order for relief) | — | — | ✓ | [P17] |
| **Plan.** No official form outside small business. 425A is the one a small-business or Subchapter V plan may "substantially conform" to (Rule 3016(d)) | ✓. Exclusivity 120 / 180 days (§ 1121(b), (c)) | ✓ 425A. Exclusivity 180 days; plan due ≤ 300 days (§ 1121(e)) | ✓ 425A. **Only the debtor** files; due ≤ 90 days (§ 1189). It must carry a business history, a liquidation analysis and projections (§ 1190(1)) | [P4], [P14], [P18], [P23], [P24] |
| **Disclosure statement.** 425B in a small-business case | ✓ (§ 1125, Rule 3016(b)) | ✓ 425B, or a plan that itself gives adequate information (Rule 3016(b)(2)) | ✗ unless the court orders § 1125 to apply (§ 1181(b), § 1187(c)) | [P5], [P14], [P16] |
| **425C**: Monthly Operating Report, filed ≤ 21 days after the end of the following month, until the plan's effective date | — | ✓ (Rule 2015(a)(6), § 308) | ✓ (Rule 2015(b)) | [P6], [P12] |
| U.S. Trustee monthly operating report (UST Form 11-MOR) and the quarterly-fee statement | ✓. Rule 2015(a) requires the fee statement; the UST form is not an Official Form | (425C instead) | (425C; quarterly fees do not apply under Subchapter V) | [P12]; **[S]** UST form and § 1930(a)(6) not read |
| **426**: periodic report on entities the estate holds ≥ 20% of. Due ≥ 7 days before the first § 341 date, then every 6 months | ✓ when the estate holds such an interest (Rule 2015.3) | ✓ | ✓ | [P7], [P25] |
| Ballots (314); court orders 312, 313, 3130S, 3150S, 315; notices 309E1 / 309E2; discharges 3180RI, 3180RV1, 3180RV2 | the proponent sends ballots; the court issues the rest | | | [P1] |
| Debtor-education certificate | only when § 1141(d)(3) applies (Rule 1007(b)(7)) | | | [P11] |
| Rule 1007(b)(8) § 522(q) statement | as in Chapter 13 | | | [P11] |

### 4.2 Gaps: fields, entities and relationships the model has no place for

**A. The operating business, and the debtor in possession**

- **`sole_proprietorship` is three facts**: name, address, and the
  health-care / single-asset / broker type [R4]. Chapter 11 needs a business
  that **operates**:
  - line of business, and a NAICS code (425C header) [P6];
  - the year operations began, and its history (425A Background A; 425B II.A;
    § 1190(1)(A)) [P4], [P5], [P18];
  - employees at filing and now (425C lines 26–27) [P6];
  - insurance kept (§ 1116(5)) [P15];
  - the debtor-in-possession bank accounts (425C lines 5 and 10) [P6];
  - the responsible party who signs the reports [P6].
- **The debtor-in-possession role.** The model has a debtor, not a fiduciary.
  Subchapter V can remove the debtor in possession (§ 1182(2), § 1185)
  [S: § 1185 not read]. A Subchapter V trustee, a Chapter 11 trustee or an
  examiner is a party distinct from `case.trustee`'s one string.
- **Business-interest assets.** The A/B `asset.detail` "percentage ownership
  for entity interests" [R1] covers the 20% Rule 2015.3 test. Form 426 also
  needs each controlled entity's financial statements, operations,
  inter-entity claims, tax allocations and the payments it makes for the
  debtor (Exhibits A–E) [P7]. That is a reporting record per entity per
  period.

**B. Financial statements as inputs, not only documents**

- The § 1116(1) balance sheet, statement of operations, cash-flow statement
  and tax return [P15] fit `document`, but `document.kind` needs those values.
  "None exists" is a sworn statement, which needs an attribute or document of
  its own.
- 425B Exhibits B (material assets and their value), C (prepetition financial
  statements), F (cash on hand at the effective date) and G (post-confirmation
  cash-flow projections) [P5]. 425A's Subchapter V background also needs a
  **projected disposable income** figure for the § 1191(c)(2) 3–5-year period
  and the final payment date [P4], [P26]. § 1191(d) takes the business's
  operating expenditures out of disposable income [P26]. So the projection is a
  **period-by-period revenue and expense forecast**, entered rather than
  derived. Nothing in the model is forward-looking past the plan term.

**C. Insiders**

- B104 excludes insiders [P2]. 425B II.B lists each insider with their
  compensation for the 2 years before filing [P5]. § 1129(a)(10) ignores insider
  votes [P20].
- The model has insiders only inside B107's `sofa_entry` payloads (Q7, Q8)
  [R4]. There is no `creditor.is_insider` (or relationship), no insider
  compensation record, and no contact person or phone on a creditor (B104)
  [P2].
- B104's "nature of the claim" is free text that `claim`'s typed classes do not
  carry. The 20-largest ranking and each secured claim's unsecured portion are
  derived; the model already derives the latter [R1].

**D. The Chapter 11 plan: classes, impairment, votes, versions**

425A's articles [P4] and §§ 1122–1129 [P19], [P20] need:

```
ch11_plan {
  proponent                     // the debtor, or (outside Subchapter V, after
                                // exclusivity) any party in interest (§ 1121(c))
  dated                         // Rule 3016(a): every plan is dated, names its proponent
  versions: [ ... ]             // as for Chapter 13, plus the disclosure statement
                                // each was circulated with
  funding_sources               // Art. 1: capital, loan, sale, operations, future income
                                // (§ 1123(a)(8): an individual's plan commits earnings)
  classes: [ { number, kind: priority | secured | unsecured | convenience | equity,
               impaired: bool, treatment_text, members: [claim_id] } ]
  unclassified: admin_expense_terms, priority_tax_terms   // Art. 3; § 1123(a)(1)
  executory_contracts: assumed / assigned [contract_lease_id], rejection_claim_bar_days
  means_of_implementation       // Art. 7, § 1123(a)(5)
  governing_law_state, effective_date_rule, other_provisions
  discharge_alternative         // derived: debtor type × § 1191(a)/(b) × § 1141(d)(3)
}
ballot { plan_version_id, claim_id, class, accept | reject, amount }   // Form 314
```

- **Classes are the central new relationship.** A class groups `claim`s
  (§ 1122), carries impairment, and is the unit of voting. A class accepts
  with ≥ 2/3 in amount and > 1/2 in number (§ 1126(c)) [S: § 1126 not read].
  It is also the unit of the best-interests test (§ 1129(a)(7)) [P20].
- **The individual's "equity" class.** In an individual case it is "the
  interests of the individual Debtor in property of the estate" [P4]. The model
  has no such class.
- **The liquidation analysis is per class** (§ 1129(a)(7), 425B IV.C,
  Exhibit E) [P5], [P20]. The Chapter 13 calculator's single § 1325(a)(4)
  figure [R1] is the closest existing piece.
- **§ 1129(a)(15).** On an objection, the plan must distribute at least the
  individual's projected disposable income over 5 years or the plan term
  [P20]. That is a second disposable-income calculation, distinct from B122B's
  current-monthly-income figure and not the 122C-2 arithmetic.

**E. The monthly operating report: post-petition, periodic case data**

425C [P6] is one record per month:

- the questionnaire, lines 1–18 (yes / no / N-A, with Exhibit A/B
  explanations);
- opening cash; receipts, itemised (Exhibit C); disbursements, itemised with
  date, payee, purpose and amount (Exhibit D); net cash flow and closing cash;
- post-petition payables (Exhibit E: date incurred, payee, purpose, due date);
- receivables (Exhibit F: who owes, how much, when due);
- employees;
- bankruptcy-related and other professional fees, for the month and
  cumulatively;
- projected against actual, and next month's projection;
- attached bank statements and reconciliations.

Why this is new:

- **Every entity in the model today is a fact as of the petition.** A
  month-keyed series that has to reconcile with itself is not one: closing
  cash becomes the next opening (line 19), and projections roll forward
  (lines 32–37).
- Rule 2015(a)'s receipts-and-disbursements record [P12] is the same ledger,
  and so are the Chapter 13 business debtor's duties under Rule 2015(d).
- Totals, net flow and closing balance are derived. The line items and the
  questionnaire answers are stored.

**F. Lifecycle**

Chapter 11 adds:

- the § 1188 status conference (Subchapter V);
- disclosure-statement approval, which a small-business case can approve
  conditionally;
- the voting deadline, the confirmation hearing, confirmation under
  § 1191(a) or (b), and the plan's effective date;
- the end of the 425C duty at the effective date [P12];
- the deadlines of §§ 1121(e) and 1189 [P23], [P24].

The deadline engine counts from `filed_at`, which here is the order for relief
in a voluntary case, and from `meeting_341_at` [R1]. These are new anchors.

**G. Professionals**

- A Chapter 11 debtor's counsel is employed by court order and paid on fee
  application (Rule 2014, §§ 327 and 330) [S: not read]. `filing_professional`
  carries the § 329 disclosure only [R1].
- 425C lines 28–31 report the professional fees paid [P6].

### 4.3 What the Chapter 7 model already covers for Chapter 11

- **The petition, including the path selection.** B101 with
  `case.chapter = 11`. `petition.small_business_status` carries line 13's four
  answers, including the Subchapter V election. `debt_character` covers
  primarily business debts [R4].
- **The schedules and the SOFA.** These are the same forms whatever the chapter,
  including B107's business sections: Q4 business income, Q27 business
  connections with EIN, nature, accountant and dates, and Q28 financial
  statements given [R4].
- **B122B.** Lines 2–11 are a subset of B122A-1/B122C-1's current-monthly-income
  lines: wages, alimony, household contributions, business and rental net,
  interest, unemployment with the Social Security contention, pensions, and
  other income [P3]. `pay_period_record` and `other_income_record` already hold
  them [R1]. **No new storage.** There is a flat-form overlay to build (§6).
- **B104's creditor data**, except the insider flag, the contact and the nature
  text. Name, address, amounts, the three status flags and the lien and
  collateral value are `creditor` + `claim` + `asset` [R1], [P2].
- **B2030, B121 and B2010**, unchanged.
- **Contracts and leases** to assume or reject: `contract_lease` [R1], [R2].

---

## 5. Chapter 12, in one line

Family farmers and fishermen only (§ 109(f)) [P21]. The plan is due under
§ 1221 with no official plan form (Rule 3015(a)) [P13]. The debtor in
possession has Rule 2015(c)'s duties [P12]. So it is Chapter 13's shape (plan,
standing trustee, payments) plus Chapter 11's operating-business records, and
nothing here should assume only 7, 11 and 13 exist (`case.chapter` already
allows 12 [R1]).

---

## 6. Found in passing

- **Spec drift: the case number.** `forms/specs/b113.json`, `b122c1.json` and
  `b122c2.json` map the caption case number `unmapped`, saying "the case data
  model has no attribute for it yet" [R2]. The model now has
  `case.case_number`, written by the notice of filing or an electronic filing
  [R1]. That is a curation fix to those specs. It changes nothing in
  `acroform/`.
- **Form 423 is abrogated.** Since 2024-12-01 the provider's certificate is
  filed, unless the provider notified the court [P9], [P11]. Anything that
  plans to generate 423 should not.
- **B122B is a flat PDF.** The 12/21 file on its uscourts.gov page has no
  AcroForm [P3]. If specced, it takes overlay boxes like B113 and the Director's
  Forms (`forms/README.md` "Flat forms") [R3]. So do B2830, 425A, 425B and 426.
  Of the new Chapter 11 forms, only 104 and 425C are fillable.
- **Form 425A is a word-processing template, not a form to fill.** Its
  bracketed instructions are meant to be replaced with prose [P4]. A Chapter 11
  plan "packet" is therefore a generated document that *substantially conforms*
  (Rule 3016(d)) [P14]. It is not a widget fill, and the two-layer scheme in
  `forms/` does not describe it. The same holds for 425B.
- **The 2025 dollar adjustments reach these chapters' eligibility.** The small
  business debtor limit is $3,424,000 (§ 101(51D), as adjusted 2025-04-01)
  [P22]. The Chapter 13 limits are in §3.3 [P21]. B2830 prints the $214,000
  § 522(q) figure [P8]. These belong in the effective-dated constant sets the
  model already has [R1]. The CARES-era $7.5M Subchapter V limit has lapsed
  [S: inferred from the 2025 figure, not read].

## 7. Not checked — open threads

- U.S. Trustee Form 11-MOR (the non-small-business operating report) and its
  part for individual debtors; whether § 1930(a)(6) quarterly fees exempt
  Subchapter V.
- §§ 1126(c), 1185, 1194 (payments through the Subchapter V trustee), 1326,
  1328 and Rule 2014. Each is cited above from general knowledge and marked [S].
- The effective date and debtor-side duties of the Rule 3002.1 / 410C13 set.
- Which launch districts take a local Chapter 13 plan form (the court
  registry's `chapter_13_plan` fact owns this).
- How 425C's single checkbox per questionnaire line encodes Yes / No / N/A.

## Sources

**Primary, read**: the form or rule text was downloaded and parsed, or the
statute page was read. **Read via a summarising fetch**: a model summarised
the statute or rule page, so quotations there are near-verbatim, not
guaranteed exact. **[S]**: secondary, or primary but not opened.

- **[P1]** uscourts.gov, *Bankruptcy Forms* (the full list) — primary, read.
  https://www.uscourts.gov/forms-rules/forms/bankruptcy-forms
- **[P2]** Official Form 104 (12/15), `form_b104.pdf` — primary; AcroForm and
  text read. https://www.uscourts.gov/sites/default/files/form_b104.pdf
- **[P3]** Official Form 122B (12/21), `form_b122b.pdf` — primary; text read;
  no AcroForm. https://www.uscourts.gov/sites/default/files/form_b122b.pdf
- **[P4]** Official Form 425A (02/20) — primary; text read.
  https://www.uscourts.gov/sites/default/files/b_425a_0.pdf
- **[P5]** Official Form 425B (12/17) — primary; table of contents and
  structure read. https://www.uscourts.gov/sites/default/files/b_425b_1217_0.pdf
- **[P6]** Official Form 425C (12/17) — primary; AcroForm (90 widgets) and text
  read. https://www.uscourts.gov/sites/default/files/b_425c_1217.pdf
- **[P7]** Official Form 426 (12/17) — primary; pages 1–2 read.
  https://www.uscourts.gov/sites/default/files/b_426_1217_0.pdf
- **[P8]** B2830 (04/25) — primary; text read.
  https://www.uscourts.gov/sites/default/files/2025-04/form-2830_0425-form.pdf
- **[P9]** Official Form 423, abrogation notice (2024-12-01) — primary, read.
  https://www.uscourts.gov/sites/default/files/2024-12/b-423-abrogated.pdf
- **[P10]** Official Form 101 (06/24), fillable PDF — primary; line 13 text
  read. https://www.uscourts.gov/sites/default/files/form_b_101_0624_fillable_clean.pdf
- **[P11]** Fed. R. Bankr. P. 1007 (restyled 2024) — primary, read via a
  summarising fetch with verbatim quotation requested.
  https://www.law.cornell.edu/rules/frbp/rule_1007
- **[P12]** Fed. R. Bankr. P. 2015 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/rules/frbp/rule_2015
- **[P13]** Fed. R. Bankr. P. 3015 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/rules/frbp/rule_3015
- **[P14]** Fed. R. Bankr. P. 3016 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/rules/frbp/rule_3016
- **[P15]** 11 U.S.C. § 1116 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/uscode/text/11/1116
- **[P16]** 11 U.S.C. § 1187 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/uscode/text/11/1187
- **[P17]** 11 U.S.C. § 1188 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/uscode/text/11/1188
- **[P18]** 11 U.S.C. § 1190 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/uscode/text/11/1190
- **[P19]** 11 U.S.C. § 1123 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/uscode/text/11/1123
- **[P20]** 11 U.S.C. § 1129 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/uscode/text/11/1129
- **[P21]** 11 U.S.C. § 109, with the amendment notes for the 2025-04-01
  adjustment (90 F.R. 8941) — primary, read via a summarising fetch.
  https://www.law.cornell.edu/uscode/text/11/109
- **[P22]** 11 U.S.C. § 101, amendment notes for paragraph (51D) — primary,
  read via a summarising fetch. The definition text itself was not reached.
  https://www.law.cornell.edu/uscode/text/11/101
- **[P23]** 11 U.S.C. § 1121 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/uscode/text/11/1121
- **[P24]** 11 U.S.C. § 1189 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/uscode/text/11/1189
- **[P25]** Fed. R. Bankr. P. 2015.3 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/rules/frbp/rule_2015.3
- **[P26]** 11 U.S.C. § 1191 — primary, read via a summarising fetch.
  https://www.law.cornell.edu/uscode/text/11/1191
- **[R1]** [`docs/reference/case-data-model.md`](../reference/case-data-model.md)
  — the current case schema (`plan`, `means_test_input`, `case`, `petition`,
  lifecycle, amendments, "Not here, on purpose").
- **[R2]** `forms/specs/b113.json`, `b122c1.json`, `b122c2.json` — their
  `unmapped` and always-`None` `derived` mappings.
- **[R3]** [`forms/README.md`](../../forms/README.md) — flat forms, B113's
  printed-row limit.
- **[R4]** `forms/specs/b101.json` (lines 12, 13, 16), `b107.json` (Q4, Q7, Q8,
  Q27, Q28).
