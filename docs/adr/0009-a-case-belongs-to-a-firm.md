# ADR 0009 — A case belongs to a firm, and membership lives in our own store

- **Status:** Accepted
- **Date:** 2026-08-05
- **Amends:** [ADR 0007](0007-hosted-ui-pkce-refresh-token-in-local-storage.md),
  whose *"server-side authorization decisions read only verified access-token
  claims"* this supersedes — see **Why not Cognito claims** below.
- **Relates to:** [ADR 0001](0001-client-stays-dumb-trust-boundary.md) (one
  trust boundary, enforcement in code);
  [`docs/reference/case-data-model.md`](../reference/case-data-model.md) owns
  the entities; `infra/modules/firm_store` and `infra/modules/case_store` own
  the keys.

## Context

Until this change a case belonged to **one human being**. `owner_principal` on
the case record was a Cognito `sub`, and `GSI1PK = OWNER#<sub>` was the list
path. If an attorney opened a matter and their paralegal signed in, the
paralegal got a **404** — not a permissions error, a 404, because the store
deliberately cannot distinguish "not yours" from "doesn't exist".

Two people at one firm could not work the same matter. For a bankruptcy
practice that is not a limitation, it is a non-starter: intake, document
collection and review are split between an attorney, a paralegal and a clerk
as a matter of routine.

**The business plan and the data model described different products.** The plan
sells per-seat licences to 2–15-seat firms at ~2.5 seats each; the data model
filed *"Firms, staff, and shared case access"* under **Not here, on purpose**.
No ADR or D-decision recorded the gap, and there was no GitHub issue for
firms, tenancy, roles or permissions — open or closed.

## Decision

**A case belongs to a FIRM, not to the person who opened it.** Reaching one
takes two conditions, and the whole rule lives in one expression
(`core/access.may_see_case`):

```
case.firm_id == accessor.firm_id
  AND (accessor.is_admin OR accessor.access_all_cases OR linked to this case)
```

`created_by` survives on the case as an **audit fact** — who opened the matter
— and grants nothing on its own.

### The vocabulary is MyCase's

Firm users with a **role**, an **admin** flag, per-case **linking** with an
all-cases switch, and per-feature permission **levels**. Deliberately: a firm
evaluating us should recognise what it is looking at, and MyCase is what most
of them are leaving. **Nothing here integrates with MyCase** — we adopted the
shape, not the API — and the feature list is ours, because theirs enumerates
billing, tasks and calendars, none of which exist here.

### Four independent axes, not one ladder

| | what it decides |
|---|---|
| `role` | **nothing.** It chooses the DEFAULT permission map at creation. |
| `is_admin` | every feature, every case, and the firm's user list. |
| `access_all_cases` | every case in the firm, without per-case linking. |
| `permissions` | per-feature `add_edit` / `view_only` / `hidden`. |

Keeping them separate avoids two specific traps. Folding role into access is
the one where *"attorney"* quietly comes to mean *"can see everything"* —
invisibly, because nobody reads a job title as a permission. Folding
`is_admin` into `access_all_cases` would mean the only way to give a
supervising attorney the whole caseload is also to let them change everyone's
permissions.

### Everything fails closed

A feature missing from a user's map is `hidden`. A level this version cannot
rank is `hidden`. A disabled user has no permissions at all, checked *above*
the admin branch. That is what lets `extraction_review` be listed before the
feature exists: it is invisible to everyone until someone grants it, rather
than arriving one deploy later as something every existing row could already
do.

## Why not Cognito claims

This is the part that amends ADR 0007, so it deserves the argument rather than
the conclusion.

The pool has **no groups, no custom attributes and no pre-token Lambda**. So
"which firm is this person in, and what may they do" is either added to the
token or looked up. We looked it up, for three reasons:

1. **A token outlives a revocation.** Access tokens are valid for an hour. A
   firm admin who revokes a colleague's access to documents expects that to
   take effect now, not within the hour — and "log out and back in" is not an
   answer you give the person who just fired someone.
2. **A pre-token Lambda is a second place the rule lives.** The API would still
   have to read the store for the administration endpoints, so claims would be
   a *cache* of an authoritative record, with all the staleness that implies
   and none of the simplification.
3. **The property ADR 0007 was protecting survives.** That ADR's sentence was
   guarding against a **client-supplied** identity. Nothing here takes one: the
   subject is verified from the signature, and everything derived from it is
   read server-side from a table the client cannot write. Authentication still
   reads only verified claims. Authorization reads a store, and the store is
   ours.

