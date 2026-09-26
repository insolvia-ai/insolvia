# ADR 0024 — Insolvia files the case itself, from its own infrastructure, under the attorney's CM/ECF login and on the attorney's per-filing approval

- **Status:** Accepted (2026-09-26)
- **Date:** 2026-09-23 (spike and first draft); 2026-09-26 (decision reversed
  and accepted by the maintainer)
- **Amends:** [ADR 0001](0001-client-stays-dumb-trust-boundary.md) — see
  **Where this departs from ADR 0001** below.
- **Relates to:** issue #368 (17.1, the spike this records); the court
  registry and firm defaults it specified landed in #395 (#360 / 14.8); feeds
  #369 (court-notice intake) and #370 (amendments). Rides on
  [ADR 0014](0014-the-repository-is-the-regulatory-release-registry.md)
  (the court registry is a regulatory series),
  [ADR 0017](0017-launch-states-florida-texas-georgia.md) (the ten launch
  districts), [ADR 0018](0018-sqs-queue-and-worker-lambda-over-step-functions.md)
  (queue + worker Lambda, and its dev approximation),
  [ADR 0019](0019-ai-review-calls-anthropic-from-the-worker.md) (the sealed
  tax-id pattern the credential vault copies) and
  [ADR 0021](0021-test-tiers-and-seed-fixtures.md) (what each tier may
  touch). The packet today is
  `services/api/src/insolvia_api/core/packet_assembly.py`; the registry is
  `packages/insolvia_core/src/insolvia_core/courts.py`.

## Decision

**Insolvia files the case.** A dedicated filing worker in Insolvia's own AWS
account signs in to the court's CM/ECF as the attorney — with the attorney's
PACER password and TOTP seed, held in an isolated credential vault — and
submits the filing set Insolvia prepared, in that district's docket order,
through that court's own screens (and its Case Upload facility where the
court has it and we have verified it). It captures the court's confirmation
and case number and marks the case filed.

This is option **(b)** from the spike, which the first draft of this ADR
rejected. The maintainer reversed that on 2026-09-26, choosing it over a
browser extension in the attorney's own session and over a machine-to-machine
path that does not exist (all three are under **Weighed and rejected**), and
chose three guardrails that are **binding requirements, not
recommendations** — no environment submits a filing unless all three hold:

1. **The attorney approves each filing, in Insolvia, re-authenticating at
   approval time.** Nothing is submitted without that per-filing act. The
   approval is made by the attorney whose credential will be used — never a
   paralegal, a firm administrator or Insolvia staff — after a fresh sign-in
   (the API checks the token's `auth_time`, minutes old, not the session's
   age). It is bound to a digest of exactly what will be filed: the court and
   division, every document's bytes, the Case Upload file, the docket order
   and the fee handling. Any change to any of those after approval voids it.
   It is single-use and expires.
2. **Written attorney authorization.** Before a credential can be stored,
   the attorney signs an authorization for Insolvia to submit filings under
   their CM/ECF login, on the terms of guardrail 1. The signed instrument
   (its text versioned in this repository, the version and a digest recorded
   with the signature) is stored *with* the credential and is a precondition
   for it: the vault refuses a credential with no current authorization, and
   withdrawing the authorization revokes the credential.
3. **Credential isolation.** The password and TOTP seed are sealed under a
   **dedicated KMS key**, in a **dedicated store** — not the case table and
   not the case key. **Only the filing worker can decrypt**: the key policy
   (not only IAM) allows `kms:Decrypt` to the filing worker's role alone, the
   API can seal (enrol) but not open, and no human role in staging or prod can
   open either. **Every use is access-logged** — an application row naming the
   attorney, the filing, and the purpose, beside CloudTrail data events on the
   key and the store. **The attorney can revoke instantly**, from Insolvia, and
   revocation is effective before the next submission: it destroys the sealed
   item, and the worker re-checks the credential's status immediately before
   the final submit, not only when it starts.

The maintainer was offered, and did not choose, a fourth guardrail — a legal
opinion before the first production filing. The rules-based objection the
research found is therefore **not resolved**; it is recorded under **Risks**,
accepted knowingly, and it sits with the attorney's licence.

