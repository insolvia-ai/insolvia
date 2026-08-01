# ADR 0007 — Hosted-UI PKCE, tokens in memory, the refresh token in localStorage

- **Status:** Accepted
- **Date:** 2026-08-01
- **Relates to:** issue #75 (7.1, the `Product · Auth` milestone); sits inside
  [ADR 0001](0001-client-stays-dumb-trust-boundary.md)'s trust boundary; scoped
  to web by decision D9 in `docs/plan.md` /
  [ADR 0004](0004-react-native-replaces-flutter.md).
  [`docs/reference/terraform.md`](../reference/terraform.md)'s *Auth* section
  owns the Cognito module's configuration and
  [`docs/reference/architecture.md`](../reference/architecture.md) owns the
  environment and hosting models — this ADR decides only what the client does
  with what that seam already provisions.

## Decision

**The app signs in through the Cognito hosted UI using the OAuth 2.0
authorization-code grant with PKCE (S256), as a public client with no client
secret.** This is a confirmation, not a new choice: the seam in
`infra/modules/auth/main.tf` already provisions exactly this shape. One sharp
edge that file's own comment records governs the client, though — **Cognito has
no server-side "require PKCE" toggle.** It honours a `code_challenge` when one
is sent and cannot enforce that one is. Sending PKCE is therefore a property of
the client implementation, which means it is something the app's tests must
pin, not something the infrastructure guarantees.

**Access and ID tokens live in memory only** — a module-scope closure, never
written to any persistent store. **The refresh token is persisted in
`localStorage`.** That second half is the deliberate, eyes-open trade-off of
this ADR. It buys the "stay signed in" window the pool was configured for
(`refresh_token_validity = 30`, days) and it means a page reload does not bounce
the user through `/authorize`. The cost, stated plainly: **any successful XSS in
the app can exfiltrate a credential valid for up to 30 days against GLBA-scope
data.** Memory-only storage is the more secure option and it was rejected on UX
grounds — see *Alternatives considered*.

**The app refreshes the access token from the stored refresh token** when it is
expired or near expiry: proactively on a small clock skew, and reactively on a
401 from the API. Refresh goes through the hosted domain's token endpoint as an
OAuth `refresh_token` grant, not through a Cognito SDK auth flow — the app
client deliberately does not permit `ALLOW_REFRESH_TOKEN_AUTH` as an explicit
flow, because Cognito rejects that flow outright when refresh-token rotation is
enabled. Rotation returns a new refresh token, which replaces the stored one.
When a refresh fails — token expired, revoked, or rotation-rejected — the
session is cleared and the user is sent to sign-in.

**What the client sends the API is the access token, as
`Authorization: Bearer` — never the ID token.** The API verifies the signature
against the pool's JWKS plus `iss`, `token_use == "access"`, `client_id` and
expiry. The two tokens are not interchangeable spellings of "the user": the ID
token is an assertion *about* the user issued *to* the client, and is the right
artifact for the client to read display identity from; the access token is the
artifact minted to authorize API calls. This has one practical consequence
worth knowing before the first endpoint lands: because the pool uses
`username_attributes = ["email"]`, the access token's `username` claim is a
Cognito-generated UUID and carries no email at all. So **`GET /me` returns
claims-derived identity only, and the app renders the user's email from the ID
token it already holds.** Server-side authorization decisions read only
verified access-token claims — ADR 0001's client stays dumb, and a client-sent
identity is an input, not a fact.

**Sign-out does both legs, always.** Clear the in-memory tokens, remove the
persisted refresh token, then redirect to the hosted UI's `/logout` endpoint.

**Web is the only shipping target** (D9 / ADR 0004). A future native client is
out of scope here; it would need its own Cognito app client with its own
redirect registration, and `infra/modules/auth/main.tf`'s header comment
already specifies which kind and why.

## Context

The infrastructure for auth has existed since issue #65 and nothing consumes
it: pools, hosted domain and app client are provisioned per environment, the
API does not verify tokens, and the app does not sign in. What was never
decided is the part that lives entirely in the client — where tokens rest
between requests — and that is the part with a security cost attached, so it is
the part that gets re-litigated if nobody writes down what was traded for what.

