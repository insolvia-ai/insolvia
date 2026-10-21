# Single-tenancy assumptions: one firm, client, district or chapter (2026-10-21)

This note answers [#468](https://github.com/insolvia-ai/insolvia/issues/468)
on the [wayfinder map #467](https://github.com/insolvia-ai/insolvia/issues/467).
The map's Notes ask the model to "model for N, launch at one". The question is
where the current model assumes **one** firm, client, district or chapter, or
would break with many firms × clients × districts × Chapters 7/11/13. For each
finding it gives where it is, what breaks under N, and what it costs to reverse.

**This is a snapshot. It owns no fact.** Each finding is fixed in its owner: the
code, an ADR or `case-data-model.md`. Claims were checked against the tree at
`2870a73` (main, 2026-10-21). The history comes from
[`decision-status-2026-10-13.md`](decision-status-2026-10-13.md) and is not
re-derived here.

**Reversal cost** uses three grades:

- **code-only**: no stored row changes shape.
- **reshape**: stored rows must be re-keyed or rewritten.
- **reseed**: the data is thrown away and seeded again.

Prod data is disposable before launch, so today every *reshape* is really a
*reseed*. After the first real firm onboards, a reshape becomes a migration.

---

## Summary

- **The binding allows one firm per person.** The binding is keyed
  `(firm, subject)` and names exactly one case. The
  one-firm-per-person rule is enforced at two levels:
  - Cognito: a single shared pool means one email is one account across
    every firm.
  - Our code: invitations refuse an address the firm has never bound, and the
    by-subject resolver raises when a subject is bound in two firms.

  A debtor who changes counsel (common in Chapter 13) cannot be invited by the
  new firm. A revoked binding is kept forever, so if a second binding ever
  existed, every portal request would answer 500. ADR 0023 §3 says the binding
  "moves from the case to the client record" once ADR 0022 lands. 0022 has
  landed and the binding has not moved; nothing links it to the `FirmClient`.
  **Confirmed. Reshape.** (§1.1–1.3)
- **Staff can belong to one firm only, for the same reason** (shared pool plus
  the resolver's raise). An attorney of counsel at two firms, or a contract
  paralegal, cannot be added to the second firm. The rows already allow many
  firms per subject, so lifting this is **code-only**, though it touches every
  accessor. (§1.4)
- **No chapter is hard-coded to 7.** Both the packet gate and forms/ are
  chapter-aware for 7 and 13, and Chapters 11 and 12 are refused by name. Three
  soft spots remain:
  - `chapter_form_series` falls through to the Chapter 7 set for any chapter
    other than 13.
  - Case Upload hard-codes "not small business".
  - The means-test error says no form exists for Chapter 11 (Form 122B does).

  Chapter 11 is **not modelled** in `forms/`: there is no B104 and no B122B.
  The map's "modelled and checked against official forms" bar for Chapter 11
  is therefore unmet. **Partly confirmed. Code-only.** (§2)
- **The registry is keyed per district, but its state list and its opening
  facts are narrow:**
  - The loader refuses any district outside FL/GA/TX.
  - Each district's `opening` record (local forms, docket order, fee rule) is
    a single *Chapter 7 individual* record, with no chapter dimension. A
    Chapter 13 filing set therefore inherits the Chapter 7 checklist.

  The drivers are per court and carry the chapter. **District-in-drivers:
  dismissed. District-in-registry: confirmed for the state list and the
  chapterless opening.** Code-only plus new registry releases. (§3)
- **The `clients.py` / `firm_clients.py` collision is real but contained.**
  Sort-key prefixes keep the rows apart. "Client" has three meanings:
  - the portal binding;
  - the firm's person record;
  - the OAuth app client in `origin.client_id`.

  The `client.*` audit actions mix the first two. One code comment
  misdescribes where the binding's rows live. **Confirmed (naming). Code-only.**
  (§4)
- **The pool's case sensitivity is resolved.** The pool sets
  `case_sensitive = false` (insolvia-services). ADR 0009 records why and
  when, and both normalisers agree. `plan.md:172` still calls it open.
  **Dismissed. Stale text only.** (§5)
- **Seeds and fixtures prove one district and one chapter pair.** The fixtures
  hold two cases, both in M.D. Fla. Tampa. Dev has one firm. Staging has two
  firms, but the second has no cases. The "proven against many" bar is not met
  by the data the tests run on. **Reseed.** (§6)

---

## 1. Firms and clients

### 1.1 One live portal case per person per firm *(confirmed: reshape)*

The binding is **not** keyed to the case. Its authoritative row is
`PK FIRM#<firm_id> / SK CLIENT#<subject>`, carrying a single `case_id`
(`packages/insolvia_core/src/insolvia_core/clients.py:21-24, 300-317`). Only the
mirror sits under the case
(`clients.py:320-327`). The decision-status file described it as "keyed to the
**case** (`PK CASE#…`)" (`decision-status-2026-10-13.md:164`). That is half
right: the binding *names* one case, but it is *keyed* by person.

- `plan_binding` refuses a second live binding "anywhere in the firm". The
  rule is "One active case per client"
  (`clients.py:239-249`), and the store adds a transaction condition on top
  (`adapters/aws/client_binding_store.py:58-70`).
- Re-binding a revoked subject overwrites the firm-table row with the new
  `case_id` (`client_binding_store.py:60-68`). After that, only the old case's
  mirror records the earlier access. `case_copy` does not copy portal bindings
  (`docs/reference/case-data-model.md:221-222`).
- The seeder treats "this subject has any binding in this firm" as already
  present (`services/admin/src/insolvia_admin/entrypoints/seed.py:1140-1143`).
  A fixture that gives one client two cases would silently load only the
  first.

**Under N**, these cases break:

- A client with an open Chapter 7 case and a new related matter at the same
  firm.
- A debtor who is a joint debtor on one case and the sole debtor on another.
- A Chapter 13 case that is dismissed and refiled while the old case still
  needs documents. The firm must revoke the old binding first, and the portal
  then loses the old case.

ADR 0022's premise is that one client has many cases over time
(`firm_clients.py:1-5`). The portal does not follow it.

**Reverse:** re-key the authoritative row to `(firm, client_id)` or
`(firm, subject, case)`, and make portal resolution choose a case. Every
binding row is rewritten, so this is a **reshape**: a reseed today, a migration
after the first firm invites clients. No portal URL carries a case id
(ADR 0023 §3), so clients see no change.

### 1.2 The binding never reached the client record *(confirmed: reshape)*

ADR 0023 §3 says: "When **0022 (proposed)** lands, the binding moves from the
case to the client record"
(`docs/adr/0023-client-portal-identity-and-isolation.md:55-57`). ADR 0023 also
lists the move as something that "would reopen" it (`:248-249`).

ADR 0022 is built, but `ClientBinding` has no `client_id`
(`clients.py:92-112`), and ADR 0022 never mentions the portal (a grep of
`0022-a-client-is-not-a-case.md` finds no "portal" or "binding"). The portal
identity (a Cognito subject) and the firm's person (a `FirmClient`) are
unlinked. Two consequences follow:

- Staff cannot see "this client's portal login" across their cases.
- §1.1's one-case rule cannot be expressed as "one login, many cases of this
  client".

This was already flagged as decision-status inconsistency 13
(`decision-status-2026-10-13.md:255`) and is still untracked. **Reverse:** it
is the same reshape as §1.1, and should be decided together with it.

### 1.3 One firm per client, enforced twice *(confirmed: code-only to lift, but see 1.4)*

- **Invitation.** Cognito answers `UsernameExistsException` for an address
  that already has an account
  (`adapters/aws/user_directory.py:63-74`). The route then looks for a prior
  binding **in this firm only**, and returns 409 if it finds none
  (`services/api/src/insolvia_api/api/routes/portal_invitations.py:204-213`;
  the reasoning is at `:24-30`). A debtor who was a portal client at firm A
  can never be invited by firm B, even after A revokes them.
- **Resolution.** `ClientBindingStore.find` queries the by-subject index with
  `Limit=2` and raises "a client resolves to more than one firm"
  (`client_binding_store.py:206-223`; the port requires it at
  `ports.py:1094-1098`). `resolve_client` calls it on every portal request
  (`services/api/src/insolvia_api/api/client_auth.py:133`). Revoked rows keep
  their GSI keys (`clients.py:308-317`) and are never deleted (`clients.py:14-17`).
  So any second binding, live or revoked, turns every portal request for that
  person into a 500.

**Under N:** Chapter 13 debtors substituting counsel is the common case. In a
multi-firm world, a debtor with no counsel history is the exception, not the
rule.

**Reverse:** let `find` return all of a subject's bindings, and let resolution
pick the live one (or ask the client). Allow the invitation to attach an
existing pool account. The privacy argument at `portal_invitations.py:27-30`
("may be ... another firm's client") then needs a replacement, for example
email verification by the client. Lifting the raise is **code-only**. If §1.1
is re-keyed anyway, do this in the same change.

### 1.4 One firm per staff member *(confirmed: code-only)*

- Adding a colleague calls `create_user` first, so an email already in the
  pool returns 409 "already has an Insolvia account"
  (`services/api/src/insolvia_api/api/routes/firm.py:195`;
  `user_directory.py:72-74`).
- `FirmStore.find_user` raises when a subject resolves to two firms
  (`adapters/aws/firm_store.py:225-248`). The port calls this "One person, one
  firm is an application invariant — nothing in the key schema enforces it"
  (`ports.py:189-195`).
- The API (`services/api/src/insolvia_api/api/auth.py:217`) and the MCP
  service (`services/mcp/src/insolvia_mcp/api/auth.py:84-102`) both resolve the
  accessor through it, and a token carries no firm.
- The seeder refuses a subject that is both staff and a client
  (`seed.py:1135-1139`, "a subject is staff or a client, never both").

**Under N:** an attorney of counsel to two firms, or a contract paralegal,
cannot work for both. Neither can a paralegal who once filed their own case
through a client's portal.

**Reverse:** the rows are already `PK FIRM#<firm_id> / SK USER#<subject>`, so
many firms per subject need **no reshape**. The work is code-only but wide:

- the pool account must be attachable;
- firm selection must come per request (a header, or a firm picker);
- every accessor must resolve against the selected firm.

### 1.5 Benign single-valued firm defaults *(dismissed)*

A firm has one `default_court`, one `default_chapter`
(`firms.py:267-268`), and one signature block per user, with a single
`bar_number`/`bar_state` (`firms.py:201-214`). These are defaults copied onto a
per-case `filing_professional` that can be edited per case
(`firms.py:204-209`; `case-data-model.md:317-319`). Firms that span districts,
and attorneys admitted in two states, override them per case. This is not a
blocker. `_DEFAULT_CHAPTERS` (`firms.py:184`) repeats `cases.CHAPTERS`
(`cases.py:31`), which gives the chapter list two owners.

---

## 2. Chapters

### 2.1 The case and the deadline rules *(dismissed)*

`cases.CHAPTERS = (7, 11, 12, 13)` (`cases.py:31`). The deadline rules carry a
`chapters` list per rule, validated against the same set
(`services/api/src/insolvia_api/core/deadline_rules.py:55, 155-164`). The
2024-12-01 release has rules for every combination, including Chapter 11 and
Chapter 12 only. The chapter is a single, mutable integer (`cases.py:168, 572-574`).
Converting a case between chapters (§ 706, § 1307) overwrites it with no
history. That is worth a field once conversion is in scope, but it is not a
single-chapter assumption.

### 2.2 The packet gate *(dismissed as "hard-coded"; three soft spots)*

The gate refuses any chapter outside (7, 13) by name
(`services/api/src/insolvia_api/core/packet_assembly.py:761-771`). It holds
separate filing sets for Chapter 7 and Chapter 13 (`:158-221`), and it checks
Chapter 13's local plan form against the registry (`:717-746`). It is
chapter-aware, not chapter-7-bound. The soft spots:

- `chapter_form_series` returns `CHAPTER_13_FORM_SERIES if chapter == 13 else
  PACKET_FORM_SERIES` (`packet_assembly.py:236-239`). Any chapter other than
  13 gets the Chapter 7 set. Today the gate stops a Chapter 11 case first. A
  caller that skips the gate, or a future Chapter 11 that lifts it, gets B108
  and B122A silently. Make it a lookup that refuses an unknown chapter.
  **Code-only.**
- Case Upload's stat record writes field 17 as `"n"`, commented "Chapter 11
  small business only; never a case filed here"
  (`services/api/src/insolvia_api/core/case_upload.py:688`). That is true only
  while Chapter 11 is refused upstream. **Code-only.**
- `means_test.py:1354-1362` says "Forms 122A and 122C exist for Chapters 7 and
  13 only". Form 122B (Chapter 11) exists, and the data model says it "is not
  built" (`case-data-model.md:1057`). The message is wrong, and it is the
  natural place for a Chapter 11 branch. **Code-only.**

### 2.3 forms/ *(Chapter 11 not modelled: confirmed)*

`forms/specs/` holds B101, B106*, B107, B108, B113, B121, B122A-1/2,
B122C-1/2, B2010 and B2030. It has no Chapter 11 form: no B104 (the 20 largest
unsecured creditors) and no B122B. The specs that exist are chapter-complete,
because they are AcroForm ground truth. B101's spec carries all four chapter
boxes and the Chapter 11 small-business question
(`forms/specs/b101.json:90-121, 1563-1578`). The projections branch on the
form's own rules (`form_projections/b101.py:290-292`; `b106sum.py:68-70`;
`b122a2.py:356`; `b122c1.py:181`; `b122c2.py:152`).

The map's Notes say: "Chapters 11 and 13 are *modelled and checked against
their official forms and fields*". That holds for Chapter 13. For Chapter 11 it
does not, until B104 and B122B have specs and the gate's sets and the Case
Upload stat record have a Chapter 11 row. **Code-only and data.** No stored
case changes shape.

---

## 3. Districts

### 3.1 Drivers *(dismissed)*

There is one driver per court, by design
(`services/filing/src/insolvia_filing/core/drivers/base.py:1-27`).
`resolve_driver(court, …)` looks the court up in `VERIFIED_DRIVERS`, and an
unknown court hands the filing back (`base.py:151-166`). `CaseOpening` carries
`court, division, chapter, joint` (`base.py:60-72`). Only the fake driver
exists (PR 10 is missing, per `decision-status-2026-10-13.md` §1).
`services/filing/scripts/dev_filing_proof.py:117, 142, 165` names `flmb`, but
it is a dev proof script, not runtime code. Credentials are per
`(firm, attorney)` and hold a list of courts
(`packages/insolvia_core/src/insolvia_core/filing_credentials.py:172-189, 282-302, 309-320`).

### 3.2 The registry's state list *(confirmed: code-only plus data)*

The registry has one JSON file per district. Every release holds exactly the
ten FL/GA/TX districts
(`insolvia_core/regulatory/courts/us-bankruptcy/<release>/districts/`). The
loader refuses any other state, because `_STATE_FIPS = {"FL": "12", "GA": "13",
"TX": "48"}` (`courts.py:100`). The check is `state not in _STATE_FIPS` → "is
not a launch state (ADR 0017)" (`courts.py:685-686`).

Case creation refuses a court the registry does not know (`cases.py:427-432`).
A firm outside those three states therefore cannot open a case at all, not
only fail to file it. The same launch-state gate appears in three more places:

- exemptions: `LAUNCH_STATES` (`services/api/src/insolvia_api/core/exemptions.py:67,
  516-520`);
- the IRS local standards (`ust_data.py:28-31`);
- ADR 0017, still Proposed (`docs/adr/0017-launch-states-florida-texas-georgia.md:3-8, 75-78`).

**Reverse:**

- Replace `_STATE_FIPS` with the full state FIPS table (**code-only**).
- Add district records as new releases. The registry is append-only.
- Extend the exemption and local-standards series one state at a time.

No stored case changes shape.

### 3.3 The district `opening` record is Chapter 7 only *(confirmed: data)*

`Opening` holds `signature_instrument, ssn_statement, local_forms, fee_rule,
docket_order, file_names` once per district. Its comment calls the docket
order "the order a Chapter 7 individual opening's documents are docketed in"
(`courts.py:222-237`). ADR 0024 scopes it the same way: "local forms for a
Chapter 7 individual opening" (`docs/adr/0024-electronic-filing-path.md:80,
392-395`). The data says so too:

- S.D. Fla.'s LF-10 is "listed in the individual Chapter 7 petition package"
  (`…/2026-10-06/districts/flsb.json:74`).
- N.D. Fla.'s LF-2 is "not for an individual Chapter 7"
  (`…/flnb.json:101`).

The only chapter-specific district fact is `chapter_13_plan` (`courts.py:239-249,
283-287`).

`filing_set.py` builds every case's checklist from that one record, whatever
the chapter. It lists the local forms (`services/api/src/insolvia_api/core/filing_set.py:716-726`)
and orders documents by `opening.docket_order` (`:416-417`). Under Chapter 13,
districts add their own required local forms and fee rules, and the docket
order changes, because the plan is in it. The checklist would show Chapter 7
items and miss Chapter 13 ones.

**Reverse:** key `opening` by chapter (`opening: {7: …, 13: …}`) in a new
release, with the loader's `_predates` default covering old releases. This is
**code plus a registry release**, and the case shape does not change.

### 3.4 District facts outside the registry *(minor)*

- **Chapter 13 multipliers** are looked up by the *printed district name*,
  not the court code (`services/api/src/insolvia_api/core/ust_data.py:277-291`).
  `cases.py:136-140` explains why `district` survives as the printed name. It
  works, but it is a second join key that can drift. **Code-only.**
- **The exemption state** is the current residence state of Debtor 1
  (`services/api/src/insolvia_api/core/exemption_analysis.py:282-287`). The
  730-day domicile rule (§ 522(b)(3)(A)) is named as the analyzer's job
  (`exemptions.py:511-513`) but is not applied. A debtor who moved across
  state lines within two years gets the wrong scheme. **Code-only.**
- **District state holidays** (Rule 9006(a)(6)(C)) are deliberately not
  modelled (`core/legal_holidays.py:7-18`). Deadlines that fall on one are left
  to the preparer. The tool states this gap itself.
- **Stale doc:** ADR 0024 still says the matrix knobs live in
  `creditor_matrix.DISTRICT_VARIANCES` (`0024-electronic-filing-path.md:388-390`).
  They are now registry facts (`core/creditor_matrix.py:33-41, 151`).
- **Forward hazard:** `caseNumberKey` is court-scoped (`case_numbers.py:18-24`)
  and has no index yet. When court-notice intake (#369) adds a lookup by
  number, the index must be firm-scoped or tolerate two firms holding the same
  court case. Substitution of counsel produces exactly that.

---

## 4. The `clients.py` / `firm_clients.py` collision *(confirmed: naming; code-only)*

"Client" means three things in the package:

| Meaning | Where |
|---|---|
| The **portal binding**: a Cognito subject answering for roles on a case | `insolvia_core/clients.py` (`ClientBinding`, `CLIENT_ROLES`); firm table `SK CLIENT#<subject>`; case table `SK CLIENT#<subject>` (mirror); `client.invite` / `client.revoke` audit actions |
| The **firm's person** (ADR 0022) | `insolvia_core/firm_clients.py` (`FirmClient`); firm table `SK FIRMCLIENT#<id>`; debtor `GSI3PK CLIENT#<client_id>` (`cases.py:878-883`, `debtors.py:602`); access-log `PK CLIENT#<client_id>`; `client.create/read/update/merge` |
| The **OAuth app client** | `origin.client_id` on a portal candidate, "the portal app client" (`case-data-model.md:722`; `routes/portal.py:240`) |

The key prefixes are kept apart by construction. `firm_clients.py:24-39`
explains why its sort key is `FIRMCLIENT#` and not ADR 0022's `CLIENT#`: the
binding's `begins_with(SK, "CLIENT#")` listing would have returned firm
clients. No row can be misparsed today. Three smaller problems remain:

- The `client.` audit-action prefix is shared by both senses
  (`access_log.py:78-90`).
- `cases.client_key`'s docstring says the binding's `CLIENT#<subject>` rows
  "live in the firm table" (`cases.py:880-882`). The mirror lives in the case
  table under that sort key (`clients.py:23, 320-327`). The conclusion is still
  safe, because a GSI key is a different attribute, but the stated reason is
  wrong.
- `origin.client_id` (an app client) and `Debtor.client_id` (a `FirmClient`)
  have the same name on neighbouring records (`provenance.py:59, 107-111`).

Already logged as decision-status item 14 (`decision-status-2026-10-13.md:256`).
**Reverse:** rename `clients.py` to `portal_bindings.py`, and rename the
binding's audit actions to `portal.invite`/`portal.revoke`. That is
**code-only**, except that the audit-action strings already stored in the
access log keep their old names, and readers must accept both. Do it in the
same change as the §1.1 reshape.

## 5. Pool case sensitivity *(dismissed; stale text)*

The pool sets `username_configuration { case_sensitive = false }`, with
`username_attributes = ["email"]`
(insolvia-services `infra/modules/auth/main.tf:145, 149-171`, at `b9aac2b`).
The comment there records the measurement (`a@` and `A@` were two accounts on
staging) and the reason for doing it before launch: it is ForceNew, and it
orphans every subject-keyed row. ADR 0009 records the same decision
(`docs/adr/0009-a-case-belongs-to-a-firm.md:175-188`). The parser still
lowercases, so stored addresses are deterministic (`firms.py:643-649`), and the
adapter relies on the pool rather than repeating the normaliser
(`adapters/aws/user_directory.py:22-40`).

Only `docs/plan.md:172` still says "the pool's case sensitivity is a decision
rather than a task". `decision-status-2026-10-13.md:181` repeats it. Plan.md's
current risk 7 is unrelated (`plan.md:303`). The map's "Not yet specified"
bullet can close. **Nothing to reverse.**

## 6. What the tests run on *(confirmed: reseed)*

- `seeds/fixtures/v4/cases.json` has two cases, Chapter 7 and Chapter 13, both
  `court: "flmb"` (`:169-170, 376-377`).
- `seeds/dev.json` seeds one firm (`:48`).
- `seeds/staging.json` seeds two firms (`:52, 88`), but every fixture case goes
  to the first (`staging.json` `cases`). The second firm is the cross-tenant
  negative test, with nothing of its own to leak.

None of the data has a joint case with two portal clients, a second district,
a district with a local Chapter 13 plan form, or one person known to two
firms. **Reverse:** a new fixture version (`scripts/dev-fixture.sh`) and seed
entries. That is a **reseed**, by definition.

---

## Method

The research read:

- the root `CLAUDE.md`, `packages/insolvia_core/CLAUDE.md` and
  `docs/reference/case-data-model.md`;
- ADRs 0009, 0014, 0017, 0022, 0023 and 0024 (targeted sections);
- `insolvia_core`: `clients.py`, `firm_clients.py`, `firms.py`, `cases.py`,
  `courts.py`, `filing_credentials.py`, `case_numbers.py`, `access_log.py`,
  `ports.py` and the AWS adapters for bindings, firms and the user directory;
- the four registry releases under `regulatory/courts/us-bankruptcy/`;
- `services/api`: the portal and firm routes, `client_auth.py`,
  `packet_assembly.py`, `filing_set.py`, `case_upload.py`, `means_test.py`,
  `exemptions.py`, `exemption_analysis.py`, `ust_data.py`, `legal_holidays.py`,
  `deadline_rules.py` and the form projections;
- `services/filing`'s driver base, `services/mcp`'s auth, and `services/admin`'s
  seeder;
- `forms/specs/`, `seeds/`, and insolvia-services' `infra/modules/auth/main.tf`
  (read-only).

Line numbers are for `2870a73`.
