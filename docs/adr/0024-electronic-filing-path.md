# ADR 0024 — The filing path is prepare-and-hand-off, with the packet shaped for the courts' own upload facilities

- **Status:** Proposed
- **Date:** 2026-09-23
- **Relates to:** issue #368 (17.1, the spike this records); specifies the
  court registry for #360 (14.8); feeds #369 (court-notice intake) and #370
  (amendments). Rides on [ADR 0001](0001-client-stays-dumb-trust-boundary.md)
  (no credential we do not control), [ADR 0014](0014-the-repository-is-the-regulatory-release-registry.md)
  (the court registry is a regulatory series), [ADR 0017](0017-launch-states-florida-texas-georgia.md)
  (the ten launch districts) and [ADR 0021](0021-test-tiers-and-seed-fixtures.md)
  (what each tier may touch). The packet today is
  `services/api/src/insolvia_api/core/packet_assembly.py` — nineteen forms
  plus the matrix from `core/creditor_matrix.py`, with 13.11's output
  options; `case.district` is free text by design
  (`packages/insolvia_core/src/insolvia_core/cases.py`).

## Decision

**Insolvia does not file. The attorney files, from their own CM/ECF session,
and Insolvia prepares everything that session will ask for** — the packet in
the district's docket order, the creditor matrix in the district's format, the
district's e-filing declaration and local forms, a per-district checklist,
and (once verified per court) the court's *Case Upload* package, so the
attorney's session opens the case from our data instead of re-keying it.
Option (a) from the spike, carrying option (c) where a launch district
enables it.

Rejected for launch: **(b)** a browser session under the attorney's
credentials driven from our infrastructure, and **(d)** a "certified vendor
integration", which does not exist as a program. In short: every court's rule
makes the login *the signature* and forbids anyone but an individual filing
agent from using it, and even the AO's own machine interface ends in a
logged-in browser session the attorney must finish.

**Filed** on a case therefore means the attorney has recorded the case number
and filing date from the court's confirmation screen; the court's notices,
not our submission, are the evidence of what was filed.

## What the research established

Every fact here is from a court, PACER, or the Administrative Office; URLs and
the date read are in the sources list.

**1. Filing is a browser session under an individual's account, in every
district.** NextGen authenticates through PACER's Central Sign-On; each filer
needs an individual PACER account (a shared one cannot be linked — TXWB
§III.A.2, GAMB §I.A) and a separate e-file registration approved by each
court [S1, S2]. Since 2025-12-31 every account with CM/ECF-level access must
be enrolled in multifactor authentication (TOTP) [S3]. PACER publishes no
filing API: its developer page offers an authentication API, a case-locator
API, and two things aimed at "bankruptcy petition software" — the legacy
**Case Upload** text file and the **XML Case Opening** IEPD [S4]. The IEPD's
own words: NextGen "includes web services that accept XML data but result in
a logged-in session for the lead event to be docketed manually", and "at the
end of the request you must submit the form in the returned HTML and finish
docketing" [S5]. The Authentication API guide documents scripted sign-in,
even generating the TOTP from the account's secret key — so automation is
*technically* possible — and requires every filer request to carry the
redaction-compliance flag a human acknowledges at each login [S6].

**2. The login is the signature, and only an individual "filing agent" may
use it.** FRBP 5005(a)(3): a filing "made through a person's electronic-filing
account and authorized by that person, together with the person's name on a
signature block, constitutes the person's signature" [S7]. The launch
districts add the sharing rule. TXWB: filers "are prohibited from sharing
their login information with any other person for any reason"; a Filing
Agent "must have an individual PACER account", and linking one in CM/ECF "is
the only approved method of allowing another party to file documents
utilizing the credentials of an Electronic Filer" (§II.A.3, §II.C.3) [S8].
The Texas statewide procedures, GAMB, GANB, FLMB Rule 1001-2(c) and GASB say
the same in their own words [S9–S13]. A filing agent is a person with a PACER
account, linked by the attorney, whose filings docket as the attorney's
[S8, S14]. A hosted service is not a person and cannot hold a PACER account;
holding the attorney's password and TOTP seed server-side is exactly the
credential ADR 0001 refuses. That closes option (b) whatever its feasibility.