**Filed** on a case means the worker (or, on a hand-back, the attorney)
recorded the court's case number and filing date from the court's
confirmation, and the court's own receipt is stored with it; the court's
notices remain the evidence of what was filed (#369).

### How server-side filing works

**Scope at launch** is opening a Chapter 7 individual case — the petition
package, schedules, matrix, the district's declaration and local forms. Later
filings (amendments, #370) reuse the same path, each needing its own approval.

**The pieces.**

- **Credential vault** (a new `infra/modules` store, one per environment):
  per attorney, per PACER account — the login, the sealed password, the
  sealed TOTP seed, the authorization reference, status (`active` /
  `revoked`), and which launch courts the attorney is registered to e-file in.
  Envelope encryption as in `insolvia_core.tax_ids`, but under its own key,
  with an encryption context carrying the attorney and credential reference so
  a ciphertext cannot be re-addressed onto another attorney. Enrolment is an
  API call that seals and never echoes; the screen that collects the TOTP seed
  is the one place the seed crosses a client.
- **Approval** (API + app): a screen showing the filing set exactly as it
  will go — the court, the documents in docket order with page counts, the
  Case Upload fields, the fee handling, the debtor's declaration — with the
  re-authentication in guardrail 1. Approving writes an approval record and
  enqueues one filing job; that is the only way a filing job is created.
- **Filing worker** (a new service, `services/filing`, its own image Lambda
  consuming its own SQS queue per ADR 0018): the only principal that can open
  the vault. It holds a headless browser, because every court's case opening
  is an HTML session (research §1); it signs in through PACER's
  Authentication API where the court's configuration allows it [S6], and
  through the CSO screens otherwise; it **generates the TOTP code at submit
  time** from the seed and never stores one; it carries the redaction flag
  the attorney acknowledged in their approval.
- **Per-court drivers**: one module per district that knows that court's
  NextGen screens — its Open BK Case menu, its division and county pickers,
  its Case Upload route where the registry says `legacy_txt` is verified, and
  screen-by-screen entry from the same data where it does not. Every screen a
  driver will act on is **fingerprinted**; a screen it does not recognise is
  a stop, never a guess. A district without a verified driver is not
  automated: it gets the hand-off checklist (below) and the attorney files it.

**Headless browser, and where Case Upload fits.** Case Upload is not an
alternative to the browser session — it is a file the court accepts *inside*
that session to fill the opening screens from our data (research §4). Where a
court's Case Upload is verified the driver uploads `debtor.txt` and answers
fewer screens; where it is not, the driver types the same values into the
screens. Either way, the registry says which, per court, with a `verified_at`.

**Idempotency — a retry never double-files.** Each approval carries a filing
id; the worker drives a state machine on the filing record, with conditional
writes so one filing is only ever in flight once:
`approved → claimed → signed_in → uploading → at_final_submit → submitted →
filed`, with `handed_back` and `outcome_unknown` as terminal stops.
Everything before `at_final_submit` is safe to abandon and restart, because
nothing reached the court's docket. The click that commits the filing is
preceded by writing `at_final_submit`; a crash, timeout or lost response
after that point moves the filing to **`outcome_unknown`**, which is **never
retried automatically** — the worker (or the attorney) reconciles by looking
the debtor up on the court's own query before anything else happens. SQS
redelivery of an already-claimed job is a no-op.

**Capture of what came back.** On the court's confirmation screen the worker
stores the case number, the filing date and time, the docket entries it
created, the confirmation page itself (HTML and a rendered PDF), and the
payment receipt if one was produced. The case moves to `filed`, its pins
freeze, and #369 keys the court's later notices on that case number.

**Failure stops and hands back.** Any screen the driver does not recognise,
a court validation message, a deficiency prompt, a login or MFA failure, a
bot-detection challenge (which we never attempt to pass), a timeout, or a
revoked credential **stops the run where it is**. The worker does not click
forward, does not retry with altered input, and does not guess. It stores
what it saw, moves the filing to `handed_back` (or `outcome_unknown`, above),
and tells the attorney where it stopped and what the court said. The
attorney finishes in their own CM/ECF session from the same filing set and
checklist the hand-off path always produced, and records the result.

**An environment kill switch** — one flag the maintainer can flip — refuses
every submission in that environment without a deploy, alongside the per-
attorney revocation.

**Filing fees are an open question, and card details are not stored.**
CM/ECF collects the fee through pay.gov inside the session, per filing or
per session, with short lockout windows (research §3). This ADR does **not**
decide to store, tokenise or enter card details; that would put Insolvia in
card-data scope and is a separate decision. Until it is made, a filing that
reaches the court's payment step is handed back at that step for the
attorney to pay in their own session within the court's window, and cases
filed with an installment application or a fee-waiver application proceed
past it. Whether a court's "pay later" route is dependable enough to rely on
is part of each driver's training-database verification.

### Where this departs from ADR 0001

ADR 0001's reasoning is that *"a credential shipped to a client is a
credential we no longer control"*, and its rule is that only Insolvia's own
execution roles authenticate to anything. This decision goes further than
0001 contemplated in the other direction: **Insolvia now holds a third
party's credential server-side** — a credential that is, by FRBP
5005(a)(3), that person's signature in federal court. ADR 0001's rule about
clients is unchanged (no client holds the credential either; the seed crosses
the enrolment screen once, inbound). What changes is that the trust boundary
now contains a secret that is not ours, whose misuse is a forged court
filing rather than a data leak. Guardrail 3 is how that secret is fenced; the
spike's line *"holding the attorney's password and TOTP seed server-side is
exactly the credential ADR 0001 refuses"* is kept below as the argument this
decision overrode. ADR 0001 carries an amendment note pointing here.

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
*(Kept as the spike wrote it. The maintainer reversed this conclusion on
2026-09-26 — see the Decision, **Weighed and rejected**, and **Risks**, where
the objection this paragraph states is recorded as accepted, not answered.)*

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


## Weighed and rejected

The spike's comparison, as the first draft wrote it — (b) is the option now
chosen, with the three guardrails added; its cells are left as the spike
judged them:

| | (a) prepare & hand off | (b) hosted browser automation | (c) court upload facility | (d) certified vendor |
|---|---|---|---|---|
| Credentials held by | the attorney | **us** — password + TOTP seed | the attorney | — |
| The filer is | the attorney, in their session | ambiguous; forbidden by every district's sharing rule | the attorney; our file is input | — |
| Reliability | ours ends at a clean package | scraping ten court-modified UIs ("programming based on this information may not work in the same manner in every court" [S4]) | per-court field configuration; fails loudly on upload | — |
| Per-district cost | a registry record + checklist | ten UIs kept working, MFA per attorney | one verification session per court | — |
| Testable locally (ADR 0021) | unit: validators over the package | only against a real court | unit: spec validator; QA/train for the real thing | — |
| Time to first real filing | when registry and checklist exist | after per-court automation, and a rule review we would lose | after one training session per court | never — no program |

Two of (b)'s cells no longer describe (b) as designed here: *testable
locally* (the fake CM/ECF server runs the whole flow on a laptop; only each
court's real screens need a court — see **Three environments**), and *a rule
review we would lose* (no rule review was obtained; see **Risks**).

(c) is not an alternative to (a) *or* (b); it is the best shape for the data
either one sends, and survives in the decision as the driver's upload route.

**Prepare-and-hand-off (the first draft's decision, spike option (a)).**
*Insolvia does not file; the attorney files from their own CM/ECF session,
and Insolvia prepares everything that session will ask for* — the packet in
docket order, the matrix, the declaration and local forms, a checklist, and
(per court, once verified) the Case Upload package. Its argument, preserved:
every launch district's rule makes the login the signature and forbids anyone
but an individual filing agent from using it (research §2); a hosted service
is not a person and cannot hold a PACER account; holding the password and
TOTP seed server-side is the credential ADR 0001 refuses; and scraping ten
court-modified UIs makes reliability ours where (a) ends at a clean package.
It had the fastest path to a first real filing and the smallest attack
surface. **Rejected by the maintainer (2026-09-26)** because it leaves the
attorney doing the filing — re-keying or uploading, screen by screen, in every
case — which is the work the product exists to remove; the rules objection is
answered by the guardrails and, where it is not, accepted (see **Risks**).
It is not deleted: it is the fallback for every district without a verified
driver, and the path every hand-back lands on.

**A browser extension in the attorney's own session (the maintainer's
option A).** An extension would drive the court's screens from the
attorney's own logged-in browser: the attorney signs in, the credential never
leaves them, and the sharing rules are not engaged. Rejected because it adds
a shipping target beyond web — an extension per browser, with its own store
review, update channel and permissions, against D9 and
[ADR 0004](0004-react-native-replaces-flutter.md)'s one target — and because
it keeps the attorney in the loop for every step: their browser open, their
session live, their MFA, their machine, for the whole of each filing.

**Machine-to-machine filing (the maintainer's option C).** There is no such
service to use. The AO's XML Case Opening web services "result in a logged-in
session for the lead event to be docketed manually" [S5] — the XML fills the
screens, a person finishes in the browser. What would reopen this names the
day that changes.

**A certified vendor integration (spike option (d)).** Does not exist as a
program; courts approve Case Upload vendors one at a time by testing, and
say approval is not an endorsement [S24–S26].

## Per-district scope at launch

All ten districts get a registry record and the filing set and checklist.
**Automated filing is enabled per district only after that court's driver
has opened the fixture case on its training database** (PR 10); until then
that district is prepare-and-hand-off — the checklist says "open the case on
the court's screens from the packet". Case Upload inside a driver is enabled
per district on the same evidence.

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


## The court registry — what a district record needs (built, #395)

The record shape below is what #395 implemented; it stands as the
specification. This decision adds, per court: whether a filing driver is
verified (with the driver version and `verified_at`), and the live and
training hosts the environment fence allows. Per district:

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

Firm defaults (default court, division and chapter, letterhead, a signature
block per attorney) were #360's second half and also landed in #395.

## Three environments

The filing worker's court hosts are **fenced per environment, in code and in
configuration**: dev may reach only the local fake; staging only the courts'
training databases and PACER's training and QA hosts; prod only the live
hosts. A worker asked to sign in anywhere else refuses before it decrypts.
Seeds never carry a credential — the repository is public, and the loader
already refuses prod.

- **Local (a developer's machine, `infra/envs/dev`).** A **fake CM/ECF
  server** — a fixture in `services/filing` — plays PACER's authentication
  (password and a real TOTP check against a test seed the fixture generates),
  two court configurations (one with Case Upload, one screen-entry only), the
  Open BK Case screens, a Case Upload that refuses a wrong field count the way
  the spec says, a payment step, and a confirmation screen with a case number.
  It has fault modes: an unrecognised screen, a validation message, an MFA
  failure, a timeout after the final submit, and a redelivered job. The
  worker drives it with the same headless browser it uses in the cloud, so
  the whole flow — enrol, authorize, approve with re-auth, enqueue, sign in,
  upload, submit, capture, hand back, revoke — runs on a laptop. As ADR 0018
  already does for the pipeline worker, `infra/envs/dev` creates the real
  per-machine queue, the vault table and its dedicated KMS key, and the
  worker runs as a local poller against them, so the key policy and the
  sealed round-trip are exercised against real AWS, not a stub. Unit tests
  cover the state machine, the approval digest, the Case Upload spec
  validator and each driver's screen fingerprints; integration tests drive
  the fake.
- **Staging.** The courts' own training databases (`ecf-train` for TXSB,
  TXWB, FLMB, FLNB) and PACER's training and QA environments, with the test
  attorney accounts they issue [S5, S30–S33]. A driver is marked verified in
  the registry only after it has opened the fixture case end to end on that
  court's training database — the credentials entered by the maintainer
  through the same enrolment screen, never seeded from the repo.
- **Prod.** Real filings, by real attorneys, under the three guardrails.
  The first production filing per district follows that district's verified
  staging run.

**What genuinely cannot be tested locally**, with the nearest approximation:
each court's real screens and their drift (the fake plays two
configurations; the fingerprints are captured from training-database
sessions with fixture data, and a mismatch in prod is a hand-back, not a
failure mode we can rehearse); PACER's real MFA and session behaviour (the
fake runs real TOTP); whether a court's Case Upload accepts our file (the
spec validator locally, the training database for the real answer); pay.gov
(nothing — payment is handed back, above); the court's NEF email and the BNC
notices (#369's fixtures); and whether a court's infrastructure blocks
automated sessions from our egress (only a training database can show it).
None of these is "you'll see it on staging": each has a local stand-in and
a named staging check.

## Build breakdown

In order. Sizes: S ≈ a day or two, M ≈ a week, L ≈ more.

| # | PR | Done when | Size |
|---|---|---|---|
| 1 | **Court registry and firm defaults** — `courts/us-bankruptcy`, `case.district` → `{court, division}`, `GET /v1/courts`, firm default court/division/chapter, letterhead, per-user signature blocks prefilling the signer | **Done — #395** | — |
| 2 | **Matrix keyed by court** — #395 already reads `DISTRICT_VARIANCES` from the registry and added the name-line width and comma-after-city knobs; remaining: TXEB's case-number header when the matrix is filed separately, and per-district goldens | the fixture case's matrix differs byte-for-byte between `txsb`, `txnb`, `txwb`, `txeb`, `gamb` and the common format, each pinned | S |
| 3 | **Filing set and checklist** — the packet in the district's docket order and file names; PDF checks (size split, text layer, page size); the district's declaration and B121 handling; a checklist built from the record. The checklist is both the hand-off path and every hand-back's landing page | the packet screen shows the filing set and checklist for the case's district; unit tests over every record | M |
| 4 | **Credential vault** (guardrail 3) — `infra/modules` store and dedicated KMS key in dev, staging and prod; key policy granting Decrypt to the filing worker role alone and seal-only to the API; encryption context per attorney and credential; enrol / status / revoke endpoints; an application access row on every open; CloudTrail data events via `audit_trail` | in dev: enrol seals, the worker's role opens, the API's role is refused Decrypt by the key policy, revoke destroys the item and the next open fails; unit tests pin the policy documents | M |
| 5 | **Authorization capture** (guardrail 2) — the authorization text, versioned in the repo; the attorney signs it in the app after re-authenticating; the signed record (text version, digest, time, signer) stored with the credential; the vault refuses a credential without a current one; withdrawal revokes | enrolment is impossible without a signed authorization, and withdrawing it revokes the credential — both proved in integration tests and in dev | S |
| 6 | **Per-filing approval** (guardrail 1) — the approval screen; fresh-`auth_time` check; only the credential's owner may approve; approval bound to the filing set's digest, single-use, expiring; approval is the only producer of a filing job | editing any document after approval voids it; a paralegal and a stale session are both refused; unit and integration tests, and in dev against the real queue | M |
| 7 | **Filing worker and the fake CM/ECF** — `services/filing` (new service, image Lambda, own queue per ADR 0018); the state machine and conditional writes; TOTP at submit; the environment host fence and kill switch; stop-and-hand-back; receipt capture into the case; the fake server with its fault modes | the fixture case files end to end against the fake on a laptop; every fault mode ends `handed_back` or `outcome_unknown`; a redelivered job and a crash after the final submit never produce a second filing | L |
| 8 | **Filed-state capture** (feeds #369) — `status=filed`, `court_case_number`, `filed_at`, the stored confirmation and receipt; written by the worker, or by the attorney on a hand-back; the pins freeze | a staging case reaches `filed` from the worker, and from a manual hand-back, with a case number the notice matcher can key on | S |
| 9 | **Case Upload package** — `debtor.txt` per the AO spec (statistics from the 106/122 projections, debtor and alias records, office and county codes from the registry), a spec validator under pytest. The full SSN comes from the sealed tax id (13.12) through a logged `taxid.read` with its own purpose | the fixture case's `debtor.txt` validates against the 80-field spec in unit tests and is accepted by at least one training database in PR 10 | M |
| 10 | **Training-database runbook and per-court drivers** — register on PACER training and QA and each district's `ecf-train`; one driver per launch district, fingerprints captured from those sessions; the registry records per court whether the driver and Case Upload are verified, with `verified_at` | per district: the fixture case opened end to end on that court's training database by the worker, and the registry record updated. Human-supervised, outside CI. Each district S–M; all ten, L | L |
| 11 | **Filing fees** — decide how the fee is paid when the court's payment step is reached, without storing card details unless a separate decision says so; until then the hand-back at payment stands | a written decision; the driver's behaviour at payment matches it on every verified district | S (decision) |
| 12 | **XML case opening** (post-launch) — the IEPD package from the same projections, behind the same per-court verification | only if PR 10 shows a launch district exposes the XML menu | L |

No production filing happens until PRs 4–8 are live in prod and at least one
district's driver is verified in PR 10.

## Consequences

- **The attorney's work per case becomes one approval.** The prepared set
  is filed without re-keying; the attorney's act is reviewing and approving
  it, which is also the act FRBP 5005(a)(3) needs to be theirs ("authorized
  by that person").
- **Insolvia now operates something whose failure is a court filing.** A
  wrong filing is not a bug report; it is a docket entry under an attorney's
  signature. That is why every uncertainty stops and hands back, and why
  `outcome_unknown` is never retried.
- **A new service and a new store.** `services/filing` is the only principal
  that can open the vault; the API's role never can. IAM review grows by one
  role and one key policy, both deliberately narrow.
- **Ten UIs to keep working.** Each court's NextGen configuration is its own
  and changes on its own schedule [S4, S34]; a driver that stops recognising
  a screen hands back until it is re-verified. Drivers are maintenance, not
  a one-time build.
- **Prepare-and-hand-off is not gone.** It is the fallback per district and
  on every failure, so its build (PRs 2, 3, 9) is on the critical path either
  way.

## Risks

**The rules-based objection stands, and was accepted knowingly.** The
research (§2) found that FRBP 5005(a)(3) makes a filing through a person's
account, authorized by them, their signature [S7]; that the launch
districts' local rules make the login the signature and restrict its use —
TXWB prohibits filers from "sharing their login information with any other
person for any reason" and names linked filing agents with their own PACER
accounts as "the only approved method" of filing under another's
credentials, with the Texas statewide procedures, GAMB, GANB, FLMB Rule
1001-2(c) and GASB to the same effect [S8–S14]; that PACER's own terms of use
also govern the account — the spike did not read them, and PR 5's
authorization text must be checked against them; and that
mandatory multifactor authentication since 2025-12-31 exists to bind a
session to the person enrolled [S3]. Guardrails 1 and 2 make each filing
genuinely *authorized by* the attorney; **they do not make the login
unshared**, and nothing in this design does. The maintainer was offered a
legal opinion before the first production filing and did not choose it. The
risk therefore sits, first, **with the attorney's licence and e-filing
privileges** — a court that concludes its sharing rule was broken can revoke
filing privileges or sanction the filer — and then with Insolvia's standing
with those courts. The authorization in guardrail 2 must say this plainly to
the attorney who signs it.

- **A vault of court signatures is a high-value target.** A breach is not
  disclosure; it is the ability to file in federal court as each enrolled
  attorney. Guardrail 3, the per-filing approval (a stolen credential alone
  cannot create a filing job through Insolvia), the environment fence, and
  the kill switch are the defences; a compromise of the worker role itself
  defeats the approval check, which is why that role is the narrowest in the
  account.
- **Courts can see and stop automation.** A court may block our egress, add
  a challenge, or change a screen without notice. Every one of those is a
  hand-back, not a retry; but a district could go from automated to manual
  overnight.
- **A lost response after the final submit.** The one window where we
  cannot know whether the court docketed the filing. `outcome_unknown` and
  reconciliation against the court's own record are the answer; the
  residual risk is a delay, never a duplicate.
- **The full SSN travels.** `debtor.txt`, the IEPD and the opening screens
  all carry it. It is read from the sealed tax id through a logged read and
  exists in the worker's memory for the length of one run.
- **Per-court configuration** makes Case Upload and each driver a
  ten-times-verified feature, not one build, and a court can turn Case Upload
  off. The Texas statewide procedures are dated 2017 and disagree with TXWB's
  2025 document on size limits — `verified_at` is the defence, not a one-time
  transcription. PACER has said future CM/ECF versions will require PDF/A;
  the per-court flag absorbs that.
- **Fees.** Until PR 11 decides, every fee-paid filing hands back at payment,
  and the courts' lockout windows (TXSB 48 h, GASB same day, TXNB 24 h for
  software filings) run from submission [S9, S13].

## What would reopen this

- A court, the AO or PACER stating — in a rule, an order, a published
  notice or an answer to an inquiry — that a hosted service may not submit
  under an attorney's login even with written authorization and per-filing
  approval; or any sanction or loss of privileges for an attorney who filed
  through Insolvia. Either moves the affected districts to
  prepare-and-hand-off at once, via the per-district driver flag.
- A credential compromise, or evidence that the vault's isolation was
  weaker than guardrail 3 requires.
- A legal opinion, if the maintainer later obtains one, concluding the
  practice is not permitted.
- The AO shipping the staged web services the IEPD promises — a
  case-opening POST that *completes* docketing without "the form in the
  returned HTML" — or a court issuing filing-agent access to an organisation
  rather than a person. Either is a sanctioned machine path and replaces the
  headless browser; the registry, the filing set, the Case Upload package
  and the approval step all survive it, because they are the data and the
  consent such a path would need.

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
