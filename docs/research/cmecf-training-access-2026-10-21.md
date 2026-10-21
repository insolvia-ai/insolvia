# CM/ECF training-database access in the ten launch districts — 2026-10-21

Research for issue #470, on the map #467. [ADR 0024](../adr/0024-electronic-filing-path.md)
PR 10 enables automated filing in a district only after that court's driver
has opened the fixture case on the court's **training database**. PR 12
(XML case opening) happens only if PR 10 shows that a launch district
exposes the XML menu. This file asks what each of the ten districts of
[ADR 0017](../adr/0017-launch-states-florida-texas-georgia.md) requires before
that session can happen. The districts are `flnb`, `flmb`, `flsb`, `txnb`,
`txsb`, `txeb`, `txwb`, `ganb`, `gamb` and `gasb`, as named in
`packages/insolvia_core/src/insolvia_core/regulatory/courts/us-bankruptcy/2026-10-06/districts/`.

**This is a snapshot. It owns no fact.** Each claim is marked
**primary** (the court's, PACER's or the AO's own page or document, read
today), **primary via ADR 0024 / registry** (read from a primary source on
2026-09-23/24 and carried here, not re-read), **secondary** (a search
summary or an inference from primary data), or **not found** (looked for on
the court's pages and absent). Not found means only that: the court may
still answer by phone. Contact addresses are left out on purpose, because
this repository is public. Each court's help desk is linked through its
pages in [Sources](#sources).

---

## 1. Summary

- **No court publishes a vendor or developer path to its training
  database.** Every court that describes its training database (or that, as
  in Texas, writes it into its procedures) treats it as the place where a
  *registering filer* practises before getting live access. Getting in has two
  steps. First, open an account in PACER's *training* environment. This is
  self-service, and the form carries a reCAPTCHA, so a human does it.
  Second, submit an "Attorney Admissions / E-File Registration" or a
  "Non-Attorney E-File Registration" to the court's "(train)" entry. The
  clerk then approves it [S3, S4, S5]. No launch court says whether it will
  approve a non-attorney, non-staff developer for training. **The safe
  reading for PR 10 is that the request comes from the design-partner
  attorney, or from a person the attorney links as a filing agent.** The
  district attorney admission each court requires for *live* filing is not
  stated as a requirement for training. Ask each help desk.
- **Four districts publish a training or test host on their own pages.**
  These are FLMB (`ecf-train`, with a self-serve eTraining registration page),
  TXNB (`ecf-train`), TXSB (`ecf-train`) and GAMB (both `ecf-train` and
  `ecf-test`). The other six publish nothing. **Every one of the twenty
  `ecf-train.<court>` / `ecf-test.<court>` hostnames resolves in DNS**, so the
  hosts exist. Whether external filers may use them is not stated.
- **From this machine, every training and test host timed out on HTTPS,
  while the live hosts answered 200.** This is a probe from one network on
  2026-10-21, not a statement by any court. Possible causes are IP
  allow-listing, a requirement to register first, or hosts that are offline.
  It matters to ADR 0024's staging tier. The worker runs from Insolvia's AWS
  egress, and if the training hosts accept only allow-listed or registered
  networks, **PR 10's first session per court must confirm that the worker's
  egress can reach the host at all.**
- **Lead time: not found in any district.** No court states how long a
  training (or live) registration takes. GAMB says live requests are
  "approved in a timely manner" [S12]. TXNB's debtor-attorney class is
  scheduled by date, and you are "not confirmed" until the court replies
  [S9].
- **Whether training mirrors production: not found in any district.** No
  page states the training database's release or says it matches live.
  PACER's lookup lists only the *live* release [S1]: 1.8.3 for FLMB, FLSB and
  GAMB, and 1.9 for the other seven, unchanged since the registry's
  2026-09-24 reading. **Each driver's fingerprints must therefore be checked
  against live as well. A pass on training is not proof for live.**
- **XML case opening: no launch court documents it.** No court page read
  mentions an XML case-opening menu. FLMB, FLNB, TXWB and GANB document
  **Case Upload** (the legacy text file). GASB's March 2026 manual documents
  only the Open BK Case screens. PACER's lookup lists a `WithXMLData` case
  flag ("A code to track cases opened with XML data") for 8 of the 10 courts,
  all except GAMB and GASB. But 90 of the 94 bankruptcy courts carry it, so
  it reads as a default that ships with the release, not as a court
  switching the menu on [S1 — secondary inference]. The only place the AO
  documents XML access is its **QA environment**. The IEPD says QA test
  accounts come "already registered" with "access to the XML Case Opening"
  [S2]. **PR 12's trigger cannot be settled from published pages.** It needs
  a look at each court's menu in a training session, or a question to each
  help desk.
- **The rules on who may use a login are uniform.** In all ten districts the
  login is the attorney's signature, sharing it is prohibited, and the only
  sanctioned exception is an "authorized agent" or "filing agent", which
  means a *person* with their own PACER account linked by the attorney.
  ADR 0024 already records this objection to option (b) as accepted, not
  answered. Two district points bear on PR 10 itself. The 2017 Texas
  statewide procedures make docketing "satisfactorily to the Authorizing
  Court's training system" a **prerequisite to live access** everywhere in
  Texas except S.D. Tex. [S6]. TXWB's 2025 procedures leave training "at the
  discretion of the Clerk" [S7].

---

## 2. Per district — the training database

| District | Training/test DB offered? | How access is requested, by whom | Lead time | Mirrors prod? | XML case opening? |
|---|---|---|---|---|---|
| **N.D. Fla.** `flnb` | **Not on current pages.** Today's ECF Training page points only to PACER's online modules [S13]. A 2012 court manual says training is "self-paced… through our training database" [S14] (primary, stale). `ecf-train`/`ecf-test` resolve in DNS, and HTTPS timed out [probe]. | Not stated. Live access needs N.D. Fla. district-court admission, verified before a login is approved [S15]. | Not found | Not found (live 1.9 [S1]) | Not documented. Case Upload is documented [S16]. `WithXMLData` flag present [S1]. |
| **M.D. Fla.** `flmb` | **Yes.** "Logon to Train" at `ecf-train.flmb.uscourts.gov`, with a separate eTraining registration page [S17]. | Through the court's eTraining registration page [S17]. Who may register is not stated. The 2025 LR 1001-2 amendment deleted the training requirement for live access [S18]. Live access requires M.D. Fla. district-court admission (LR 1001-2(b)) [S18]. | Not found | Not found (live 1.8.3 [S1]) | Not documented. Case Upload is a listed event (registry, primary via registry). Flag present [S1]. |
| **S.D. Fla.** `flsb` | **Not published.** The CM/ECF page names only the live database [S19]. DNS resolves, and HTTPS timed out [probe]. | Not stated. The live LF-95 form is for attorneys with S.D. Fla. admission (primary via registry). | Not found | Not found (live 1.8.3 [S1]) | Not documented. Flag present [S1]. |
| **N.D. Tex.** `txnb` | **Yes.** "Log in to ECF – Training" → `ecf-train.txnb.uscourts.gov` [S9, S10]. | Not stated for the database. The debtor-attorney class (4 h, at most 10 seats, staff may attend for the attorney) is booked by asking the court by email [S9]. The statewide procedures make training-system docketing a prerequisite to live access [S6]. | Not found. Class dates "TBA" [S9]. | Not found (live 1.9 [S1]) | Not documented. Flag present [S1]. |
| **S.D. Tex.** `txsb` | **Yes.** "Training Bankruptcy CM/ECF" → `ecf-train.txsb.uscourts.gov` [S8]. | Not stated. Training "is available on request but is not required" (statewide procedures' S.D. exception) [S6]. | Not found | Not found (live 1.9 [S1]) | Not documented. Flag present [S1]. |
| **E.D. Tex.** `txeb` | **Not published.** The attorney e-services page covers live registration only [S20]. DNS resolves, and HTTPS timed out [probe]. | Not stated. Live access requires good standing in the E.D. Tex. district court (LBR 9010-1) [S20]. The statewide training prerequisite applies [S6]. | Not found | Not found (live 1.9 [S1]) | Not documented. LBR App. 1007-b-5 assumes Case Upload (primary via ADR 0024). Flag present [S1]. |
| **W.D. Tex.** `txwb` | **Not published.** The court publishes eight training modules plus one-on-one training, not a database URL [S21]. ADR 0024's "`ecf-train`" for TXWB is not on any page read. DNS resolves, and HTTPS timed out [probe]. | Modules, then one-on-one training, then an answer sheet sent to the court's registrar [S21]. Training "may be required" at the clerk's discretion [S7]. | Not found. The modules take "approximately two hours" [S21]. | Not found (live 1.9 [S1]) | Not documented. The procedures assume Case Upload's `debtor.txt` [S7]. Flag present [S1]. |
| **N.D. Ga.** `ganb` | **Not published.** Training is four videos, which must be watched before live access, and the requirement is waivable for current e-filers elsewhere [S22, S23]. DNS resolves, and HTTPS timed out [probe]. | Not stated for a database. | Not found | Not found (live 1.9 [S1]) | Not documented. Case Upload is "optional" [S24]. Flag present [S1]. |
| **M.D. Ga.** `gamb` | **Yes.** The NextGen page lists both a **Test** and a **Train** login (`ecf-test`, `ecf-train`) [S25]. The registration page links the test login [S12]. Neither is explained. | Not stated. The court's NextGen mailbox is named for questions [S12, S25]. Live registration is the same PACER path "for attorneys, trustee, filing agents, and limited filers" [S12]. | Not found. Live access is "approved in a timely manner" [S12]. | Not found (live 1.8.3 [S1]) | Not documented. **No `WithXMLData` flag. GAMB publishes no flags at all** [S1]. |
| **S.D. Ga.** `gasb` | **Not published.** The court offers free classes, scheduled through the divisional manager [S26, S27]. DNS resolves, and HTTPS timed out [probe]. | Contact the divisional office for training [S26]. Live registration is through PACER after agreeing to the terms [S28]. | Not found | Not found (live 1.9 [S1]) | Not documented. The March 2026 manual covers only Open BK Case screens [S27]. **No `WithXMLData` flag** [S1]. |

**The national path, behind every row.** The SDB guide (S.D. Bankr.,
2020, not a launch court, used here as the clearest primary description of
the NextGen mechanism) sets it out. You open a PACER *training* account
("Case Search Only Registration", a reCAPTCHA, and fictitious payment
details), then make an e-file registration to the court's "(train)" entry as
an attorney or non-attorney. The training PACER account "is only for viewing
documents, not for e-filing" until that registration is approved [S5].
train-pacer.uscourts.gov describes itself as the site "used for courts to
train public users" [S4]. The AO's separate **QA** environment (Testing
Services Division) issues credentials that "grant you PACER access to all
courts in those environments" and are pre-registered with attorney filer
privileges and XML access [S2]. The AO publishes no request procedure for
QA. Its developer page offers only a mailing list and a developer contact
[S3]. QA is one national test court, not each district's configuration, so
it cannot stand in for PR 10's per-court session.

## 3. Per district — rules that bear on an automated filer under an attorney's login

| District | Login is the signature | Who may use the login | Source |
|---|---|---|---|
| `flnb` | Yes | Filing agents need their own PACER account, linked by the attorney | Admin. Procedures II.A (primary via registry) |
| `flmb` | Yes, LR 1001-2(g)(1) | "No… person may knowingly permit… password to be used by anyone other than an authorized agent". One attorney may not use another's password. LR 1001-2(c) | [S18] primary |
| `flsb` | Yes | The credential must match the attorney on the document and may not be shared. Staff use filing-agent accounts. An agent's entries docket as the filer's, with "no indication" of the agent, but are logged | LF-95 / Manual (primary via registry); [S29] primary |
| `txnb` `txsb` `txeb` | Yes, statewide III.B.2 | "No person shall knowingly utilize… the password of an Electronic Filer unless such a person is an authorized agent" | [S6] primary (2017) |
| `txwb` | Yes, IV.B | Sharing is "prohibited… for any reason". A filing agent must have "an individual PACER account". Linking one is "the only approved method of allowing another party to file documents utilizing the credentials" | [S7] primary (eff. 2025-02-03) |
| `ganb` | Yes | The account "should not be shared". Employees get filing-agent accounts. "Unauthorized use of a password is prohibited" | [S24] primary (Aug 2021) |
| `gamb` | Yes | No filer may let a password be "utilized by anyone other than the registered filer's authorized agent" | [S30] primary (Clerk's Instructions) |
| `gasb` | Yes | The filer agrees that use of the login "constitutes my signature for all purposes" and "affirmation that I am admitted", and agrees to "protect the security of my password" | [S28] primary (terms, 2021-10-12) |

The IEPD has one technical point for PR 10/12 drivers. If an XML POST meets
the filing-agent user-selection screen, the session cannot continue. A
filing agent who files for several attorneys must send a `FilingAgentUser`
cookie [S2].

## 4. What this means for ADR 0024 PR 10 and PR 12

1. **Who requests access.** Plan on the design-partner attorney (or a named
   filing agent of theirs) requesting training access court by court. No
   page supports Insolvia requesting it as a vendor. In Texas outside S.D.,
   the attorney's own onboarding already runs through the training system
   [S6].
2. **Reachability first.** Before writing a driver, confirm from the worker's
   actual egress that `ecf-train.<court>` answers. All ten timed out from
   here.
3. **Six districts need a phone call before a session can be planned**:
   `flnb`, `flsb`, `txeb`, `txwb`, `ganb` and `gasb` publish no training
   database. If one has no external training database, PR 10 cannot verify
   that district as ADR 0024 defines it, and the district stays
   prepare-and-hand-off.
4. **Treat a training pass as provisional.** No court states that training
   runs the live release.
5. **PR 12 stays parked.** No court documents XML case opening. The near-universal
   `WithXMLData` flag is not evidence that the menu is exposed.

## 5. Not checked — open threads

- Each court's help desk was not contacted. Every "not found" above is an
  open question for one.
- Whether a court approves a *non-attorney, non-staff* developer's
  training registration.
- TXNB's and TXEB's current procedure pages (TXNB's sections returned 404
  in ADR 0024's reading). FLSB's 2026 Procedural Manual and GASB's Mandatory
  Administrative Procedures were not re-read for training content.
- The NIEM listing for the XML IEPD did not resolve (`niem.gov` failed DNS
  lookup). The IEPD was read from PACER's copy.

## Sources

All read 2026-10-21 unless marked. "[probe]" is a DNS + HTTPS check run
from the researcher's machine on 2026-10-21: every `ecf-train` and `ecf-test`
host for the ten courts resolved, and none answered within 10 s, while
`ecf.txsb` and `ecf.flmb` returned HTTP 200.

- **[S1]** PACER, *Court CM/ECF Lookup* data (`last_updated` 2026-10-21): `software_version`, `flags` — primary. https://pacer.uscourts.gov/file-case/court-cmecf-lookup/data.json
- **[S2]** AO, *Bankruptcy IEPD, NextGen CM/ECF Release 1.7* (Nov 2021), §§3.1–3.3 — primary. https://pacer.uscourts.gov/sites/default/files/files/NGRel1.7_Individual_BK_Data_Collection_new.zip
- **[S3]** PACER, *Developer Resources* — primary. https://pacer.uscourts.gov/file-case/developer-resources
- **[S4]** PACER training site, home and *Attorney Filers* — primary. https://train-pacer.uscourts.gov · https://www.train-pacer.uscourts.gov/register-account/attorney-filers-cmecf · registration form https://train-pacer.psc.uscourts.gov/pscof/registration.jsf
- **[S5]** D.S.D. Bankr., *Filing in the NextGen CM/ECF Training Database* (2020) — primary, not a launch court. https://www.sdb.uscourts.gov/sites/sdb/files/Filing%20in%20the%20NextGen%20CMECF%20Training%20Database.pdf
- **[S6]** Texas Bankruptcy Courts, *ECF Procedures* (amended 2017-01-12), §§II.A–B, III.B.2 — primary. https://www.txs.uscourts.gov/sites/txs/files/bk_adminproc.pdf.pdf
- **[S7]** TXWB, *Administrative Policies and Procedures for Electronic Filing* (eff. 2025-02-03), §§II.A.2, II.C.3, III.A–B, IV.B — primary. https://www.txwb.uscourts.gov/sites/txwb/files/2-3-2025%20-%20Electronic%20Filing%20Procedures.pdf
- **[S8]** TXSB, *Attorney Information* — primary. https://www.txs.uscourts.gov/attorney-information
- **[S9]** TXNB, *Electronic Case Filing – Debtor Attorney Class* — primary (undated). https://www.txnb.uscourts.gov/node/989
- **[S10]** TXNB, *NextGen Information* — primary. https://www.txnb.uscourts.gov/content/nextgen-information
- **[S12]** GAMB, *CM/ECF Registration* — primary. https://www.gamb.uscourts.gov/USCourts/cmecf-registration
- **[S13]** FLNB, *ECF Training* — primary. https://www.flnb.uscourts.gov/ecf-training
- **[S14]** FLNB, *ECF User Manual ch. 2, Getting Started* (PDF dated 2012-07-05) — primary, stale. https://www.flnb.uscourts.gov/sites/flnb/files/02-Getting%20Started.pdf
- **[S15]** FLNB, *Registration Requirements* — primary. https://www.flnb.uscourts.gov/faqs/registration-requirements
- **[S16]** FLNB, *ECF User Manual* (Case Upload) — primary. https://www.flnb.uscourts.gov/ecf-user-manual
- **[S17]** FLMB, *CM/ECF Help Desk / Support* (Logon to Train; eTraining registration at `ecf-train.flmb.uscourts.gov/cgi-bin/flmb_training.pl`, which did not load for this fetch) — primary. https://pacer.flmb.uscourts.gov/fwxflmb/training/support.htm
- **[S18]** FLMB, *Local Rule 1001-2* (amended eff. 2025-08-15), (b), (c), (g) and advisory note — primary. https://www.flmb.uscourts.gov/localrules/Rules/1001-2.pdf
- **[S19]** FLSB, *CM/ECF* — primary. https://www.flsb.uscourts.gov/cmecf
- **[S20]** TXEB, *Debtor and Creditor Attorneys* (e-services) — primary. https://www.txeb.uscourts.gov/e-services-attorneys
- **[S21]** TXWB, *Attorney Training Prerequisites* — primary. https://www.txwb.uscourts.gov/attorney-training-prerequisites ; *How to Become an ECF Filer* https://www.txwb.uscourts.gov/how-become-ecf-filer
- **[S22]** GANB, *CM/ECF Registration* — primary. https://www.ganb.uscourts.gov/cmecf-registration
- **[S23]** GANB, *CM/ECF Training Videos* — primary. https://www.ganb.uscourts.gov/cmecf-training-videos
- **[S24]** GANB, *CM/ECF Administrative Procedures* (Aug 2021), items 3–4 and requirements — primary. https://www.ganb.uscourts.gov/sites/default/files/cmecf_admin_procedures_08-2021.pdf
- **[S25]** GAMB, *Migration to CM/ECF NextGen* (Test and Train logins) — primary. https://www.gamb.uscourts.gov/USCourts/node/805
- **[S26]** GASB, *CM/ECF Training, User Guidelines/Manuals and Information* — primary. https://www.gasb.uscourts.gov/cmecf-training-user-guidelinesmanuals-and-information
- **[S27]** GASB, *CM/ECF Manual for Attorney Users* (Mar 2026), §3.a and Open BK Case chapters — primary. https://www.gasb.uscourts.gov/sites/gasb/files/CMECF%20Manual%20for%20Attorney%20Users%20Mar%202026.2.pdf
- **[S28]** GASB, *CM/ECF Registration Information* and *Attorney E-Filing Terms and Conditions* (2021-10-12) — primary. https://www.gasb.uscourts.gov/cmecf-registration-information · https://www.gasb.uscourts.gov/sites/gasb/files/GASB%20Attorney%20Filer%20Terms%20and%20Conditions%2010.12.2021.pdf
- **[S29]** FLSB, *Filing Agents* (Sept 2012) — primary. https://www.flsb.uscourts.gov/sites/flsb/files/documents/attorney/Filing_Agents.pdf
- **[S30]** GAMB, *Clerk's Instructions* (Dec 2024), Part I(A)–(B) — primary. https://www.gamb.uscourts.gov/USCourts/sites/default/files/local_rules/CLERKS_INSTRUCTIONS.pdf
- **Registry / ADR 0024** — the district records under `packages/insolvia_core/src/insolvia_core/regulatory/courts/us-bankruptcy/2026-10-06/districts/` (read 2026-09-24) and [ADR 0024](../adr/0024-electronic-filing-path.md) § *What the research established* (read 2026-09-23), cited above as "primary via registry / ADR 0024".