**3. What is submitted.** PDF only, except the matrix, a `.txt` [S15, S16].
No launch court requires PDF/A today (FLNB says so explicitly [S15]);
text-searchable, word-processor-converted PDF is a rule in FLSB (9004-1(a))
and FLMB (1001-2(f)) [S17, S12]. Size limits are per court: FLNB 35 MB
[S15], GASB 35 MB [S18], TXWB 50 MB [S8], FLSB 50 MB for exhibits [S17],
while the 2017 Texas statewide text still prints 5 MB for TXNB/TXSB and
10 MB for TXEB [S9] — figures a registry carries with a verified-at date,
not a constant. Fees are paid through pay.gov inside the session, per filing
or per session, with lockouts for late payment (TXSB 48 h, GASB same day,
TXNB 24 h for software "quick filing" features) [S9, S13]. The debtor's
wet-ink signature is handled by a district instrument: Texas files a
*Declaration for Electronic Filing* (Exhibits B-1/B-2/B-3, wet ink, DocuSign
refused, its own docket event) [S8, S9]; FLMB a *Declaration Under Penalty
of Perjury for Electronic Filing*, originals kept two years [S12, S19]; FLSB
`/s/` with the wet-ink copy obtained within 14 days and kept five years
(5005-2) [S17]; GAMB and GASB a signed matrix certification [S20, S21]. The
SSN statement (B121) is its own event in FLMB and GASB and in TXWB is *not
filed* — the full SSN goes on the opening screen or in `debtor.txt` [S8,
S18, S19]. Local forms exist everywhere; FLSB's 9009-1 makes its own
mandatory "without material alteration" and 1007-1(b),(c) add two to any
schedule filing [S17].

**4. Case Upload is real, per-court, and still the attorney's act.** The AO
spec (effective March 2022) is a pipe-delimited `stat|debt|alas` file: 80
statistics fields (chapter, fee status, estimated ranges, the 106/122
totals, the presumption answer), one debtor record per debtor with the
*full* SSN and court-specific office and county codes, and alias records.
"The court CM/ECF configuration determines" which fields are required, and a
wrong field count is refused with "be sure you are using the correct Case
Upload specification for this court" [S22]. FLMB's guide walks the flow —
`Debtor.txt`, `Petition.pdf` in a prescribed order, `Creditor.txt`, the
separate declaration and SSN PDFs, deficiency screens, payment, then the
*Notice of Bankruptcy Case Filing* with the case number while "the Judge,
Trustee and 341 Meeting information will not be immediately available"
[S19]. TXEB's matrix appendix and TXWB's procedures assume it; GANB calls
it "optional" [S23, S8, S11]. Courts approve vendors one at a time by testing
with the ECF help desk; Maryland's list says "approval does not constitute an
endorsement", GASB's vendor list dates from 2003, New Jersey's from 2002
[S24–S26]. No national certification exists, which closes option (d).

**5. What comes back.** A Notice of Electronic Filing email at docketing
(one free look), the case number on the confirmation screen, then Official
Form 309A — §341 date, trustee, deadlines — served by the Bankruptcy
Noticing Center; EBN delivers an email linking a PDF whose catalog carries
XMP/XML case data (debtor, attorney, court, case number, chapter, 341
location) with a published extractor [S27–S29]. Registration is consent to
electronic notice (FLMB 1001-2(d); Texas §II.A.4) [S12, S9]. This is #369's
channel: the attorney's NEF and BNC mail, matched on case number.

**6. Test systems exist and are reachable by us.** TXSB, TXWB, FLMB and FLNB
run `ecf-train` databases; FLNB's self-paced training uses PACER's
*training* environment, open to "attorneys, paralegals, clerical staff, and
others who will be using the system" [S30–S33]. The IEPD names a PACER **QA**
environment run by the AO's Testing Services Division that issues vendors a
test attorney account "already registered" with NextGen, "including access
to the XML Case Opening" [S5]. That is the nearest thing to a vendor program
the Judiciary offers, and where our packages get proven.

## Weighing the options

