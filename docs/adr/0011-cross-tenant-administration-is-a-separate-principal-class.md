# ADR 0011 — Cross-tenant administration is a separate principal class

- **Status:** Accepted — amended 2026-08-11: the portal acquires its ID token
  through Google Identity Services (Google's own sign-in button, in-page),
  not the authorization-code + PKCE redirect point 3 originally described.
  The first real sign-in surfaced why the original could never work: Google
  "Web application" OAuth clients require the client secret at the token
  endpoint even when PKCE is sent (`invalid_request: client_secret is
  missing.`) — a genuinely secretless code exchange is a Cognito capability,
  not a Google one. The server-side alternative (the admin service holding
  the secret and exchanging the code) was declined as a per-environment
  secret to provision and rotate for a flow GIS provides without one.
  Everything downstream of token acquisition is unchanged: the SERVICE still
  verifies Google ID tokens (issuer, audience, `hd`, `email_verified`), the
  portal still holds them in memory only, and no refresh token exists
  anywhere — the amendment moves how the token arrives, not what it is or
  how it is checked.
- **Date:** 2026-08-10
- **Relates to:** amends [ADR 0001](0001-client-stays-dumb-trust-boundary.md)
  (a second application principal now reaches a data store) and
  [ADR 0009](0009-a-case-belongs-to-a-firm.md) (the no-super-admin model and
  the no-firm-id-in-URLs rule both survive, by carrying their reasoning to a
  new principal class rather than weakening it); diverges from
  [ADR 0007](0007-hosted-ui-pkce-refresh-token-in-local-storage.md) for the
  staff client's token storage. Built on
  [ADR 0012](0012-shared-python-domain-package.md)'s shared package. Issues
  [#209](https://github.com/insolvia-ai/insolvia/issues/209),
  [#212](https://github.com/insolvia-ai/insolvia/issues/212), closing
  [#178](https://github.com/insolvia-ai/insolvia/issues/178).

## Decision

**Provisioning and administering firms is done by a new kind of caller — an
Insolvia staff member — through a separate service (`services/admin`), and a
staff member is authenticated by Google Workspace, not by anything this
repository operates.**

Four decisions in one, each of which could have gone otherwise:

1. **A separate principal class, not a super-admin.** ADR 0009's "an admin is
   an admin OF A FIRM" stands. Staff are not firm users with a flag; they are
   a different kind of caller entirely, verified against a different issuer.
2. **A separate service, not new routes in the tenant API.** The admin
   service's dependency (`insolvia_core`) contains the firm domain and
   nothing case-shaped, so "what may the admin surface reach" is answered by
   what its package exports, structurally. The tenant API remains a service
   that can only act within an accessor's firm.
3. **Google Workspace as the staff identity provider, directly.** The portal
   obtains a Google ID token in-page via Google Identity Services (a
   per-environment client marked Internal in the Workspace org; *amended —
   originally authorization-code + PKCE, see Status*); the service verifies
   Google **ID tokens**: issuer, audience, `hd = insolvia.ai`,
   `email_verified`. No staff Cognito pool exists.
4. **Every mutation writes an append-only audit row** naming the verified
   staff caller — #178's "record who provisioned what" — to a table the
   service holds `PutItem` and nothing else on.

## Context

Firms cannot create themselves: self-signup is off, `POST /v1/firm/users` is
behind `FIRM_ADMINISTRATION`, and the last-admin rule refuses the edit that
would empty a firm. All three are correct, and together they mean the first
firm is written from outside the tenant API — which for a year meant a CLI
seeder that refuses production by regex, and rightly so: provisioning a real
customer wants an audit trail (#178).

Two designs were built far enough to be rejected honestly:

- **A staff Cognito pool** (`modules/staff_auth`) was implemented, applied to
  a dev environment, and closed unmerged (PR #221): once the company had
  Google Workspace, a second credential system — staff passwords, TOTP
  enrollment, offboarding in two places — existed only to re-issue an
  identity Google already asserts with enforced 2-step verification.
- **IAM OIDC federation** (Google → `AssumeRoleWithWebIdentity` → SigV4 calls
  from the portal) was rejected without building: it puts AWS credentials in
  a browser, which is ADR 0001's foundational prohibition.

## The trust boundary

Two issuers, two principal classes, and the separation is structural rather
than checked:

| | Tenant API | Admin service |
|---|---|---|
| Verifies | Cognito **access** tokens (firm pool issuer, `client_id`, `token_use`) | Google **ID** tokens (`accounts.google.com`, `aud`, `hd`) |
| Caller resolves via | firm-user row (accessor resolution) | the verified claims alone |
| Can reach | one accessor's firm | every firm — audited |

A firm user's token dies in the admin service's verification (wrong issuer,
no `aud`, no `hd`) before any claim is trusted; there is no role check to
forget. The test suites pin this from both sides.

**Accepting ID tokens is a recorded deviation**: Google's access tokens are
opaque, so for a first-party, single-audience internal API the ID token is
the credential. The Cognito profile refuses ID tokens for reasons
(`token_use`) that do not transfer — there, access and ID tokens share keys
and only one is an authorization credential; here, the ID token is the only
verifiable artifact and its audience is exactly this service's client.

**The `hd` check is deny-side redundancy.** The OAuth client being Internal
means Google refuses non-Workspace accounts at sign-in; the service checks
`hd` anyway, so a console misconfiguration cannot admit a personal account.
Duplicated checks on the deny side cannot disagree in the dangerous
direction (the same argument `permission_for` makes for disabled users).

## Consequences

- **A second principal reaches the firm table** — the first exception to
  ADR 0001's single-application-principal shape that shares a data store.
  The admin role's grant includes `dynamodb:Scan`, which the API role
  deliberately lacks: cross-tenant listing stays impossible on the tenant
  hot path at the IAM layer.
- **Firm ids appear in admin URLs.** ADR 0009 rejected them for the tenant
  API because a firm id a client can set is one somebody will set wrongly —
  a *scope* claim. For a staff caller the id is the *object* of an audited
  operation; the threat the rule guarded against does not exist here, and
  pretending it did would leave the surface unable to name what it
  administers.
- **The API's one-action Cognito grant is unchanged.** The admin service's
  role gets its own `AdminCreateUser` on the firm pool (create + RESEND are
  the same action), so the recorded trigger in `modules/auth` — "this grant
  ever needing a second action" — is not tripped.
- **Staff tokens live in memory only** in the portal — no persisted refresh
  token. ADR 0007's localStorage trade bought attorneys 30 signed-in days;
  a high-privilege internal tool re-authenticating per session through an
  already-signed-in Google account costs seconds and removes the XSS-steals-
  refresh-token arm entirely.
- **Google Cloud Console is a small non-Terraform dependency**: three
  Internal OAuth clients (one per environment), a human console procedure.
  Client IDs are public values and live in config; there are no secrets.
- **Offboarding is disabling the Workspace account**, bounded by token
  lifetime (≤ 1 hour).

## Revisit when

- Staff stop being interchangeable (read-only staff, say) — the decision
  point is `staff_principal_from_claims`, not a permission table bolted onto
  routes.
- The admin surface needs anything case-shaped — that is a new conversation
  about `insolvia_core`'s boundary, not a quiet import.
