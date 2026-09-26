# ADR 0001 — The client-stays-dumb trust boundary

- **Status:** Accepted
- **Date:** 2026-07-22
- **Relates to:** decision D6 in `docs/plan.md`; issue #67

## Decision

**No client — web or desktop — ever holds AWS credentials or calls an AWS
service directly.** Every read and write of Insolvia data is brokered by the
backend API (`services/api/`). Clients authenticate to the API; only the API's
own execution role authenticates to AWS.

## Context

The desktop app is a fat client running on an attorney's machine — an
environment we do not control, cannot patch on our schedule, and must assume
can be inspected. Per `docs/regulatory-source-register.html`, the data it
handles — SSNs and full client financials — puts us under the GLBA Safeguards
Rule and state data-security law. A credential shipped to a client is a
credential we no longer control, and scoping tricks (Cognito identity pools,
pre-signed everything) narrow the blast radius without changing who holds the
key.

Left undocumented, this rule erodes the first time something is easier to do
client-side. It already came up once: the marketing site's waitlist form.

## Consequences

- Every client capability is an API endpoint. There is no second path; if the
  API can't express it, the API grows, the client does not.
- This applies to **our own server-side renderers too**: the marketing SSR
  Lambda submits waitlist signups through the API's public endpoint rather
  than holding a DynamoDB grant. That grant was rejected in review, and this
  API exists partly because of it — one service touches the tables, one place
  enforces validation, rate limits, and audit.
- IAM policy review stays small: the API's execution role is the only
  application principal with data-store access.
- Cost accepted: an extra network hop and an endpoint to build for each new
  capability, even "trivial" ones.

> **Amended by [ADR 0024](0024-electronic-filing-path.md) (2026-09-26).**
> The rule about clients stands. What this ADR did not contemplate is the
> server side holding a credential that is **not ours**: to file cases,
> Insolvia's filing worker holds each enrolled attorney's CM/ECF/PACER
> password and TOTP seed — by FRBP 5005(a)(3), that attorney's signature in
> federal court. No client holds it (the seed crosses the enrolment screen
> once, inbound), and it is fenced by its own KMS key and store, decryptable
> by the filing worker alone, every use logged and revocable at once. The
> reasoning above — a credential shipped out of our control is one we no
> longer control — is why that fence is as narrow as 0024 makes it.