| | (a) prepare & hand off | (b) hosted browser automation | (c) court upload facility | (d) certified vendor |
|---|---|---|---|---|
| Credentials held by | the attorney | **us** — password + TOTP seed | the attorney | — |
| The filer is | the attorney, in their session | ambiguous; forbidden by every district's sharing rule | the attorney; our file is input | — |
| Reliability | ours ends at a clean package | scraping ten court-modified UIs ("programming based on this information may not work in the same manner in every court" [S4]) | per-court field configuration; fails loudly on upload | — |
| Per-district cost | a registry record + checklist | ten UIs kept working, MFA per attorney | one verification session per court | — |
| Testable locally (ADR 0021) | unit: validators over the package | only against a real court | unit: spec validator; QA/train for the real thing | — |
| Time to first real filing | when registry and checklist exist | after per-court automation, and a rule review we would lose | after one training session per court | never — no program |

(c) is not an alternative to (a); it is the best shape for (a)'s output.

## Per-district scope at launch

All ten districts get a registry record and a hand-off checklist. Case
Upload is enabled per district only after a training-database session proves
our file against that court's configuration; until then the checklist says
"open the case on the court's screens from the packet".

| District (CM/ECF id) | Release [S34] | PDF cap | Matrix departure from `COMMON_FORMAT` | Signature instrument | Notes |
|---|---|---|---|---|---|
| N.D. Fla. (`flnb`) | 1.9 | 35 MB, no PDF/A | none | court declaration | training on PACER-train [S15, S31] |
| M.D. Fla. (`flmb`) | 1.8.3 | 1001-2(f); split rule | none | Declaration for E-Filing; originals kept 2 yrs | Case Upload documented; four divisions with own docketing notes [S12, S19] |
| S.D. Fla. (`flsb`) | 1.8.3 | 50 MB (exhibits) | none (CI-3) | `/s/` + wet ink within 14 d, kept 5 yrs | mandatory local forms (9009-1; 1007-1(b),(c)); registration by LF-95 acknowledgment form [S17, S35] |
| N.D. Tex. (`txnb`) | 1.9 | 5 MB in 2017 text — **verify** | two blank lines between | Declaration B-1/B-2, no paper copy kept | 24 h settle rule for software "quick filing" [S9] |
| S.D. Tex. (`txsb`) | 1.9 | 5 MB in 2017 text — **verify** | fixed six-line blocks | Declaration B-1/B-2 | training not required; 48 h fee lockout; `ecf-train` [S9, S30] |
| E.D. Tex. (`txeb`) | 1.9 | 10 MB in 2017 text — **verify** | two blank lines; case-number header when filed separately, not via Case Upload | Declaration B-1/B-2 | Appendix 1007-b-5 [S23] |
| W.D. Tex. (`txwb`) | 1.9 | 50 MB | 50-char name line, comma after city | Declaration B-1/B-2, own event; B121 not filed | eight training modules + one-on-one; `ecf-train` [S8, S32, S36] |
| N.D. Ga. (`ganb`) | 1.8.3 | not on pages read | none | per 2021 procedures | training videos before live access; Case Upload "optional" [S11, S37] |
| M.D. Ga. (`gamb`) | 1.8.3 | not on pages read | two blank lines; alphabetical | signed matrix certification (LBR 1007-2) | Clerk's Instructions Dec 2024 [S13, S20] |
| S.D. Ga. (`gasb`) | 1.9 | 35 MB | none | signed matrix certification | manual March 2026 documents the Open BK Case flow [S18, S21] |

