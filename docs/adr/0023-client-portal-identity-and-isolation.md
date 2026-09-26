# ADR 0023 — A client is a third principal class: one case, candidates only

- **Status:** Proposed
- **Date:** 2026-09-23
- **Relates to:** issue [#361](https://github.com/insolvia-ai/insolvia/issues/361)
  (15.1), building toward #362–#364. Sits inside
  [ADR 0001](0001-client-stays-dumb-trust-boundary.md)'s trust boundary;
  follows [ADR 0011](0011-cross-tenant-administration-is-a-separate-principal-class.md)'s
  precedent for a new principal class and
  [ADR 0016](0016-mcp-server-is-its-own-service.md)'s disjoint-client-id
  audience check; reuses the confirm-before-entry seam of
  [ADR 0013](0013-mcp-server-replaces-direct-pms-integration.md) and
  [`case-data-model.md`](../reference/case-data-model.md); inherits
  [ADR 0019](0019-ai-review-calls-anthropic-from-the-worker.md)'s ZDR gate.
  Coordinates with ADR 0022 (*a client is not a case*, proposed in parallel
  under #353) — referenced, not depended on.

## Decision

**A debtor using the portal is a third principal class — a `client` —
authenticated by the existing Cognito pool through a second public app
client, bound in our own store to exactly one case, and able to write nothing
but candidate records.** Six decisions, each of which could have gone
otherwise:

1. **Identity: the existing pool, a second PKCE app client
   `insolvia-<env>-portal`, no Cognito group.** The class is decided by the
   token's `client_id` at verification: the tenant API verifies exactly one
   client id (`insolvia_core.auth.principal_from_claims`), so a portal token
   already fails closed on every staff route, and a staff token fails on
   every portal route once those verify the portal id. The *authorization*
   input stays where ADR 0009 put it — a binding row in our store, never a
   claim.
2. **Invitation: two messages, one purpose each.** A firm user holding the
   new `client_portal` feature at `add_edit` posts
   `POST /v1/cases/<case_id>/portal/invitation {email, displayName}`. The API
   calls `AdminCreateUser` — the one Cognito action its role holds
   (`infra/modules/auth/main.tf`, `api_invite`) — and Cognito's own mail
   carries the temporary password, which nothing of ours ever sees. The
   mailer carries the context: which firm, what to do, the portal URL, and
   **no secret**. Expiry is the pool's 7-day temporary-password validity;
   re-invite is the same action with `MessageAction=RESEND`, as
   `services/admin` already does. The pool-level invitation copy
   (`locals.invite_email_message`) says "the platform your firm uses" and
   must become audience-neutral in the first PR.
3. **Binding: one keyed read, exactly one active case.** `AdminCreateUser`
   returns the `sub`; the API writes a `CLIENT#<subject>` item in the
   `firm_user` row's by-subject shape (`GSI1PK = USER#<subject>`,
   `insolvia_core.firms`) carrying `firm_id`, `case_id`, the lowercased
   email, `display_name` and `status ∈ invited | active | revoked`, plus a
   mirror `CLIENT#<subject>` in the case partition, in one transaction the
   way case creation writes its first assignment. `UsernameExistsException`
   resolves through our own prior binding by email (a refiled case); an
   address with no binding of ours is a **409**, never a guess. A subject
   with an active binding cannot be bound again until revoked. When
   **0022 (proposed)** lands, the binding moves from the case to the client
   record; no portal URL names a case id, so the move is server-side.
4. **Read policy: a fixed policy for the class, not the four axes.** ADR
   0009's axes describe staff; a client has no role, admin flag, caseload or
   feature map. The portal is a separate `ClientAccessor` type
   (`subject, firm_id, case_id`) that no function taking an `Accessor` can
   receive, and routes under `/v1/portal/*` that **carry no case id** — the
   case comes from the binding. A client reads the configured questionnaire
   sections, **their own** candidates (`origin.subject == self`) with review
   status, their document requests and their own uploads' metadata, the
   case's public status (chapter, stage) and the firm's name. Never: the
   confirmed case record, other candidates (extraction, MCP, the other
   person on a joint case), staff-uploaded documents or any download URL,
   notes, jobs, forms, packets, the firm's users, or the access log. Enforced
   by construction — portal routes read through projections that take the
   binding, not `CaseStore.get` — and pinned by an architecture test that
   every `/v1/portal/` route uses `@require_client` and no other route
   accepts the portal client id.
5. **Writes: candidates through the existing seam, provenance source
   `client`.** Every answer is `create_candidate` with
   `origin = {channel: "client", client_id, subject}` from the verified
   token (`ORIGIN_CHANNELS` gains `client`), validated by the same parse
   functions, reviewed through the same `extraction_review` routes.
   Acceptance mints provenance `source: client`, under the same
   unconfirmed-cannot-exist invariant as `ai_extracted` and `imported` (a
   `case-data-model.md` amendment in #363's PR). The client may edit or
   withdraw a candidate **while pending** (the proposer rule `withdraw`
   already has); reviewed rows are immutable and a change is a new
   candidate — which is also the questionnaire's resume state, so there is
   no second draft store. The queue shows staff "From the client", the
   display name from the **binding** (not the token), and the section and
   question answered (the candidate's `locator`, of a `question` kind).
6. **Documents: the same store, the same two-step upload, extraction
   gated.** `POST /v1/portal/documents` is `create_document` with
   `uploaded_by = subject`, `kind` from the request item it satisfies, the
   same presigned PUT and `complete` step, the same key
   `cases/<case_id>/<document_id>`. **Until ADR 0019's ZDR launch-checklist
   item is closed, a client upload is not auto-extracted**:
   `documents.py::_trigger_extraction` skips the `client` channel, and a
   firm user requests extraction explicitly through the jobs endpoint — an
   act by someone holding the engagement letter. A client cannot
   re-download, even their own upload, in v1.

## Context

Intake is staff-facing (#85 scoped a portal out). The debtor filling their
own questionnaire and uploading their own documents is the largest
paralegal-hours reduction available, and it admits a member of the public to
a GLBA-scope system for the first time. Three homes for that identity:

**The existing pool with a second app client — chosen.** Everything built is
reused: one issuer and JWKS provider per environment, the one-action invite
grant, the generated managed-login branding, the per-machine dev pool
`infra/envs/dev` already applies. The audience separation is ADR 0016's
pattern for MCP — a disjoint client id *is* the audience check. What it
forgoes: pool-level policy cannot diverge by audience — password policy,
`mfa_configuration`, sign-in factors and invitation copy are one setting for
attorneys and debtors alike. Acceptable at launch (12 characters, optional
TOTP, email recovery) and the recorded reason to reopen.

**A second pool.** Buys policy independence — passwordless email-OTP for
debtors only, debtor-worded invitations — and `api_invite`'s comment
anticipates "a future customer-facing one". It costs a second issuer in every
verifier, a second hosted domain (the custom-domain blocker in
[`terraform.md`](../reference/terraform.md) doubled), a second branding
document, and a second pool per developer machine. Rejected now; it is the
upgrade path.

**Magic-link sessions minted by the API.** The friendliest UX and the worst
posture: the API becomes an identity provider — signing keys in three
environments, rotation, revocation, brute-force protection, all of which the
pool does on ESSENTIALS — and the link is a bearer credential in a query
string, which `documents.py` already explains is copied into history, proxies
and screenshots, and which email forwards. Rejected.

**Cost.** Clients are MAUs on the same ESSENTIALS pool; a debtor signs in a
handful of times per matter, so growth is bounded by active matters.
ESSENTIALS has no threat protection — compromised-credential detection and
adaptive authentication are PLUS, per MAU — and a public-facing class is the
first real argument for paying it. Deferred, trigger below.

**Service placement.** ADR 0011 and 0016 gave a new principal class and a
new protocol their own services. This one gets routes inside `services/api`:
same protocol, every collaborator (candidate, document, blob stores, access
log, mailer client) composed there, and `insolvia_core` now exports the whole
case domain, so a separate service buys no structural narrowing. The
`ClientAccessor` type and `@require_client` are the seam a later split cuts
along.

## Threat model

| Threat | Posture |
|---|---|
| Portal token stolen | Portal sessions are **memory-only** — no refresh token in `localStorage` (ADR 0011's choice, not ADR 0007's): a debtor's device is the least controlled in the system, and the questionnaire resumes server-side so a lost session costs one sign-in. Portal client: 1 h access, 1-day refresh. A stolen access token reaches one case's projection for up to an hour; it reads no confirmed case data and downloads nothing. |
| Invite link forwarded | Our mail carries no secret. Cognito's carries a 7-day temporary password that forces a change at first use — used by someone else, it locks the real recipient out visibly and the firm re-invites. |
| Client of firm A guesses firm B's case id | No portal URL carries a case id; the binding is the only path. Nothing to guess, no id oracle to probe. |
| Staff impersonating a client | No role holds `AdminSetUserPassword` or `AdminInitiateAuth`; a portal token exists only through managed login with the client's credential. `origin` comes from the verified token, never a body. Staff see a client's answers; they cannot write as one. |
| A revoked client | The binding flips; the next request resolves no active binding, answers 403 and logs `denied`. Bounded by the 1 h access token. |
| Credential stuffing at the door | ESSENTIALS: no adaptive authentication. Revisit PLUS when the first real firm invites its first real client. |

**Audit.** Every portal request writes an access-log row with the client's
subject as principal: `portal.read`, `portal.answer`, and the existing
`document.create`. Staff reads of client-sourced data ride the rows they
already write (`extraction.read`, `document.*`); invitation and revocation add
`client.invite` / `client.revoke` on the case. The table stays append-only to
every service (`insolvia_core.access_log`), the client included.

## Consequences

- **Three environments.** `modules/auth` gains the portal client
  unconditionally, so dev, staging and prod get it on their next apply; env
  outputs publish `auth_portal_client_id` into SSM beside `auth_web_client_id`,
  the deploy workflow derives `AUTH_PORTAL_CLIENT_ID`, the app reads
  `EXPO_PUBLIC_COGNITO_PORTAL_CLIENT_ID`. **Local:** the per-machine pool's
  managed login at `localhost:3000/portal` — no stub. `seeds/dev.json` and
  `seeds/staging.json` each gain one client bound to the fixture case; the
  seed loader already creates accounts and sets passwords with the
  developer's or CI's own credentials, so a seeded client needs no invite
  mail, and the integration tier signs the client in over SRP
  (`tests/integration/cognito_srp.py`) against the portal client. Mailpit
  shows the invite mail locally. **Staging** runs the e2e flow as the seeded
  client. **Prod** is identical in shape, gated by the sequencing below.
- **Launch sequencing, in order:** SES production access
  ([runbook](../runbooks/ses-production-access.md)) before the first real
  invite — the account is in the sandbox, so today an invite reaches only a
  `.test` address; the ZDR item before client uploads are auto-extracted;
  the custom auth domain ([`terraform.md`](../reference/terraform.md)
  § Auth) before a debtor is routinely sent to an `amazoncognito.com`
  hostname — AWS's own trust guidance, with more force for a member of the
  public than for an attorney.
- **`client_portal` joins `FEATURES`** (`insolvia_core.firms`), hidden by
  default per ADR 0009's list-before-build rule; the seeds grant it.
- **Plan risk 6 is inherited, not solved.** Provisioning a firm is hand-run;
  provisioning a client becomes the firm's own audited act through the API,
  but a firm with nobody holding `client_portal` cannot invite, and only its
  admin can fix that.
- **The app gains a `(portal)` route group** with its own memory-only
  session provider, guard and shell — a second `SessionProvider`, not a
  parameter on the first, because the two differ in exactly the property
  (persistence) ADR 0007 spent an ADR on.

## Build breakdown

| # | PR | Size | Done when |
|---|---|---|---|
| 1 | **infra+core+api (#361, build half): the portal client and the `client` principal.** Portal app client in `modules/auth` (three envs), SSM/env plumbing, verify profile + `@require_client` + `ClientAccessor`, the binding store, invitation create/revoke/resend routes, `client_portal` feature, `ORIGIN_CHANNELS += client`, access-log actions, mailer invite message, `GET /v1/portal/me`, the disjointness architecture test, seeded clients. | M | On dev and staging: the seeded client's portal token reaches `/v1/portal/me` and 401s on `/v1/me`; a staff token 401s on `/v1/portal/me`; a revoked binding 403s with a `denied` row. |
| 2 | **app (#361, build half): the `(portal)` route group.** Portal sign-in, callback, memory-only session, guard, a landing screen with the case's public status; firm-side "Invite client" panel on the case overview behind `client_portal`; an e2e flow as the seeded client. | M | Staging e2e: the seeded client signs in and sees their firm's name and the case's stage, nothing else. |
| 3 | **core+api+app (#362): firm-configurable questionnaire.** Section catalogue (personal information always on; property, debts, income, expenses, other switchable), firm-level config with instructions and reset-to-defaults, staff UI on the firm screen, `GET /v1/portal/questionnaire`. | M | A staging firm switches a section off and its client no longer sees it, while staff still can. |
| 4 | **core+api+app (#363): the questionnaire, writing candidates.** Question-to-field catalogue, `POST/PUT/DELETE /v1/portal/answers` (edit-while-pending), the review queue's client origin and question, acceptance minting `source: client`, `case-data-model.md` amended. | L | Staging: an invited client completes the questionnaire and a paralegal confirms answers through the review queue; no path from the client into case data skips review. |
| 5 | **core+api+app (#364): document request checklists.** Firm-defined lists with the shipped default, per-case request items with status, `POST /v1/portal/documents` against a request, `complete` satisfies the request, auto-extraction skipped for the `client` channel, progress on the case overview. | M | Staging: a case shows requested documents arrived vs outstanding, and a client upload satisfies its request. |

**Risks.** SES sandbox and the ZDR gate bound real use. One invitation
template serves two audiences. ESSENTIALS has no threat protection for a
public class. Whether managed login offers a first factor per app client
(email OTP for debtors only) is unverified and decides whether passwordless
reopens the second pool. #363's catalogue is the largest and least bounded
surface.

## What would reopen this

Pool-level policy that must differ for debtors (passwordless, a different
password policy); PLUS-tier threat protection becoming a condition of a
firm's engagement; portal traffic or cadence diverging enough to want its own
Lambda (the `ClientAccessor` seam is the cut); or 0022 landing with a client
record the binding should own instead of a case.
