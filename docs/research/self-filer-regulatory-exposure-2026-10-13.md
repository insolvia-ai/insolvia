# Self-filer regulatory exposure — 2026-10-13

A first pass at one question, asked before charting the individual-first
pivot: **if a debtor pays Insolvia and files their own bankruptcy with no
attorney, what does federal and state law allow the product to do?** The
working term for that debtor is **self-filer** — a debtor who uses Insolvia
without an attorney of record (proposed, not yet in a glossary).

**This is a snapshot, not an owner of any fact, and it is not legal advice.**
It was gathered in one session from public sources; each claim below is
marked by how it was checked (see [Sources](#sources)). Its findings feed the
regulatory-posture decision on the pivot's map, which should be settled
against a written opinion from a bankruptcy / unauthorized-practice attorney,
not against this file.

---

## 1. Summary

- **The pivot is not dead, but its most natural shape is the most exposed
  one.** A *paid*, non-attorney product that *guides* a self-filer's legal
  choices is very likely a **bankruptcy petition preparer** under
  11 U.S.C. §110, and its guidance features risk **unauthorized practice of
  law** (UPL) under state law.
- **Software can be a petition preparer.** *In re Reynoso* (9th Cir. 2007)
  held a web-based petition-software seller to be one [S1].
- **Payment is the §110 trigger.** A Maryland bankruptcy court held in 2022
  that Upsolve's *free*, donor-funded tool was not a preparer because it took
  no compensation [S4, S5 — secondary].
- **Advice is UPL even when free.** The same Maryland court reportedly held
  that one Upsolve feature — filling in an exemption statute for the user —
  was UPL under Maryland law [S5 — secondary, not confirmed in the opinion].
- **What a preparer may not do removes Insolvia's differentiators**: the
  exemption engine (ADR 0017's data), Claude's petition review (ADR 0019),
  filing on the debtor's behalf (ADR 0024) and handling the court fee
  (ADR 0024 PR 11) [S2].
- **Postures that avoid §110 exist** (§5): an attorney makes the legal
  judgments; the debtor pays nothing; or a non-lawyer-owned legal entity
  where a state allows one. The first makes firms the *delivery channel* for
  the individual-first product, which bears directly on the "one cohesive
  model" goal.

---

## 2. §110 — the federal petition-preparer statute

Read from the statute text [S2].

| Provision | What it says |
|---|---|
| §110(a)(1) | A preparer is a person, other than an attorney or an attorney's supervised employee, "who prepares for compensation a document for filing". |
| §110(b)–(c) | The preparer signs every document, prints name and address, and adds the SSN of the individual (or the responsible officer of an entity). A not-an-attorney notice on an official form is signed and filed with the petition. |
| §110(e)(1) | The preparer may not execute any document on the debtor's behalf. |
| §110(e)(2) | No legal advice, including: whether to file, or which chapter; whether debts will be discharged; whether the debtor keeps a home, car or other property; tax consequences; reaffirmation; how to characterise property or debts; bankruptcy procedures and rights. |
| §110(f) | May not use the word "legal" in advertising. |
| §110(g) | May not collect or receive the court filing fee. |
| §110(h) | A maximum fee may be set by Supreme Court rule or Judicial Conference guideline; fees over the value of services, or over a guideline, are disallowed and turned over. Fees received in the prior 12 months are disclosed under penalty of perjury. |
| §110(i) | On motion, the preparer pays the debtor actual damages, the greater of $2,000 or twice the fee, and attorneys' fees; +$1,000 when a trustee or creditor moves. |
| §110(j) | Injunctions, up to a bar from acting as a preparer at all. |
| §110(k) | Nothing in §110 permits conduct that is otherwise the unauthorized practice of law. |
| §110(l) | Fines up to $500 per failure; **tripled** where the preparer's identity was concealed. |

**Fee caps in practice** are set district by district. The District of
Maryland's preparer page states a presumptive maximum of $125 [S6 — seen in
a search excerpt, page not opened]. A compliant preparer is, in effect, a
typing service.

## 3. *In re Reynoso*, 477 F.3d 1117 (9th Cir. 2007)

Read from the opinion text [S1]; consistent with the ABI summary [S3].

- **Facts.** Frankfort Digital Services sold access to web software for
  $219. The debtor entered data in dialog boxes; the software generated a
  complete petition. The forms omitted the preparer's identity and implied
  the debtor prepared them alone.
- **Preparer holding.** A question of first impression in the Ninth Circuit.
  The software was a preparer: it "translated that information into
  responses to questions on the bankruptcy forms" and determined where text
  went. The customer supplying data and printing did not change Frankfort's
  role. The $219 was the compensation.
- **UPL holding (California law).** The features went beyond typing: the
  software selected exemptions, supplied legal citations, was marketed as an
  "expert system" and as comparable to a top bankruptcy lawyer, and gave
  advice tailored to each customer.
- **Limits the court put on itself.** Footnote 9 expresses no view on
  whether software alone would be the practice of law. It repeats that
  "merely clerical" preparation is not the practice of law and that a
  preparer may offer typing services.
- **Remedies.** Fines under §110(b), (c), (f); disgorgement of every fee
  from debtor-customers in the Northern District of California for the prior
  twelve months; a permanent injunction from acting as a preparer in that
  district.
- **Caveat.** The opinion applies the pre-2005 (pre-BAPCPA) Code. §110's
  core definition is unchanged in the current text [S2], but the opinion's
  cites should not be reused unchecked.

Earlier Ninth Circuit authority it relies on: *In re Crowe* (BAP 2000; a
book author who promised to complete petitions was a preparer) and *In re
Doser* (2005; no in-person contact needed) [S3].

## 4. Upsolve

Two separate proceedings; neither binds Florida, Texas or Georgia.

**Maryland bankruptcy court, 2022 — *In re Peterson* / *In re Crawford*
(Bankr. D. Md., Nos. 19-24045, 19-24551; Judge St. John, June 1, 2022).**
The court ordered Upsolve to show cause in January 2020 [S4]. Per a
secondary summary [S5]:

- Upsolve was **not** a §110 preparer: it received no direct or indirect
  compensation; its funding was charitable.
- Most of the tool's functions were **not** the practice of law; its
  "Exemption Editor" **was** UPL under Maryland law, on the reasoning that
  software which narrows a user's options by the user's characteristics is
  giving advice. The summary cites *Jansen v. LegalZoom.com* (W.D. Mo.).
- Upsolve's tool screened out complex cases, had users acknowledge that it
  acts at their direction, and had an attorney review the forms.
- Upsolve had agreed to suspend the tool in Maryland pending the outcome.

The summary [S5] contains visible errors (it misnames the case), and the
116-page opinion [S4] was located but **not read**. Confirm the exemption
holding and the relief from the opinion before relying on them. Upsolve's own
Maryland page says the free filing tool is not available there [S7].

**New York, *Upsolve, Inc. v. James* (2d Cir., Sept. 9, 2025).** A First
Amendment challenge to New York's UPL statutes, about non-lawyer help
answering debt-collection suits — not bankruptcy filing. The Second Circuit
vacated the preliminary injunction that had protected Upsolve's program,
holding the statutes content-neutral and remanding for intermediate scrutiny
[S8, S9]. A certiorari petition is pending as No. 25-948 (a Cato amicus was
filed 2026-03-12) [S10]; its status was not checked.

## 5. Postures that avoid or reduce the exposure

| Posture | How it avoids §110 / UPL | What it costs |
|---|---|---|
| **A. An attorney makes the legal judgments** (full or limited-scope representation through firms) | Attorneys are outside §110(a); their advice is lawful | Non-lawyer ownership and fee-sharing rules (ABA Model Rule 5.4 and state equivalents) limit how Insolvia can take the debtor's payment and pay attorneys — needs a deliberate legal structure. Not researched here. |
| **B. The self-filer pays nothing** (Upsolve's posture) | No compensation, so not a preparer [S5] | Revenue from elsewhere. State UPL still bites on advice features (the exemption editor) [S5]. |
| **C. Forms software only** — the debtor makes every legal choice | Clerical work is not the practice of law [S1] | If paid, still a preparer under *Reynoso* [S1]: fee cap, signature, no advice. Texas's software carve-out (Tex. Gov't Code §81.101(c), added 1999 after the Quicken Family Lawyer case, for products that state they are not a substitute for an attorney) reaches only **state** UPL, not federal §110 [S11, S12]. |
| **D. A non-lawyer-owned legal entity** (Arizona's alternative business structures, Utah's sandbox) | The entity may lawfully provide legal services | Unknown whether it reaches debtors in FL/TX/GA bankruptcy courts. Not researched here. |

Posture A is worth naming for the model question: if the safe individual-first
product is "a self-filer buys Insolvia and gets an attorney through it", firms
are the delivery channel, and a model where self-filers and firms are both
first-class is a legal requirement, not only a design preference.

## 6. Bearing on existing decisions

| Decision | Exposure if paid Insolvia is a §110 preparer |
|---|---|
| ADR 0017 (FL/TX/GA exemption data) | Choosing or characterising exemptions is listed advice (§110(e)(2)) and was UPL in *Reynoso* [S1] and, reportedly, Maryland [S5]. Texas and Florida are in the 5th and 11th Circuits; *Reynoso* is persuasive there, not binding. |
| ADR 0019 (Claude reviews the petition) | Tailored review of a self-filer's petition is advice-shaped. |
| ADR 0024 (Insolvia files) | §110(e)(1) bars executing documents for the debtor; the attorney CM/ECF login it rides on does not exist for a self-filer. |
| ADR 0024 PR 11 (court fee) | §110(g) bars a preparer from collecting the filing fee. |

## 7. Not checked — open threads

- **Later treatment of *Reynoso*.** No citator check (KeyCite / Shepard's)
  was run; no post-2007 appellate software-preparer decision was found.
- **The Maryland opinion itself** [S4] — exemption holding and relief.
- **Fifth and Eleventh Circuit** preparer case law, and FL/TX/GA UPL law
  beyond Texas's carve-out. *Florida Bar v. TIKD Services* (Fla. 2021, a
  traffic-ticket app held to be UPL) is recalled, not verified.
- **U.S. Trustee Program enforcement** against online services today. DOJ
  publishes preparer enforcement releases (e.g. an Oregon injunction and
  fines) [S13]; dates and any online-service targets were not checked.
- **How paid DIY bankruptcy sites position themselves** today, and whether
  they register as preparers.
- **Posture A's structure** (Rule 5.4, referral-fee rules) and **posture D's
  reach** into federal bankruptcy courts.
- **Pro se electronic filing** — which districts accept a self-filer's
  petition electronically — is a separate question, not covered here.
- **Current text of Tex. Gov't Code §81.101(c)** — only the 1999 enrolled
  bill was seen [S11].

---

## Sources

Marked **primary** (the law or the opinion, read), **primary, not read**
(located, not opened or not parsed), or **secondary** (a summary of the
primary; checked only as far as noted).

- **[S1]** *In re Reynoso*, 477 F.3d 1117 (9th Cir. 2007), opinion text —
  primary. https://law.resource.org/pub/us/case/reporter/F3/477/477.F3d.1117.04-17190.html
- **[S2]** 11 U.S.C. §110, Cornell LII — primary.
  https://www.law.cornell.edu/uscode/text/11/110
- **[S3]** American Bankruptcy Institute, "Can Software Be a Bankruptcy
  Petition Preparer" — secondary.
  https://www.abi.org/member-resources/blog/can-software-be-a-bankruptcy-petition-preparer
- **[S4]** *In re Peterson* / *In re Crawford*, Bankr. D. Md. (June 1, 2022),
  memorandum opinion on govinfo — primary, not read (PDF could not be parsed
  in-session). https://www.govinfo.gov/content/pkg/USCOURTS-mdb-1_19-bk-24045/pdf/USCOURTS-mdb-1_19-bk-24045-0.pdf
  · companion docket: https://www.govinfo.gov/content/pkg/USCOURTS-mdb-1_19-bk-24551/pdf/USCOURTS-mdb-1_19-bk-24551-0.pdf
- **[S5]** Relativity, "Data Law Update: Can Software Engage in the
  Unauthorized Practice of Law?" — secondary; misnames the case.
  https://www.relativity.com/blog/data-law-update-can-software-engage-in-the-unauthorized-practice-of-law/
- **[S6]** U.S. Bankruptcy Court, D. Md., "Bankruptcy Petition Preparers" —
  primary, seen in a search excerpt only.
  https://www.mdb.uscourts.gov/pro-se/bankruptcy-petition-preparers
- **[S7]** Upsolve, "How To File Bankruptcy for Free in Maryland" —
  secondary (the party's own page). https://upsolve.org/md/
- **[S8]** Public Citizen Consumer Law & Policy Blog, "Second Circuit Vacates
  Preliminary Injunction…" — secondary.
  https://clpblog.citizen.org/second-circuit-holds-unlawful-practice-law-can-apply-to-debt-collection-advice/
- **[S9]** Law360, "2nd Circ. Allows NY AG To Curb Nonprofit's Debtor
  Coaching" — secondary (headline and excerpt only).
  https://www.law360.com/pulse/articles/2385874
- **[S10]** Cato Institute amicus brief, *Upsolve v. James*, No. 25-948
  (2026-03-12) — primary, filing located, not read.
  https://www.supremecourt.gov/DocketPDF/25/25-948/400716/20260312132759863_Upsolve%20v.%20James_Final.pdf
- **[S11]** Texas H.B. 1507, 76th Leg. (1999), enrolled text adding
  §81.101(c) — primary, via search excerpt.
  https://capitol.texas.gov/tlodocs/76R/billtext/html/HB01507E.htm
- **[S12]** *Unauthorized Practice of Law Comm. v. Parsons Technology*, 179
  F.3d 956 (5th Cir. 1999) — primary, via search excerpt (injunction vacated
  in light of the amended statute).
  https://law.resource.org/pub/us/case/reporter/F3/179/179.F3d.956.99-10388.html
- **[S13]** U.S. Department of Justice, "U.S. Trustee Program enforcement
  leads to injunctions, fines against bankruptcy petition preparers" —
  primary (agency release), date not checked.
  https://justice.gov/opa/pr/us-trustee-program-enforcement-leads-injunctions-fines-against-bankruptcy-petition-preparers