The constraint that shapes the answer is the hosting model. The app is a
static, client-rendered SPA on S3 behind CloudFront, compute-free by design
(see *Web hosting topology* in
[`docs/reference/architecture.md`](../reference/architecture.md)). There is no
server of ours in the request path that could hold a token on the user's
behalf, and by the same document's environment model nothing secret can be
inlined into the bundle either. Every credential the session needs is therefore
in the browser, and the only question is which browser storage.

The data behind that session is GLBA-scope — SSNs and full client financials,
the same reasoning ADR 0001 rests on. This ADR does not get to be casual about
a 30-day credential in reachable storage; it gets to be explicit that it chose
one, and what holds the risk down.

## Consequences

- **XSS is now a session-compromise vulnerability, not just a defacement
  one.** Script running in the app's origin can read the refresh token and use
  it from anywhere until it expires or is revoked. The mitigations below reduce
  that exposure; none of them removes it. This is the accepted risk of the
  decision, and it is the thing to re-read if the app ever starts rendering
  untrusted HTML or pulling in third-party script.
- **A Content-Security-Policy response header on the CloudFront distribution
  is load-bearing under this decision, not a hardening nicety** — it is the
  principal control that stops injected script from running at all. It is
  **not configured today**: `infra/modules/web_hosting/` provisions the
  distribution with no response-headers policy. Adding one is required
  follow-up work for this milestone, not an optional cleanup.
- **Refresh-token rotation converts silent theft into a visible session
  break.** The app client already has rotation `ENABLED` with a 30-second retry
  grace period, so a rotated token is effectively single-use: once the
  legitimate client refreshes, a stolen copy stops working. An attacker's
  stolen token and the real user's session cannot both keep working
  indefinitely. This detects, it does not prevent.
- **Sign-out is server-side, not just local.** `enable_token_revocation` is
  true on the client, so the `/logout` leg revokes the refresh token at
  Cognito rather than leaving clearing local state as the only thing between a
  signed-out browser and a live session.
- **Local-only sign-out is explicitly rejected.** Dropping the tokens without
  the redirect leaves Cognito's hosted-UI session cookie intact, so the next
  sign-in silently re-authenticates the same user with no credential prompt —
  which on a shared machine reads, correctly, as "sign-out did not work".
- **Threat protection is not available as a mitigation here.** The pool is on
  the `ESSENTIALS` tier; Cognito's advanced security features are a PLUS-plan
  upsell that was deliberately deferred. What ESSENTIALS does include —
  revocation and rotation — is what the two bullets above spend.
- **Tokens are never logged, on either side.** `services/api/CLAUDE.md` already
  requires metadata-only JSON logs with no bodies and no PII; a bearer token is
  covered by that rule, and the client holds to the same line.
- **The app owns proving PKCE.** Since the pool cannot enforce it, "we send
  `code_challenge`/`code_verifier` with S256" is an assertion about our code
  and needs a test that fails if the parameter disappears.

## Alternatives considered

**Memory-only tokens, with nothing persisted.** The more secure option, and the
one a stricter reading of the GLBA obligation picks: an XSS then steals a
credential measured in the remainder of an hour rather than up to 30 days.
Rejected on user experience. Cognito's hosted-UI session cookie is short-lived,
so past that window every reload — and every returning visit — becomes a full
credential prompt for an attorney using the app across a working week, and the
30-day refresh window the pool is configured for goes entirely unused. This is
the natural fallback: if the CSP-plus-rotation mitigations prove insufficient
in practice, dropping persistence is a small, self-contained change.

**A token-holding backend-for-frontend, issuing an httpOnly `SameSite` cookie
session.** The only design that gets long sessions *and* keeps tokens out of
reach of injected script — the browser never holds a token at all, and the
first bullet of *Consequences* goes away rather than getting mitigated.
Rejected for this milestone because it puts compute in front of a deliberately
compute-free S3 + CloudFront SPA (*Web hosting topology* in
[`docs/reference/architecture.md`](../reference/architecture.md)), which is a
new service, a new deploy path and a new trust boundary to reason about, for a
product that does not yet sign a single user in. Named here as the upgrade
path: this is what to build if the risk accepted above stops being acceptable.

**Sending the ID token to the API as the bearer credential.** Rejected as the
wrong artifact — an ID token is issued to the client about the user, not minted
to authorize calls to a resource server, and accepting it invites an API that
authorizes on identity claims it was never handed for that purpose. The cost of
being correct here is the `GET /me` shape described in *Decision*, which is a
small price.
