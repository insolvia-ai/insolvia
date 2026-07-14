# Insolvia — Business Plan

_Last updated: 2026-07-14 · Status: draft v0.1_

## 1. Executive summary

Insolvia is a modern, **cross-platform (native desktop + web)** bankruptcy
case-preparation and e-filing platform for **consumer-bankruptcy law firms**. We
compete directly with the market incumbent, **Best Case by Stretto**, which is
used to prepare an estimated **~80% of the bankruptcy cases filed nationwide**
and whose customers have relied on its **desktop** software for years.

Our thesis is simple: **meet those attorneys where they are.** The market is
bifurcating into an aging desktop incumbent and cloud-only newcomers that ask
desktop-loyal firms to give up the fast, offline, keyboard-driven experience
they trust. Because Insolvia is built on a single **Dart/Flutter** codebase, we
ship a genuine native desktop app *and* a web app from the same source — no
"cloud-only regression," and no separately-maintained desktop and web products.

We win by pairing the reliability desktop users expect with modern intake,
a compliance-forward forms engine, transparent pricing, and painless migration
off Best Case.

## 2. Market & incumbent analysis

### 2.1 Best Case by Stretto (the incumbent)

Best Case positions itself as "the leader in Bankruptcy Case Prep and Filing."
Core capabilities we must match or beat:

- **Online client intake & Due-Diligence import** — a secure portal for remote
  intake; imported due-diligence data auto-populates forms, marketed as saving
  "an average of 60 minutes of data entry per case."
- **OneTouch™ electronic filing** — navigates the court's website, uploads the
  correct documents, and returns a case number + court receipt; also automates
  post-petition filing (e.g. Form 423) and the Chapter 13 Plan.
- **Court notices & calendar** — automatic PACER document downloads, calendar
  sync for bankruptcy events, and Proof-of-Claim tracking.
- **Credit-report import** — direct access to the three primary credit bureaus,
  importing data straight into the software.
- **Always-current forms & data** — Stretto continuously updates federal and
  local forms plus IRS and Census Bureau data to keep filings compliant.
- **Practice-management adjacencies** — docket/critical-date alerts, document
  management/generation, billing & invoicing, time & expense, trust accounting,
  client portal.

**Delivery & pricing.** Best Case ships as a **desktop** product and a newer
**cloud** product:

| Product | Price (indicative) |
|---|---|
| Best Case **Desktop** | ~$1,750–$4,500 / year, plus a maintenance fee (~$800–$2,700/yr) after year one |
| Best Case **Cloud** | ~$99 / month (30-day trial) |

**Strategic read.** The desktop base is large, sticky, and paying premium
annual + maintenance fees. It is also the base most exposed to disruption: the
UX is dated, pricing is opaque and high, and the cloud migration path asks users
to trade away the desktop experience. That is precisely our opening.

### 2.2 Competitive landscape

| Competitor | Model | Notes |
|---|---|---|
| **Best Case by Stretto** | Desktop + cloud | Incumbent; ~80% of filings; premium price; dated desktop UX. |
| **CINcompass** (also Stretto) | Cloud | Stretto's own cloud petition-prep + practice management; unlimited users. |
| **NextChapter** | Cloud-native | Leading modern cloud alternative; add-ons for texting, court notices, client portal, virtual paralegal. |
| **Jubilee** | Cloud/desktop | Petition prep + case management. |
| **LegalPRO** | Cloud | Petition prep & filing across Chapters 7/11/12/13, payments/invoicing. |
| **Filevine** | Cloud | Broad case management, not bankruptcy-specialized. |

**Where Insolvia is different:** everyone modern is cloud-*only*. Nobody offers a
first-class native desktop *and* web experience from one product. That is our
structural differentiation, not a feature checkbox.

## 3. Target customer & jobs-to-be-done

**Primary:** solo and small (2–15 seat) **consumer-bankruptcy** law firms and
their paralegals, handling **Chapters 7, 11, and 13**. Many are current
Best Case **desktop** customers frustrated by price and stagnation but wary of
cloud-only tools.

**Jobs to be done:**
1. Intake a client's financial picture quickly and accurately (remote-friendly).
2. Produce **compliant** petitions, schedules, and the means test with minimal
   re-keying.
3. **E-file** to the court (CM/ECF) and retrieve case numbers/receipts.
4. Track deadlines, court notices (PACER), and Proof-of-Claim events.
5. Bill, manage trust accounting, and keep matters organized.

The hair-on-fire job is **#2 + #3 with confidence in compliance** — errors here
cost real money and sanctions.

## 4. Positioning & differentiation

1. **One product, desktop *and* web.** A true native desktop app (fast, offline
   drafting, keyboard-driven) plus a web app for anywhere access — same data,
   same forms, no compromise. This is the "meet them where they are" wedge.