The cost is stated rather than hidden: **two extra reads on the hottest path**
in the service — one on the by-subject index for the firm user, one for the
firm itself. Both are small keyed lookups on the same table. Neither is cached,
and that is deliberate: a cache here means a permission an admin has just
revoked keeps working, which is the one place staleness is a security property
rather than a latency one.

The firm read exists so a **suspended** firm is actually suspended. The
alternative is a status field that lies, or bulk-disabling every user of a firm
that stops paying.

## What this costs elsewhere

**Two listing indexes, and which one a caller uses depends on their
permissions.** `by-firm` for admins and `access_all_cases`; `by-assignee` for
everyone else. They return different sets by construction, so a pagination
cursor minted against one is meaningless against the other — cursors therefore
carry the index they came from, and a permission change mid-pagination is a
**400** rather than a silent gap. This is the one genuinely awkward consequence
of the design and it is written into the code at both ends.

**Creating a case is a transaction.** The case and its creator's assignment,
together. A user without `access_all_cases` who opened a matter and was not
linked to it would have created a case they cannot see, cannot list and cannot
reach by id — indistinguishable, from outside, from the request having failed,
except that the id is taken.

**A firm cannot be left without an administrator.** Self-signup is disabled on
the pool (`allow_admin_create_user_only`), so a firm with no active admin
cannot appoint one — nobody inside it can fix it. Every edit that would produce
that state is refused with a **409**, which is not a permission failure and
must not be reported as one: it is being said to the person who currently
*has* the permission.

**The API can create Cognito accounts.** `AdminCreateUser` on one pool, and
nothing else — no password setting, no auth, no delete. Cognito emails the
temporary password to the invited address and nothing in the service ever sees
it, which is the difference between a provisioning grant and an impersonation
primitive. The narrower alternative (a separate invite Lambda) is recorded in
`modules/auth` along with the signal that should trigger it: this grant ever
needing a second action.

## Alternatives considered

- **Keep single-owner and add sharing later.** Rejected: every downstream
  feature — documents, intake, extraction review — would be built against an
  ownership model known to be wrong, and each would need the same rework.
- **A firm id in the URL** (`/v1/firms/<id>/users`). Rejected: a firm id a
  client can set is a firm id somebody will eventually set to a different one.
  Every endpoint takes the firm from the resolved accessor instead, so "may I
  administer this firm" has exactly one answer and it was already decided.
- **Groups in Cognito.** Rejected with the claims argument above, plus: groups
  are flat, and this model needs per-feature levels and a per-case link.
- **A separate tenancy service.** Rejected as premature. One trust boundary
  (ADR 0001), one API, one place the rule is written.

## Consequences

- `core/access.may_see_case` is the single definition of visibility. Both store
  implementations apply it; routes do not re-derive it.
- 404 covers three distinct facts — no such case, another firm's case, and an
  in-firm case the caller is not linked to. The third is new, and it is not
  only about enumeration: distinguishing it would tell any member of a firm
  which matters exist and which colleagues are on them.
- 403 is now a real response, for "signed in but not in an active firm" and for
  a per-feature refusal. It is deliberately *not* hidden behind a 404, because
  it is a fact about the caller's own account, with nothing to enumerate.
- `/v1/me` is the one route that resolves an accessor without requiring one. It
  reports the firm, or reports its absence, so a person who has signed up and
  not been added yet has something to render instead of an error screen.
- The pool's usernames are **case-insensitive**, and were not when this ADR
  was first written. Cognito's legacy default — an absent
  `username_configuration` — is case-SENSITIVE, so `Alice@firm.com` and
  `alice@firm.com` were two accounts and an attorney who typed a capital on
  the hosted sign-in page was told they did not exist. Measured against the
  real staging pool.

  It was fixed rather than accepted, and the timing was the whole argument.
  The block is immutable, so setting it REPLACES the pool — which deletes
  every account **and** orphans every row keyed on a Cognito subject:
  `firm_user`, `case.created_by`, every assignment. Prod had zero users and
  both those tables were empty, so the change cost nothing. After the first
  firm onboards it stops being a replacement and becomes a data migration.
  The lesson generalises: an immutable identity setting is cheapest to get
  wrong on the day you notice.