Unreached: FLSB's rule page returned 403 (the 2026 rules PDF was read
instead); TXNB's procedure sections 404 under the URLs its index publishes;
the NIEM registry did not resolve (the IEPD was read from PACER's copy). GANB
and GAMB size limits were not found on the pages read.

## The court registry (#360) — what a district record needs

A series in the regulatory registry (ADR 0014): a committed, effective-dated
release the API ships, with `case.district` becoming a reference into it and
the free text migrated. Per district:

- **Identity:** `code` (CM/ECF id, `flsb`), PACER `court_id` (`FLSBK`), name,
  state, circuit.
- **Divisions:** the CM/ECF *office code* (the digit in `office-yy-bk-nnnnn`
  and Case Upload field 10), name, courthouse address, and the FIPS-5
  counties served (Case Upload field 17 and the IEPD's `USCountyCode` use the
  same codes; PACER's lookup lists counties per court, so the division mapping
  is hand-entered from each court's page).
- **CM/ECF:** live and training login URLs, help-desk contact, release
  version with an as-of date.
- **PDF rules:** size cap, text-searchable and flatten requirements, PDF/A
  (false everywhere today), each with source URL and `verified_at`.
- **Matrix:** the `MatrixFormat` values already in
  `creditor_matrix.DISTRICT_VARIANCES`, plus the two knobs this research
  added (name-line width; a case-number header when filed separately).
- **Opening:** the docket order for the petition package; the signature
  instrument (which declaration, wet-ink and retention rule); SSN statement
  handling (`own_event` / `not_filed`); local forms for a Chapter 7
  individual opening (id, title, URL, when required); the fee rule (deadline,
  whether Case Upload may be used with installments).
- **Case Upload:** `unverified` / `legacy_txt` / `xml`, the court-required
  field set once known, and `verified_at` from the training session.
- **Registration notes:** whether training is required, and the page that
  says so — for the checklist.
- **Sources:** every URL above with the date read.

Firm defaults (default district and chapter, attorneys' bar numbers and
signature blocks, letterhead) are #360's second half and unchanged by this.

## Build breakdown

| # | PR | Done when | Size |
|---|---|---|---|
| 1 | **Court registry series** — `courts/us-bankruptcy` release with the ten records, loader + CI validation (append-only, source-dated), `GET /v1/courts`, `case.district` → `{court, division}` with migration of the free text; seeds updated | a staging case picks district and division from the registry; the old text survives as an audit field | M |
| 2 | **Matrix keyed by court** — generation reads `DISTRICT_VARIANCES` through the registry; the two new knobs; TXEB's separate-file header; per-district goldens | the fixture case's matrix differs byte-for-byte between `txsb`, `txnb`, `txwb`, `txeb`, `gamb` and the common format, each pinned | S |
| 3 | **Filing set and checklist** — packet output in the district's docket order and file names; PDF checks (size split, text layer, page size); the district's declaration and B121 handling; a checklist screen built from the record | the packet screen shows the checklist and filing set for the case's district; unit tests over every record | M |
| 4 | **Firm defaults** (#360 second half) — default district/chapter, bar numbers, signature blocks, letterhead prefilling `filing_professional` | a new staging case's signer block arrives prefilled | M |
| 5 | **Training-database runbook** — register on PACER-train and the AO QA environment, obtain each district's `ecf-train` access, upload the fixture case's set, record the court's upload kind and required fields in the registry | executed once per launch district, each record's `verified_at` set; deliberately human-run, outside CI | S |
| 6 | **Case Upload package** — `debtor.txt` per the AO spec (statistics from the 106/122 projections, debtor/alias records, office and county codes from the registry), a spec validator under pytest, emitted with the packet where the record says `legacy_txt`. The file requires the full SSN; until tax-id storage exists the field is blank and the checklist says so | the fixture case's `debtor.txt` validates against the 80-field spec in unit tests and is accepted by at least one training database in PR 5 | M |
| 7 | **Filed-state capture** (feeds #369) — the attorney records court case number and filed date from the confirmation screen; `status=filed`, `court_case_number`, `filed_at`; the pins freeze | a staging case can be marked filed with a case number the notice matcher can key on | S |
| 8 | **XML case opening** (post-launch) — the IEPD package from the same projections, behind the same per-court verification | only if PR 5 shows a launch district exposes the XML menu | L |

**Risks.** Per-court configuration makes Case Upload a ten-times-verified
feature, not one build, and a court can turn it off. `debtor.txt` and the
XML both carry the full SSN, so PR 6's value is capped until field-level
encryption lands (the tax-id gap `docs/plan.md` records). The Texas
statewide procedures are dated 2017 and disagree with TXWB's 2025 document
on size limits — `verified_at` is the defence, not a one-time transcription.
PACER has said future CM/ECF versions will require PDF/A; the per-court flag
absorbs that. MFA means an attorney's session is theirs alone; nothing here
depends on otherwise.

## What would reopen this

The AO shipping the staged web services the IEPD promises — a case-opening
POST that *completes* docketing without "the form in the returned HTML" — or
a court publishing a vendor filing interface a hosted service may hold
credentials for. Either is the concrete machine-to-machine case this ADR
found absent; the registry, the filing set and the Case Upload package all
survive it, because they are the data such a path would send.

## Sources (all read 2026-09-23)

- S1 PACER, *File a Case* — https://pacer.uscourts.gov/file-case
- S2 PACER, *Attorney Filers for CM/ECF* — https://pacer.uscourts.gov/register-account/attorney-filers-cmecf
- S3 PACER, *Multifactor Authentication Coming Soon* (2025-05-02) — https://pacer.uscourts.gov/announcements/2025/05/02/multifactor-authentication-coming-soon
- S4 PACER, *Developer Resources* — https://pacer.uscourts.gov/file-case/developer-resources
- S5 AO, *Bankruptcy IEPD Document, NextGen CM/ECF Release 1.7* (Nov 2021), in https://pacer.uscourts.gov/sites/default/files/files/NGRel1.7_Individual_BK_Data_Collection_new.zip
- S6 PACER, *Authentication API User Guide* v2.0 (2025) — https://pacer.uscourts.gov/sites/default/files/files/PACER%20Authentication%20API-2025_v2_0.pdf
- S7 Fed. R. Bankr. P. 5005 — https://www.law.cornell.edu/rules/frbp/rule_5005
- S8 TXWB, *Administrative Policies and Procedures for Electronic Filing* (eff. 2025-02-03) — https://www.txwb.uscourts.gov/sites/txwb/files/2-3-2025%20-%20Electronic%20Filing%20Procedures.pdf
- S9 Texas Bankruptcy Courts, *ECF Procedures* (statewide, amended 2017-01-12) — https://www.txs.uscourts.gov/sites/txs/files/bk_adminproc.pdf.pdf; index at https://www.txnb.uscourts.gov/content/statewide-ecf-administrative-procedures
- S10 TXEB, *Appendix 5005* (redline to 2022-08-22) — https://www.txeb.uscourts.gov/sites/txeb/files/Appendix%205005%20-%20redline%2012-1-2016%20to%208-22-2022.pdf
- S11 GANB, *CM/ECF Administrative Procedures* (Aug 2021) — https://www.ganb.uscourts.gov/sites/default/files/cmecf_admin_procedures_08-2021.pdf
- S12 FLMB, *Local Rule 1001-2* (amended eff. 2025-08-15) — http://www.flmb.uscourts.gov/localrules/Rules/1001-2.pdf
- S13 GASB, *CM/ECF Administrative Procedures* (eff. 2016-12-01) — https://www.gasb.uscourts.gov/sites/gasb/files/AdminProcDec2016.pdf
- S14 TXWB, *NextGen Filing Agents* — https://www.txwb.uscourts.gov/nextgen-filing-agents; FLNB, *Filing Agents* — https://www.flnb.uscourts.gov/filing-agents
- S15 FLNB, *Document Format Requirements* — https://www.flnb.uscourts.gov/faqs/document-format-requirements
- S16 PACER FAQ, *Is there a limit on the size of the PDF files…* — https://pacer.uscourts.gov/help/faqs/there-limit-size-pdf-files-which-cmecf-will-accept
- S17 FLSB, *Local Rules* (2026 edition) — https://www.flsb.uscourts.gov/sites/flsb/files/local_rules/2026_Local_Rules.pdf
- S18 GASB, *CM/ECF Manual for Attorney Users* (Mar 2026) — https://www.gasb.uscourts.gov/sites/gasb/files/CMECF%20Manual%20for%20Attorney%20Users%20Mar%202026.2.pdf
- S19 FLMB, *Attorney Guide ch. 8, Case Upload* (2009) — http://www.flmb.uscourts.gov/cmecf/attorneyguide/documents/Chapter8.pdf; ch. 7, *Case Opening* — http://www.flmb.uscourts.gov/cmecf/attorneyguide/documents/chapter7.pdf; *Rule 1007-2* — http://www.flmb.uscourts.gov/localrules/Rules/1007-2.pdf
- S20 GAMB, *Clerk's Instructions* (Dec 2024) — https://www.gamb.uscourts.gov/USCourts/sites/default/files/local_rules/CLERKS_INSTRUCTIONS.pdf; *CM/ECF Registration* — https://www.gamb.uscourts.gov/USCourts/cmecf-registration
- S21 GASB, *Certification of Creditor Mailing Matrix* — https://www.gasb.uscourts.gov/forms/certification-creditor-mailing-matrix; *CM/ECF Registration Information* — https://www.gasb.uscourts.gov/cmecf-registration-information
- S22 AO, *CM/ECF Case Upload File Specifications* (eff. Mar 2022, rev. 2023-07-11) — https://pacer.uscourts.gov/sites/default/files/files/Mar_2022_case_upload_spec.pdf
- S23 TXEB, *LBR Appendix 1007-b-5, Matrix Submission* — https://www.txeb.uscourts.gov/sites/txeb/files/LBR%20Appendix%201007-b-5,%20Matrix%20Submission.pdf; TXWB, *List of Creditors Specifications* — https://www.txwb.uscourts.gov/list-creditors-specifications
- S24 D. Md. Bankr., *Approved Case Upload Vendor Software* — https://www.mdb.uscourts.gov/for-attorneys/approved-case-upload-vendor-software
- S25 GASB, *Petition Preparation Software with CM/ECF Case Data Upload Functionality* — https://www.gasb.uscourts.gov/sites/gasb/files/PetitionVendors.pdf
- S26 D.N.J. Bankr., *Bankruptcy Petition Software* (2002) — https://www.njb.uscourts.gov/news/bankruptcy-petition-software
- S27 Official Form 309A — https://www.uscourts.gov/sites/default/files/form_b309a.pdf
- S28 Bankruptcy Noticing Center — https://bankruptcynotices.uscourts.gov/ and *XML* — https://bankruptcynotices.uscourts.gov/xml
- S29 AO, *Release Notes for PACER Users, Bankruptcy NextGen 1.6* (free look; court-set file limits) — https://pacer.uscourts.gov/sites/default/files/files/BK016NGrn_PACER.pdf
- S30 TXSB, *Attorney Information* (live and `ecf-train` URLs) — https://www.txs.uscourts.gov/attorney-information
- S31 FLNB, *ECF Training* — https://www.flnb.uscourts.gov/ecf-training; PACER training registration — https://train-pacer.psc.uscourts.gov/pscof/registration.jsf
- S32 TXWB, *Attorney Training Prerequisites* — https://www.txwb.uscourts.gov/attorney-training-prerequisites
- S33 FLMB, *Training Database* — https://ecf.flmb.uscourts.gov/cgi-bin/flmb_faq.pl (listed; not fetched)
- S34 PACER, *Court CM/ECF Lookup* data (updated 2026-09-23) — https://pacer.uscourts.gov/file-case/court-cmecf-lookup/data.json
- S35 FLSB, *CM/ECF* — https://www.flsb.uscourts.gov/cmecf
- S36 TXWB, *ECF Admin Procedures* — https://www.txwb.uscourts.gov/ecf-admin-procedures
- S37 GANB, *CM/ECF Registration* — https://www.ganb.uscourts.gov/cmecf-registration; *Policy Regarding Electronic Filing by Attorneys* (2015) — https://www.ganb.uscourts.gov/sites/default/files/policy_re_electronic_filing_by_attys.pdf
- Also read: uscourts.gov, *Electronic Filing (CM/ECF)* — https://www.uscourts.gov/court-records/electronic-filing-cm-ecf; N.D. Cal. Bankr., *Case Upload* — https://www.canb.uscourts.gov/ecf/efiling-manual/case-upload; C.D. Cal. Bankr., *Email Notification* — https://www.cacb.uscourts.gov/manual/email-notification; TXEB, *Debtor and Creditor Attorneys* — https://www.txeb.uscourts.gov/e-services-attorneys; FLNB, *Registration Requirements* — https://www.flnb.uscourts.gov/faqs/registration-requirements
