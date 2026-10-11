---
name: insolvia-deploy-role-permissions
description: >-
  How to change what the Insolvia CI deploy role is allowed to do — and why
  those changes can't be applied by CI, or from this repo. Use this WHENEVER a
  change touches the GitHub Actions deploy role's IAM permissions (the OIDC
  provider / insolvia-shared-deploy-role role / its policy in
  infra/envs/ci-trust/main.tf, which lives in the insolvia-services repo since
  2026-10-11), or when diagnosing a deploy that fails with an IAM AccessDenied.
  Trigger it the moment you see "not authorized to perform: <service>:<Action>"
  / "no identity-based policy allows" in a staging or prod apply or a service
  deploy, or "AccessDenied" on iam:PutRolePolicy, or any time you're about to
  add an IAM action so the pipeline can create/modify some AWS resource. Also
  consult it before telling the user "just merge it" for anything that grants
  the pipeline a new permission — because that specific change is a PR to
  another repository plus a human-run apply, and forgetting that sends everyone
  in circles. Read it rather than guessing; getting the apply order wrong
  around the trust anchor can lock CI out of AWS.
---

# Changing the CI deploy role's permissions (Insolvia)

## The rule in one line

**The deploy role is not in this repo, and CI cannot grant itself permissions.**
When an insolvia deploy fails on an IAM `AccessDenied`, the fix is a PR to
[`insolvia-ai/insolvia-services`](https://github.com/insolvia-ai/insolvia-services)
editing its
[`infra/envs/ci-trust/main.tf`](https://github.com/insolvia-ai/insolvia-services/blob/main/infra/envs/ci-trust/main.tf),
merged there, then a **human** runs that repo's
[`scripts/apply-ci-trust.sh`](https://github.com/insolvia-ai/insolvia-services/blob/main/scripts/apply-ci-trust.sh)
from the insolvia-services checkout (`../insolvia-services`). Then re-run the
failed deploy here.

The role (`insolvia-shared-deploy-role`) trusts **both** repositories; the
staging seed role trusts this one only. Both moved with the shared
infrastructure on 2026-10-11 —
[ADR 0025](../../../docs/adr/0025-shared-infra-and-mailer-move-to-insolvia-services.md).

## Why a human, and why "just merge it" is wrong

The `ci-trust` root carries an explicit `Deny` (`DenySelfPrivilegeEscalation`)
on the role modifying its *own* policy, so an apply run as the deploy role
fails by design, and there is deliberately no workflow that applies it. A
privilege change to the pipeline takes effect only when someone holding admin
applies it consciously. The full reasoning is insolvia-services'
[`docs/runbooks/aws-bootstrap.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/aws-bootstrap.md)
§ "The ci-trust anchor".

So when you see the signal, tell the user: *PR to insolvia-services → merge →
run `scripts/apply-ci-trust.sh` there → re-run the deploy here.* Nothing in
this repo's diff can fix it.

## Getting the grant right first time

Each attempt costs a cross-repo round-trip, so read the error for the exact
`<service>:<Action>` **and the resource it was evaluated against**, and grant
the coherent set at once. The traps that have cost round-trips before:

- `lambda:CreateEventSourceMapping` is fenced by the `lambda:FunctionArn`
  **condition** (`function:insolvia-*`); the by-UUID mapping actions
  (`Get/Update/DeleteEventSourceMapping`, `ListTags`, `Tag/UntagResource`) need
  `resources = ["*"]` — a `FunctionArn` condition or an
  `event-source-mapping:*` resource both deny them. Trust the deny's
  `on resource: *` over the Service Authorization Reference.
- A SecureString SSM parameter needs KMS too (`kms:Encrypt`/`Decrypt` under
  `kms:ViaService = ssm.<region>.amazonaws.com`), not only `ssm:*`.
- A **rename** that moves a resource out of a fenced ARN family
  (`insolvia-*-case-*`, `alias/insolvia-*-cases`, …) silently drops a deny
  rather than failing — see `infra/CLAUDE.md` and the `insolvia-aws-naming`
  skill.

Scope every grant by ARN pattern or condition and say why in a comment — that
file documents the reasoning for each grant.

## Do NOT

- Weaken `DenySelfPrivilegeEscalation` / `DenyTrustAnchorMutation`, or add a
  workflow that applies `ci-trust`. Either deletes the security property.
- Apply `ci-trust` without reading the plan — a destroy/replace of the role or
  the OIDC provider locks CI out of AWS in both repositories at once.

Credential mechanics for the human apply live in the **`insolvia-aws-auth`**
skill.
