# Conversion paperwork — 2026-10-25

> **Research for [#497](https://github.com/insolvia-ai/insolvia/issues/497)**,
> *"What must Insolvia prepare when a case converts?"*.
> [ADR 0027](../adr/0027-a-cases-chapter-is-history.md) records a
> **conversion** as a dated event on the same case and leaves open the
> paperwork it triggers. For each direction Insolvia handles (7 → 13, 13 → 7,
> and 7 or 13 → 11, which is recorded but not prepared), this note answers:
> what the debtor's attorney files and by when; which deadlines reset and what
> each counts from; which of it the case model can produce; and whether ADR
> 0027's working understanding of the court's side holds.

## 1. Summary

- **A conversion is not a new case.** It "constitutes an order for relief
  under the chapter to which the case is converted" but does not change the
  date of the petition, the commencement of the case or the order for relief,
  except where § 348(b) and (c) say so [P3]. ADR 0027's central decision
  stands.
- **Rule 1019 covers conversion *to* Chapter 7 only** [P1]. Nothing in it
  applies to 7 → 13 or to → 11. The general "documents already filed count"
  rule for every direction is **Rule 1007(c)(6)** [P2].
- **The 2024 restyling renumbered Rule 1019.** Old 1019(1)(A) is now
  1019(a)(1); old 1019(1)(B) (statement of intention) is 1019(a)(2); old
  1019(2) (new time) is 1019(b); old 1019(5) (the post-petition debts
  schedule) is **1019(e)** [P1]. ADR 0027 and #497 cite the old numbers.
- **No rule sets a time for a new means-test statement on conversion.**
  Restyled Rule 1007(c) has none; (c)(1) counts from the petition, and the
  1019(a)(1) fallback reaches only the (b)(1) documents, not the (b)(4)
  Chapter 7 statement [P2], [P1]. 13 → 7 needs Form 122A-1 and 7 → 13 needs
  122C-1 (and 122C-2 if above median) by Rule 1007(b)(4) and (b)(6), but the
  time is local practice or a court order [S1].
- **Only the debtor's 13 → 7 (§ 1307(a)) and 12 → 7 (§ 1208(a)) conversions
  happen by notice.** The notice date "becomes the date of the conversion
  order" for § 348(c) and Rule 1019 [P4]. **A debtor's 7 → 13 under § 706(a)
  needs a motion and an order** (Rule 1017(f)(2)), so its effective date is
  the order's [P4]. ADR 0027's "for a debtor's conversion as of right the
  date the notice was filed" is right only for § 1307(a) and § 1208(a).
- **The deadline engine needs two new anchors, not one**: the conversion date
  *and* the first date set for the **post-conversion** § 341 meeting. Most of
  the Chapter 7 resets (Rules 1017(e)(2), 4004(a), 4007(c), 4008(a),
  1007(c)(4)) count from that new meeting date, not from the conversion
  [P1], [P5], [P6], [P7]. The case's single `meeting_341_at` cannot hold the
  pre- and post-conversion meetings at once, and Rule 1019(b)(2) needs the
  old one (§5).
- **New records the model lacks**: a post-petition debt (holder, address,
  amount, date incurred), the debts incurred after confirmation, property
  acquired after the petition, and contracts entered into or assumed after
  the petition (Rule 1019(e)(2)–(3)) [P1]. Everything else a conversion asks
  for — Form 108, 122A-1/2, 122C-1/2, B113, B2030, amended schedules — the
  model already produces [R1].
- **There is no official or Director's form for a conversion.** The
  uscourts.gov forms list has no notice of conversion and no post-petition
  debts schedule; B2000 lists nothing for converted cases [P10], [P11].
  Each of these is a generated pleading, like the amendment cover sheet.

**This is a snapshot, not an owner of any fact, and it is not legal advice.**
Each claim is marked by source (see [Sources](#sources)). **[P]** means a
primary source was read this session: a rule, a statute, a court fee
schedule, a form or a repo file. **[S]** means secondary, inferred, or
primary but not read this session.

---

## 2. What the attorney files, and by when

### 2.1 13 → 7

| What | When | Source |
|---|---|---|
| Notice of conversion (debtor's right, § 1307(a)); no court order needed | any time; effective on filing | § 1307(a) [P5]; Rule 1017(f)(3) [P4] |
| Fee: $15 trustee surcharge plus $10 | with the notice | Misc. fee schedule items 9, 10 [P12] |
| Schedule of **unpaid debts incurred after the petition but before conversion**, with each holder's name and address | 14 days after conversion | Rule 1019(e)(2)(A) [P1] |
| If a plan was confirmed: schedules of (A) property acquired after the petition and not in the trustee's final report (unless § 348(f)(2) does not apply, i.e. a good-faith conversion), (B) debts incurred after confirmation, (C) contracts and leases entered into or assumed after the petition | no time stated; "unless the court orders otherwise" | Rule 1019(e)(3) [P1] |
| Statement of intention (Form 108), if required | 30 days after the conversion order, or before the first date set for the meeting, whichever is earlier | Rule 1019(a)(2) [P1] |
| Lists, schedules, SOFA not already filed | 14 days after conversion (as if an involuntary order for relief on the conversion date) | Rule 1019(a)(1) → 1007(c)(2) [P1], [P2] |
| Form 122A-1 (and 122A-2 if above median) | **no rule sets a time**; local rule or order | Rule 1007(b)(4) [P2]; timing [S1] |
| Supplemental B2030, if a new fee was agreed for the conversion | 14 days after the payment or agreement | Rule 2016(b)(2) [P13] |
| Financial-management certificate | 60 days after the first date set for the (post-conversion) § 341 meeting | Rule 1007(c)(4)(A) [P2]; that it is the new meeting [S] |

The Chapter 13 trustee, not the debtor, files the final report and account
within 30 days (Rule 1019(e)(2)(B)) [P1]. The estate in a good-faith 13 → 7
is the petition-date property still in the debtor's hands on conversion
(§ 348(f)(1)(A)); in a bad-faith conversion it is the property as of the
conversion (§ 348(f)(2)) [P3]. In practice that is usually reported by
amending Schedules A/B and C [S].

### 2.2 7 → 13

| What | When | Source |
|---|---|---|
| Motion to convert (§ 706(a)); the court orders it | any time, if not already converted from 11, 12 or 13 | § 706(a) [P6]; Rule 1017(f)(2) [P4] |
| Chapter 13 plan (Form 113 or the local form) | **14 days after conversion** | Rule 3015(b)(2) [P8] |
| Form 122C-1 (and 122C-2 if above median) | **no rule sets a time**; local rule or order | Rule 1007(b)(6) [P2]; timing [S1] |
| Supplemental B2030 | 14 days after the payment or agreement | Rule 2016(b)(2) [P13] |
| First plan payment | 30 days after the plan is filed or the order for relief, whichever is earlier; § 348(b) does not list § 1326, so which date the conversion makes the "order for relief" is unsettled here | § 1326(a)(1) [P9]; § 348(b) [P3]; reading [S] |
| B2830 and the financial-management certificate | by the last plan payment | Rule 1007(c)(4)(B), (c)(5) [P2] |

No conversion fee is listed for 7 → 13 [P12]. The schedule says no refund is
given when the new chapter's fee is lower [P12].

### 2.3 7 or 13 → 11 (recorded, not prepared)

- **How**: a debtor's 7 → 11 is by motion under § 706(a) (Rule 1017(f)(2))
  [P6], [P4]. 13 → 11 is on a party's or the U.S. trustee's request, after a
  hearing, only before confirmation (§ 1307(d)) [P5].
- **Fee**: the difference between the Chapter 11 fee and the Chapter 7 or 13
  fee, on the debtor's request (28 U.S.C. § 1930(a)) [P14]. U.S. trustee
  quarterly fees then run until conversion or dismissal (§ 1930(a)(6))
  [P14].
- **Forms**: Form 104 is due "with the petition" and Form 122B by Rule
  1007(c)(1), which counts from the petition; **no rule sets their time on
  conversion** [P2]. Rule 1020's small-business designation is stated "in the
  petition" or, involuntarily, 14 days after the order for relief; it says
  nothing of a converted case [P15].
- **What counts from the conversion**: § 348(b) reads "the order for relief"
  as the conversion in §§ 1102(a), 1121(b) and (c) and 1141(d)(4), among
  others [P3]. So the committee and the exclusivity periods start at
  conversion.
- An objection to discharge is due by the first date set for the
  confirmation hearing (Rule 4004(a)(2)) [P7].

**§ 1208, in one line**: a Chapter 12 debtor may convert to Chapter 7 by
notice at any time, exactly like § 1307(a), and Rule 1017(f)(3) covers both
[P16], [P4].

## 3. Which deadlines reset, and what each counts from

"C" is the conversion date (`effectiveOn`). "M′" is the first date set for
the post-conversion § 341 meeting. "P" is the petition date.

### 3.1 Conversion to Chapter 7 (13 → 7)

Rule 1019(b)(1) starts a **new time** under Rules 1017, 3002, 4004 and 4007
[P1].

| Deadline | Counts from | Source |
|---|---|---|
| § 341 meeting, 21–40 days | after the order for relief; § 348(a) makes the conversion one | Rule 2003(a)(1)(A) [P17]; § 341(a) [P18]; § 348(a) [P3]; that the window counts from C [S] |
| § 707(b)/(c) motion, 60 days | **M′** | Rules 1019(b)(1)(A), 1017(e)(2) [P1], [P4] |
| Proof of claim, 70 days | **C** (new time under 3002; the 2017 note says it "commences") | Rule 1019(b)(1)(B), 3002(c) and its committee note [P1], [P19] |
| Governmental claim, 180 days | P (the "order for relief" date, which § 348(a) leaves unchanged); unsettled | Rule 3002(c)(1) [P19]; reading [S] |
| Objection to discharge, 60 days | **M′** | Rules 1019(b)(1)(C), 4004(a)(1) [P1], [P7] |
| Dischargeability complaint, 60 days | **M′** | Rules 1019(b)(1)(D), 4007(c) [P1], [P20] |
| Objection to exemptions, 30 days | the conclusion of the new meeting (not recorded today); **no new time** if more than 1 year has passed since the first confirmation order, or the case was in Chapter 7 before and that time expired | Rules 1019(b)(3), 4003(b)(1) [P1], [P21] |
| Statement of intention | earlier of C + 30 and M′ | Rule 1019(a)(2) [P1] |
| Post-petition debts schedule | C + 14 | Rule 1019(e)(2)(A) [P1] |
| Reaffirmation agreement, 60 days | M′ | Rule 4008(a) [P22]; that it resets [S] |
| Financial-management certificate, 60 days | M′ | Rule 1007(c)(4)(A) [P2]; that it resets [S] |
| Pre-conversion administrative expense (creditor side) | before C, or a time the court sets; a governmental unit has the later of C + 180 and that time | Rule 1019(f)(1) [P1] |
| **Reconversion** to 7 after a 7 → 13 | **no new time** where the original Chapter 7 time has expired | Rule 1019(b)(2) [P1] |

A proof of claim filed before conversion counts in the Chapter 7 case
(Rule 1019(c)) [P1]. § 348(d) treats a claim arising after the petition and
before a § 1307 conversion (other than an administrative expense) as if it
arose just before the petition [P3]; Rule 1019(f)(2)–(3) has the clerk notice
those creditors of their claim time [P1].

### 3.2 Conversion to Chapter 13 (7 → 13)

| Deadline | Counts from | Source |
|---|---|---|
| Plan, 14 days | **C** | Rule 3015(b)(2) [P8] |
| Proof of claim, 70 days | **C** ("entry of an order converting the case to Chapter 12 or 13") | Rule 3002(c) [P19] |
| § 341 meeting, 21–50 days | after the order for relief | Rule 2003(a)(1)(C) [P17]; that it counts from C [S] |
| § 1328(f) discharge objection, 60 days | the first date set for the § 341 meeting | Rule 4004(a)(3) [P7]; whether M′ or the Chapter 7 meeting: **not stated** [S] |
| Dischargeability complaint, 60 days | the first date set for the § 341 meeting | Rule 4007(c) [P20]; same open question [S] |
| Codebtor stay, § 1305 | C | § 348(b) [P3] |
| First plan payment | open (§2.2) | § 1326(a)(1) [P9] |

Rule 1019(b)'s "new time" does not reach a conversion to Chapter 13, so for
4004 and 4007 the rules do not say which meeting counts. That is an open
question to put to a practitioner, not one to encode.

### 3.3 What this means for the anchors

- **Some reset deadlines count from C**: the plan, the proof of claim, the
  statement of intention, the post-petition debts schedule.
- **Most Chapter 7 resets count from M′**, a date the court sets *after* the
  conversion and notices later (on a Form 309A or 309B) [P10].
- **Some stay on P**: the petition date never moves (§ 348(a)) [P3], and
  Rule 1007(c)(1)'s 14 days, Rule 3002(c)(1)'s governmental 180 days (on our
  reading) and the means-test income period stay where they were.
- **Some depend on history**: Rule 1019(b)(2) needs whether the case was in
  Chapter 7 before and whether its time expired; Rule 1019(b)(3)(A) needs
  the first confirmation's date.

## 4. What Insolvia's records can produce

| Paperwork | Produced by | New record? |
|---|---|---|
| Statement of intention (Form 108) | `claim.intention`, `contract_lease` → `forms/specs/b108.json` [R1], [R2] | no |
| 122A-1/122A-2 on 13 → 7; 122C-1/122C-2 on 7 → 13 | `pay_period_record`, `other_income_record`, `means_test_input` → the 122 specs [R1], [R2]. Current monthly income is the six months before **the commencement of the case** (§ 101(10A)(A)(i)) [P23], which § 348(a) leaves unchanged [P3], so the same income records serve | no |
| Chapter 13 plan (B113) on 7 → 13 | `plan` → `forms/specs/b113.json` [R1], [R2], with the gaps the Chapter 13 research lists [R3] | no (beyond #490) |
| Supplemental B2030 | `filing_professional`'s compensation facts → `b2030.json` [R1], [R2] | no; but one disclosure per engagement today, not a supplement |
| Amended schedules (§ 348(f) estate) | the per-item `amended` flag and the amended-only packet [R1] | no |
| **Schedule of unpaid post-petition debts** (Rule 1019(e)(2)(A)) | the holder's name and address come from `creditor` | **yes**: a `postpetition_debt` (creditor, amount, date incurred, nature). Not a `claim`: a claim feeds 106D/E/F and the creditor matrix as a pre-petition debt [R1] |
| Debts incurred after confirmation (1019(e)(3)(B)) | `postpetition_debt.date_incurred` against the confirmation event (ADR 0027) | derived from the above |
| Property acquired after the petition (1019(e)(3)(A)) | `asset` has no acquisition date [R1] | **yes**: an acquired-on date, or a post-petition asset kind. The same record would serve Rule 1007(h)'s supplemental schedule [P2] |
| Contracts entered into or assumed after the petition (1019(e)(3)(C)) | `contract_lease` has no such date [R1] | **yes**: a date entered or assumed |
| Notice of conversion (§ 1307(a)) | the case caption and the debtor; no official form [P10] | a generated pleading; no new data |

None of these four new documents is an official form [P10], [P11]. Each is
a generated document, the way the amendment cover sheet is [R1].

## 5. ADR 0027's Context, checked

| Working understanding | Verdict |
|---|---|
| A converted case keeps its number and petition date (§ 348(a)) | **Confirmed for the petition date** [P3]. § 348(a) does not mention the case number; that it stays is court practice (one docket) [S]. |
| Schedules filed before conversion count as filed in the new chapter (Rule 1019(1)(A)) | **Correct in substance, wrong citation.** For conversion to 7 it is **Rule 1019(a)(1)**; for every direction it is **Rule 1007(c)(6)** [P1], [P2]. Both say "unless the court orders otherwise". |
| A debtor's 13 → 7 under § 1307(a) takes effect when the notice is filed (Rule 1017(f)(3)) | **Confirmed** [P4]. But a debtor's 7 → 13 under § 706(a) is by motion and order (Rule 1017(f)(2)), so it is effective on the order. |
| Conversion resets some deadlines (Rule 1019) | **Confirmed only for conversion to 7** (Rule 1019(b)) [P1]. A conversion to 13 resets the plan and claim times through Rules 3015(b)(2) and 3002(c) instead [P8], [P19]. |
| (Decision) The paperwork includes "a new means-test statement" under Rule 1007(c) | **Correct the citation.** The duty is Rule 1007(b)(4)/(b)(6); Rule 1007(c) sets **no** time for it after conversion [P2]. |

### What bears on ADR 0027's decisions

- **Nothing makes a conversion a new case.** § 348(a) is explicit [P3].
  The decision stands.
- **The anchor is short of what the rules need.** The ADR says the engine
  gains "a second anchor (the conversion date)". It needs a third: the first
  date set for the post-conversion § 341 meeting. And because Rule 1019(b)(2)
  needs the old meeting's deadlines, the case cannot just overwrite
  `meeting_341_at`. The meeting date belongs to a chapter period: either a
  dated event in the lifecycle history (a "meeting set" kind) or a value
  recorded against the conversion. The event model can hold it; the ADR's
  current event kinds do not.
- **`effectiveOn` needs the right default per path.** It is the notice date
  for §§ 1307(a) and 1208(a), and the order date for everything else,
  § 706(a) included [P4].
- **Insolvia may file the conversion itself.** A § 1307(a) notice is a
  filing Insolvia could prepare and e-file. Its conversion event would then
  be Insolvia's own act with a court receipt, like the filing worker's
  `filed`, rather than a court notice someone accepts. ADR 0027's "who writes
  an event" may want that path.
- **Pre-filing chapter edits are unaffected.** None of this applies before
  the petition is filed.

## 6. Not checked — open threads

- Whether § 707(b)'s means test applies at all after a 13 → 7 conversion.
  Courts are split [S1]. The rules require Form 122A-1 regardless.
- The time each launch district's local rules set for 122A-1 or 122C-1
  after conversion.
- Which § 341 meeting Rules 4004(a)(3) and 4007(c) count from after 7 → 13.
- Whether the 2003(a) windows and § 1326(a)(1) count from the conversion,
  given § 348(a)'s "does not effect a change in the date of … the order for
  relief".
- Whether a conversion to 11 needs Form 104 and 122B on a set schedule, or
  only by court order.

## Sources

**[P]** primary, read this session: the rule or statute page was fetched and
its text extracted (not a summary); forms and the fee schedule were fetched
from uscourts.gov. **[R]** a repo file read this session. **[S]** secondary,
inferred, or not opened.

- **[P1]** Fed. R. Bankr. P. 1019 (restyled 2024) — https://www.law.cornell.edu/rules/frbp/rule_1019
- **[P2]** Fed. R. Bankr. P. 1007 — https://www.law.cornell.edu/rules/frbp/rule_1007
- **[P3]** 11 U.S.C. § 348 — https://www.law.cornell.edu/uscode/text/11/348
- **[P4]** Fed. R. Bankr. P. 1017 — https://www.law.cornell.edu/rules/frbp/rule_1017
- **[P5]** 11 U.S.C. § 1307 — https://www.law.cornell.edu/uscode/text/11/1307
- **[P6]** 11 U.S.C. § 706 — https://www.law.cornell.edu/uscode/text/11/706
- **[P7]** Fed. R. Bankr. P. 4004 — https://www.law.cornell.edu/rules/frbp/rule_4004
- **[P8]** Fed. R. Bankr. P. 3015 — https://www.law.cornell.edu/rules/frbp/rule_3015
- **[P9]** 11 U.S.C. § 1326 — https://www.law.cornell.edu/uscode/text/11/1326
- **[P10]** uscourts.gov, *Bankruptcy Forms* (full list, including the 309 notices and Director's Forms B 2000–B 2830) — https://www.uscourts.gov/forms-rules/forms/bankruptcy-forms
- **[P11]** Director's Form B2000 (12/25), *Required Lists, Schedules, Statements, and Fees* — https://www.uscourts.gov/sites/default/files/2025-04/form-2000_0425-form.pdf
- **[P12]** Bankruptcy Court Miscellaneous Fee Schedule, items 9–10 — https://www.uscourts.gov/court-programs/fees/bankruptcy-court-miscellaneous-fee-schedule
- **[P13]** Fed. R. Bankr. P. 2016 — https://www.law.cornell.edu/rules/frbp/rule_2016
- **[P14]** 28 U.S.C. § 1930 — https://www.law.cornell.edu/uscode/text/28/1930
- **[P15]** Fed. R. Bankr. P. 1020 — https://www.law.cornell.edu/rules/frbp/rule_1020
- **[P16]** 11 U.S.C. § 1208 — https://www.law.cornell.edu/uscode/text/11/1208
- **[P17]** Fed. R. Bankr. P. 2003 — https://www.law.cornell.edu/rules/frbp/rule_2003
- **[P18]** 11 U.S.C. § 341 — https://www.law.cornell.edu/uscode/text/11/341
- **[P19]** Fed. R. Bankr. P. 3002, with its 2017 committee note — https://www.law.cornell.edu/rules/frbp/rule_3002
- **[P20]** Fed. R. Bankr. P. 4007 — https://www.law.cornell.edu/rules/frbp/rule_4007
- **[P21]** Fed. R. Bankr. P. 4003 — https://www.law.cornell.edu/rules/frbp/rule_4003
- **[P22]** Fed. R. Bankr. P. 4008 — https://www.law.cornell.edu/rules/frbp/rule_4008
- **[P23]** 11 U.S.C. § 101(10A) — https://www.law.cornell.edu/uscode/text/11/101
- **[R1]** [`docs/reference/case-data-model.md`](../reference/case-data-model.md) — core entities, claims, assets, amendments, `filing_professional`.
- **[R2]** `forms/specs/` — b108, b113, b122a1, b122a2, b122c1, b122c2, b2030 exist; no spec mentions conversion.
- **[R3]** [`chapter-11-13-form-gaps-2026-10-21.md`](chapter-11-13-form-gaps-2026-10-21.md).
- **[R4]** `services/api/src/insolvia_api/core/deadlines.py` and `regulatory/code/deadline-rules/2024-12-01/rules.json` — today's anchors are `petition` and `meeting_341`, and rules filter on `case.chapter`.
- **[S1]** General knowledge of practice, not verified this session: local rules setting means-test times after conversion; the split on whether § 707(b) applies after 13 → 7.
