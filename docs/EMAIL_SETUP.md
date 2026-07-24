# Email setup — `insolvia.ai`

How mail for `insolvia.ai` is sent and received, and the one-time human steps
that cannot be automated.

**Sending and receiving are two different providers, on purpose.**

| Direction | Provider | Owns |
|---|---|---|
| **Receiving** — mail to `@insolvia.ai` | **Google Workspace** | The human mailboxes: `hello@`, `support@`, `security@` |
| **Sending** — transactional mail from the app | **AWS SES** | The verified domain identity, DKIM, custom MAIL FROM, and `no-reply@` |

Everything in DNS is Terraform-owned in `infra/modules/email`, applied from
`infra/envs/shared`. Do not add mail records in the Route53 console or in the
Google Admin console's "let Google manage it" flow — they will be reverted by
the next apply.

> ## ⚠️ Outbound is still in the SES sandbox
>
> The account has **not** been granted SES production access (tracked as issue
> **#80 / 6.8**). While in the sandbox, SES rejects any send to an address that
> is not itself a verified SES identity.
>
> Everything the request needs is built — privacy policy, unsubscribe path,
> suppression, bounce/complaint alarms. What remains is a human action in the
> AWS console, and one decision (`www.insolvia.ai` is parked offline, so the
> privacy policy AWS reviews does not currently load). Runbook, checklist, and
> the exact request text: **[`SES_PRODUCTION_ACCESS.md`](SES_PRODUCTION_ACCESS.md)**.
>
> This affects the **app's** transactional mail from `no-reply@` only. It does
> **not** affect Google Workspace: mail humans send from their `@insolvia.ai`
> mailboxes goes out through Google, which has no such restriction. Inbound is
> likewise unaffected.

---

## The address map

| Address | Purpose | Direction | Lives in |
|---|---|---|---|
| `hello@` | General / inbound enquiries, the public contact address | receive + reply | Google Workspace |
| `support@` | Product support | receive + reply | Google Workspace |
| `security@` | Vulnerability disclosure | receive + reply | Google Workspace |
| `no-reply@` | Transactional sender (app mail) | **send-only** | SES — no mailbox |

**`no-reply@` is send-only and has no inbox.** It exists purely as an outbound
`From:` identity for mail the application sends. Do not create a Workspace
mailbox for it and do not add it as a "Send mail as" identity for a human.

---

## The DNS records, and what each one is for

All of these are in the `insolvia.ai` hosted zone (`Z01038711J6IZ68FD6ZDW`) and
all are managed by `infra/modules/email`.

| Name | Type | Value | Why |
|---|---|---|---|
| `insolvia.ai` | MX | `1 smtp.google.com` | **Who receives our mail.** Points inbound at Google Workspace. |
| `insolvia.ai` | TXT | `v=spf1 include:amazonses.com include:_spf.google.com -all` | **Who may send as us.** Both senders must be listed. |
| `insolvia.ai` | TXT | `google-site-verification=…` | Proves to Google we control the zone. |
| `google._domainkey.insolvia.ai` | TXT | `v=DKIM1;k=rsa;p=…` | Signs mail **Google** sends. |
| `_dmarc.insolvia.ai` | TXT | `v=DMARC1; p=none; …` | Alignment policy. Starts permissive — see below. |
| `_amazonses.insolvia.ai` | TXT | *(SES token)* | SES domain-identity verification. |
| `<token>._domainkey.insolvia.ai` | CNAME ×3 | *(SES DKIM)* | Signs mail SES sends. |
| `mail.insolvia.ai` | MX | `10 feedback-smtp.us-east-1.amazonses.com` | Bounce/complaint feedback for mail **SES sends**. |
| `mail.insolvia.ai` | TXT | `v=spf1 include:amazonses.com -all` | SPF for the SES Return-Path domain. |

Three of these are routinely confused with each other. They are not
interchangeable:

- **The apex MX vs. the `mail.` MX.** The apex MX decides who receives mail
  *addressed to us*. The `mail.insolvia.ai` MX is the Return-Path endpoint that
  collects bounces for mail *we send via SES*. Swapping them silently breaks
  inbound mail.
- **The apex SPF vs. the `mail.` SPF.** The apex SPF must list **both** senders,
  because both send with a `From:` of `@insolvia.ai`. The `mail.` SPF stays
  SES-only, because Google never sends with that Return-Path.
- **There is exactly one TXT record set per name.** The Google verification
  token and the SPF record share the apex TXT set and are published together
  from `var.additional_apex_txt_records`. Adding either as a separate
  `aws_route53_record` silently clobbers the other.

### Only one apex MX set can exist

This is the constraint that shaped the current design. SES inbound receiving
requires the apex MX to point at `inbound-smtp.us-east-1.amazonaws.com`; Google
Workspace requires it to point at Google. No arrangement of priorities makes
both work — a lower-priority SES record just becomes a fallback that accepts
mail Google never sees.

Mailboxes are in Workspace, so Google wins, and the SES inbound path was removed
outright (see [What was removed](#what-was-removed-and-why)).

---

## Remaining setup steps

### Google DKIM — record published, needs one console click

The key at `google._domainkey.insolvia.ai` is in Terraform
(`var.google_dkim_value`) and live in DNS. **Publishing the record does not turn
signing on.** Google Admin console → **Apps** → **Google Workspace** → **Gmail**
→ **Authenticate email** → **Start authentication**. Google checks DNS at that
moment; until it is clicked, Workspace mail goes out unsigned.

This does not collide with the SES DKIM CNAMEs: those live at
`<ses-token>._domainkey`, a different name. Both senders sign independently.

> **The current key is 1024-bit.** Google's key-length dropdown offers 1024 and
> 2048; RFC 8301 deprecated 1024-bit signing keys and receivers increasingly
> discount them. The dropdown exists for DNS hosts that cannot store a record
> over 255 bytes — Route53 can, and `local.google_dkim_chunks` in
> `infra/modules/email/main.tf` already splits long values into multiple
> character-strings. So there is no reason to stay on 1024: regenerate at 2048
> in the Admin console, paste the new value into `google_dkim_value`, apply, and
> re-click **Start authentication**. Nothing else changes.

### DMARC stays at `p=none` until Google DKIM is authenticating

`_dmarc` is deliberately `p=none`. Tightening to `p=quarantine` or `p=reject`
before **both** senders sign correctly means receivers start dropping our own
legitimate mail — and no `rua=` aggregate-report address is configured yet
either, so you would be tightening blind. Order: click **Start authentication**,
confirm both senders pass alignment on real messages (read the headers), add a
`rua=` address, then tighten.

### SES production access

Issue **#80 / 6.8**. Until it lands, the app cannot send transactional mail to
anyone who is not a verified SES identity. Unrelated to Workspace.

The request itself is a form in the AWS console and cannot be automated from
this repo. [`SES_PRODUCTION_ACCESS.md`](SES_PRODUCTION_ACCESS.md) has the
pre-submission checklist (including the two prerequisites that are *not* yet
true), the exact text to paste, and what to do after it is granted — or
rejected.

---

## Verifying

```bash
dig +short MX insolvia.ai
```
Expect `1 smtp.google.com.` — and **no** `inbound-smtp.us-east-1.amazonaws.com`.

```bash
dig +short TXT insolvia.ai
```
Expect two values: the SPF record including **both** `amazonses.com` and
`_spf.google.com`, and the `google-site-verification=` token.

```bash
aws ses describe-active-receipt-rule-set --region us-east-1
```
Expect no active rule set. An Insolvia inbound rule set here means the teardown
was incomplete and mail is being intercepted before Google sees it.

Then end-to-end, which is the only check that actually proves anything:

- Mail `hello@insolvia.ai` from an external address → arrives in the Workspace
  mailbox.
- Reply from that mailbox → recipient sees `From: hello@insolvia.ai`, and the
  headers show SPF and DKIM passing.
- A transactional send from the app as `no-reply@insolvia.ai` still delivers and
  still passes SPF/DKIM/DMARC. Check the receiving side's headers; do not
  assume. This is the thing most likely to have been broken by a mail change,
  because nothing about the Workspace setup exercises it.

---

## What was removed, and why

`insolvia.ai` mail used to be received by SES and forwarded to one private Gmail
address: an SES receipt rule set writing raw MIME to S3, a Python forwarder
Lambda (`services/inbound_forwarder/`) that rebuilt each message and re-sent it,
an SQS DLQ, and CloudWatch alarms — plus a `TF_VAR`-injected SSM SecureString
holding the destination address. Issues **#21–#25**.

Real Workspace mailboxes replace all of it, so it was deleted rather than left
running: the apex MX now points at Google, so the receipt rules would match
nothing, the forwarder would go permanently quiet, and the alarms would stay
green *because no message ever arrives to fail*. Dead infrastructure that looks
healthy is worse than no infrastructure.

Removed in the same change: `infra/modules/inbound_forwarding`,
`services/inbound_forwarder/`, `.github/workflows/inbound-forwarder-pr.yml`, the
`inbound_forward_to` Terraform variable, and the forwarder outputs.

Manual cleanup that Terraform cannot do for you:

- **The `INBOUND_FORWARD_TO` secret** on the `insolvia-shared` GitHub
  environment. Nothing reads it now; delete it.
- **The `insolvia-inbound-mail-shared` S3 bucket** may survive `terraform
  destroy` if non-empty (`force_destroy = false`). It holds raw customer mail,
  so emptying it is a privacy obligation, not tidiness.
- **Gmail "Send mail as" entries** for `hello@` / `support@` / `security@` in
  the *personal* Gmail account. They are a live path to sending as the company
  from a personal account — remove them.
- **SES SMTP credentials** (the `AKIA…` IAM user) created for those aliases, *if*
  nothing else uses SMTP. The app's transactional mail sends via an IAM role,
  not SMTP, so this is usually safe — confirm before deleting.

**SES itself does not go away.** The domain identity, DKIM, custom MAIL FROM,
and `no-reply@` are all still live and still needed. Only the inbound half was
retired.

---

## Related

- [`SES_PRODUCTION_ACCESS.md`](SES_PRODUCTION_ACCESS.md) — getting out of the
  SES sandbox: checklist, request text, and the post-grant steps.
- [`AWS_SETUP.md`](AWS_SETUP.md) — AWS/GitHub bootstrap, deploy gating.
- [`MVP_PLAN.md`](MVP_PLAN.md) — Milestone 1 (`Foundation · Domain & Email`)
  issue breakdown and the SES-sandbox capability table.
- [`TERRAFORM_ARCHITECTURE.md`](TERRAFORM_ARCHITECTURE.md) — state model and
  environment layout.
</content>