2. **Compliance-forward forms engine.** Versioned federal + local forms with a
   visible update cadence and change log, so attorneys trust that what they file
   is current.
3. **Modern intake.** A clean client portal + import that beats the incumbent's
   "60 minutes saved" claim.
4. **Transparent pricing.** No opaque desktop license + maintenance stacking;
   simple per-seat pricing with the desktop app included, not upsold.
5. **Painless migration.** First-class import from Best Case exports so switching
   is a weekend, not a quarter.

## 5. Product scope & module roadmap

Phased from MVP to v1. Each phase is independently valuable.

| Phase | Modules | Outcome |
|---|---|---|
| **P0 — Foundation** _(this repo, now)_ | Monorepo, design system, cross-platform hello-world app, CI/CD, AWS infra (staging + prod) | Deployable skeleton; desktop + web proven end-to-end. |
| **P1 — Intake** | Client portal, secure intake forms, data import (incl. Best Case import) | Firms collect client data remotely; data lands structured. |
| **P2 — Forms & petition engine** | Versioned form templates, schedules, petition assembly, PDF generation | Produce a compliant petition packet from intake data. |
| **P3 — Means test** | Chapter 7 means test with IRS/Census data | Automated eligibility calculation. |
| **P4 — E-filing** | CM/ECF integration, receipts/case numbers, post-petition automation | File to the court from within Insolvia. |
| **P5 — Notices & calendar** | PACER pull, deadline tracking, Proof-of-Claim, calendar sync | Never miss a court event. |
| **P6 — Practice management** | Billing/invoicing, time & expense, trust accounting | Run the practice, not just the filing. |

## 6. Go-to-market

- **Displacement play** timed to Best Case desktop renewal cycles: lead with
  price transparency + "keep your desktop workflow."
- **Migration tooling** as an acquisition lever (import Best Case data on day one).
- **Compliance trust** as the brand promise — publish the forms-update cadence.
- **Land small, expand:** start with solo/small firms (fast decisions), grow
  seats as trust builds.
- **Pricing strategy (to validate):** simple per-seat/month, desktop included,
  undercut Best Case's blended desktop+maintenance annual cost while beating
  NextChapter on the desktop dimension.

## 7. Risks & compliance

- **Forms accuracy** is existential — a wrong/stale form is a filed error.
  Requires a rigorous, versioned, auditable forms pipeline and update SLA.
- **CM/ECF & PACER integration** is technically and procedurally involved (court
  by court); scope carefully and stage rollout by district.
- **Data security & PII** — we handle SSNs and full financial profiles.
  Encryption in transit and at rest, least-privilege access, audit logging,
  and a clear data-handling posture are table stakes.
- **Regulatory/UPL** — the software assists attorneys; it must not stray into
  unauthorized practice or give legal advice.
- **Incumbent moat** — Stretto's forms/data pipeline and court relationships are
  real; our counter is UX, price, cross-platform, and migration ease.

## 8. Milestones (tie-in to engineering)

- **M0 (now):** Foundation repo — design system + cross-platform hello-world app
  + CI/CD + AWS infra authored (deploy gated on `insolvia.ai` DNS). _See the
  repo `README.md` and `docs/`._
- **M1:** `insolvia.ai` DNS live → staging deploy of the app at
  `staging.insolvia.ai`; signed/notarized macOS build.
- **M2:** Intake (P1) behind auth.
- **M3:** Forms & petition engine (P2) producing a compliant Chapter 7 packet.

---

### Sources

- [Best Case (bestcase.com)](https://www.bestcase.com/) and
  [Best Case pricing](https://www.bestcase.com/pricing/)
- [Best Case on Capterra](https://www.capterra.com/p/180363/Best-Case-Bankruptcy/) ·
  [GetApp](https://www.getapp.com/legal-law-software/a/best-case/) ·
  [Software Advice](https://www.softwareadvice.com/legal/best-case-profile/)
- [My Fresh Start Finance — bankruptcy software pricing](https://myfreshstartfinance.com/bankruptcy/software-for-attorneys)
- [TrueReview — bankruptcy law software (2026)](https://www.truereview.co/post/bankruptcy-law-software)
- [NextChapter — pricing](https://nextchapterlegal.com/bankruptcy/pricing) ·
  [NextChapter vs Best Case](https://nextchapterlegal.com/bankruptcy/competitor-comparison)
- [SoftwareWorld — bankruptcy software alternatives](https://www.softwareworld.co/competitors/bankruptcy-software-alternatives/)
- [ABA Journal — managing a bankruptcy practice](https://www.americanbar.org/groups/journal/articles/2021/manage-your-bankruptcy-law-practice-with-this-software/)

_Figures (pricing, "80% of filings," "60 minutes saved") are vendor/market
claims gathered from the sources above and should be independently verified
before external use._
