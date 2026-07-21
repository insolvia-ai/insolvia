# Email setup — `insolvia.ai`

How mail for `insolvia.ai` is received, forwarded, and replied to, plus the
one-time human steps that cannot be automated.

Ported from `andreas-services/humbugg/docs/support-forwarding.md`, adapted to
Insolvia's confirmed four-address map and to the fact that we deliberately
remain in the **SES sandbox** for now.

> ## ⚠️ Read this first — replying does not work yet
>
> Everything in [Replying as an `@insolvia.ai` address](#replying-as-an-insolviaai-address)
> can be **configured** today, but it will **not work end-to-end until SES
> production access lands** — tracked as issue **#80 / 6.8** in the
> API/mailer milestone (`M6`), deliberately filed last.
>
> While the account is in the SES sandbox, SES rejects any send to an address
> that is not itself a verified SES identity. Your customers are not verified
> identities, so **replies sent through the SES SMTP alias will fail** with a
> `MessageRejected` / "Email address is not verified" error.
>
> Set it up now anyway — the setup is a prerequisite, it costs nothing while
> inbound volume is zero, and the only thing that changes at #80 is that it
> starts working. Until then, reply from your own Gmail address and accept that
> the private destination is visible to that recipient.
>
> **The sandbox is an OUTBOUND restriction only.** Inbound SES receipt rules are
> completely unaffected by it. Forwarding therefore works *fully* as soon as the
> destination Gmail address is verified as an SES identity (issue **#14 / 1.2**)
> — that single verification is all inbound needs.

---

## The address map

Confirmed in issue **#25 / 1.11**. All four are `@insolvia.ai`.

| Address | Purpose | Direction | Forwarded? |
|---|---|---|---|
| `hello@` | General / inbound enquiries, the public contact address | receive + reply | ✅ → private Gmail |
| `support@` | Product support | receive + reply | ✅ → private Gmail |
| `no-reply@` | Transactional sender (app mail, and the `From:` on forwarded copies) | **send-only** | ❌ **never** |
| `security@` | Vulnerability disclosure | receive + reply | ✅ → private Gmail |

**`no-reply@` is send-only.** It must be excluded from the forwarder's
allowed-recipient list. It exists purely as an outbound `From:` identity;
anything arriving at it is noise, and forwarding it would create an obvious
mail-loop surface (the forwarder sends *as* `no-reply@`).

### The forwarding destination is a secret

The private Gmail address every inbound message lands in is **never committed to
this repo**. It lives in an SSM SecureString and is injected once via a
`TF_VAR_`:

| Where | Value |
|---|---|
| GitHub environment secret | `INBOUND_FORWARD_TO` — the real private Gmail address |
| CI → Terraform | exported as `TF_VAR_inbound_forward_to` for the apply job |
| Terraform variable | `inbound_forward_to` (`sensitive = true`, default `""`) |
| SSM SecureString | `/insolvia/<env>/inbound-forward-to` (created from the var, then `ignore_changes = [value]`) |
| Lambda | env `INBOUND_FORWARD_TO_PARAM`; fetched at runtime with `WithDecryption` |

Throughout this document that address is written as
**`<PRIVATE_GMAIL_DESTINATION>`** — a placeholder. The real value is a
human-provided secret; do not write it into `terraform.tfvars`, a workflow file,
an issue, or a commit message.

For a local plan you may pass `-var 'inbound_forward_to=you@example.com'` or
`export TF_VAR_inbound_forward_to=...`.

---

## How inbound forwarding works

> **Not implemented in this document's change.** The Terraform module
> (`infra/modules/inbound_forwarding`, issue **#21 / 1.7**) and the Lambda source
> (`services/inbound_forwarder/`, issue **#22 / 1.8**) are being delivered
> separately. This section describes the design so the runbook is complete; if
> the shipped code disagrees with what follows, the code is right — fix this doc.

```
sender ──▶ MX (insolvia.ai) ──▶ SES inbound receipt rule
             │                        │   (hello@ / support@ / security@ only)
             │                   1. S3 action  → s3://insolvia-inbound-<env>/inbound/<messageId>
             │                   2. Lambda action (async, invocationType=Event)
             ▼                        │
        spam / virus scan        insolvia-inbound-<env>-forwarder (Python 3.11)
                                      │  reads raw MIME from S3, parses safely,
                                      │  builds a NEW message, Reply-To = original sender
                                      ▼
                                 SES SendRawEmail   From: no-reply@insolvia.ai
                                      ▼
                                 <PRIVATE_GMAIL_DESTINATION>
```

The forwarder's safety behaviours are deliberate and are ported intact from the
humbugg original:

- **Never relays untrusted headers.** A brand-new message is built; only a
  sanitised subject, the decoded body, and size-bounded attachments are copied.
  `Reply-To:` is the parsed original sender.
- **Spam / virus verdicts are checked before the body is touched.** Dropped when
  SES reports `spamVerdict: FAIL` or `virusVerdict: FAIL`/`PROCESSING_FAILED`.
- **Mail-loop detection.** Dropped when the message carries our
  `X-Insolvia-Forwarded` marker, when the envelope sender is empty (`<>`
  bounce/auto-reply), or when the sender is one of our own domains.
- **Allowed-recipient check**, as defence-in-depth on top of the receipt rule.
  `no-reply@` is *not* on that list.
- **Failures are loud.** Permanent drops are swallowed; transient failures raise,
  so the async Lambda retries and, on exhaustion, lands on the DLQ with a
  CloudWatch alarm (issues **#23 / 1.9** and **#24 / 1.10**). A missing secret
  is never a silent drop.

---

## Replying as an `@insolvia.ai` address

Forwarding is **inbound only**. It puts a copy of the customer's mail in the
private inbox; it does not give you an outbound identity.

A forwarded message arrives `From: no-reply@insolvia.ai` with `Reply-To:` the
original sender. If you just hit **Reply** in Gmail, the reply reaches the
customer (good) but goes out `From:` **your personal Gmail address** — which is
not an `@insolvia.ai` address and **leaks the private destination this whole
feature exists to hide**.

To answer as `hello@insolvia.ai` (or `support@`, `security@`), set up **"Send
mail as" over SES SMTP**. One-time, per address.

### Step 1 — create SES SMTP credentials

AWS Console → **SES** → **SMTP settings** → **Create SMTP credentials**.

This provisions a small IAM user allowed only to send email, and derives:

- an **SMTP username** (`AKIA…`-style), and
- an **SMTP password**, shown **once** — save it immediately.

These are **not** your AWS console login and **not** your normal AWS access
keys. They are a send-only username/password pair for a mail client, and they
are distinct from the forwarder Lambda (which sends via its own IAM role, not
SMTP). Put them in a password manager. **Never** in this repo, an env file, or
a workflow.

One credential pair is enough for all four addresses — the authorisation comes
from the verified `insolvia.ai` domain identity, not from the individual
address.

### Step 2 — add the address in Gmail

Gmail → ⚙️ **Settings** → **Accounts and Import** → **"Send mail as"** → **Add
another email address**:

| Field | Value |
|---|---|
| Name | e.g. `Insolvia` |
| Email address | `hello@insolvia.ai` |
| Treat as an alias | uncheck (so replies thread as the alias, not as you) |
| SMTP Server | `email-smtp.us-east-1.amazonaws.com` |
| Port | **587** |
| Username | SES SMTP username from step 1 |
| Password | SES SMTP password from step 1 |
| Security | **TLS (STARTTLS)** — *not* SSL |

Gmail then emails a confirmation code **to `hello@insolvia.ai`**, which the
forwarder delivers to `<PRIVATE_GMAIL_DESTINATION>`. Paste the code to finish.

> ⚠️ **This confirmation step is itself an outbound SES send, from Gmail's
> servers to our address — that part is inbound to us and works in the sandbox.**
> What does *not* work in the sandbox is your subsequent replies to unverified
> customers. See the warning at the top of this document.

Repeat for `support@insolvia.ai` and `security@insolvia.ai`. Do **not** add
`no-reply@` as a "Send mail as" identity — it is the app's transactional sender
and should not be a human's reply-from.

### Step 3 — reply from the alias

When answering a forwarded message, pick the `@insolvia.ai` address in the
**From** dropdown. The customer sees a branded reply and never the private
destination.

### Prerequisites

- The `insolvia.ai` **domain identity** must be verified for sending, with DKIM
  (issue **#19 / 1.5**) — this is what authorises any `@insolvia.ai` address as
  a `From:`.
- SES **production access** (issue **#80 / 6.8**) for replies to reach anyone who is
  not themselves a verified identity.

---

## Verifying

1. **DNS / MX** — the apex MX must resolve to SES inbound:
   ```bash
   dig +short MX insolvia.ai      # expect: 10 inbound-smtp.us-east-1.amazonaws.com
   ```
2. **Active receipt rule set** — only one can be active per account per region:
   ```bash
   aws ses describe-active-receipt-rule-set --region us-east-1
   ```
   Expect `insolvia-inbound-<env>` with rules matching `hello@`, `support@`,
   `security@` — and **not** `no-reply@`.
3. **Destination secret present** (prints the real address — run it somewhere
   private, and do not paste the output anywhere):
   ```bash
   aws ssm get-parameter --name /insolvia/<env>/inbound-forward-to --with-decryption
   ```
   It must not still be the placeholder.
4. **End-to-end inbound** — from an external address, mail `hello@insolvia.ai`.
   A copy should arrive at `<PRIVATE_GMAIL_DESTINATION>`,
   `From: no-reply@insolvia.ai`, `Reply-To:` the original sender.
5. **Reply path** — will fail until issue **#80 / 6.8**. Once it lands, reply from the
   alias and confirm the recipient sees `From: hello@insolvia.ai`.

### When mail does not arrive

- **Nothing at all** — check MX (1), check the rule set is the *active* one (2),
  check the destination Gmail is a verified SES identity (issue **#14 / 1.2**).
- **DLQ has messages / alarm firing** — usually a missing or placeholder SSM
  destination, SES throttling, or an S3 read error. Fix the cause, then redrive
  the DLQ.
- **Forwarder errors alarm** — read `/aws/lambda/insolvia-inbound-<env>-forwarder`.

---

## Migrating to Google Workspace

**This is the section to read before anyone signs up for Google Workspace.**

Today `insolvia.ai` mail is received by SES and forwarded to a personal Gmail
account. When the company moves to Google Workspace (real `@insolvia.ai`
mailboxes), the apex **MX records flip from SES to Google** — and at that moment
the SES inbound path stops receiving anything.

**The failure mode this section exists to prevent:** doing half the change.

- Flip MX to Google but leave the SES receipt rule set active → the rules match
  nothing, the forwarder goes quiet, and the alarms stay green because *no
  message ever arrives to fail*. Mail is fine (Google has it), but you now pay
  for and maintain dead infrastructure, and anyone reading this repo believes
  forwarding is live when it is not.
- Retire the SES inbound stack *before* MX points at Google → there is a window
  where nothing accepts mail for the domain and **senders get bounces**.

Both are avoidable by doing it as **one ordered change**.

### Ordered checklist

Do these in order. Steps 1–3 are the cutover; 4–7 are cleanup that must follow
in the same change, not "later".

1. **Provision Workspace first, change nothing in DNS.** Create the Workspace
   tenant, verify domain ownership (Google's TXT method — it does not touch MX),
   and create the real mailboxes: `hello@`, `support@`, `security@`. Confirm
   they exist and you can log into them *before* any MX change.
2. **Flip the MX records.** In `infra/` (Route53, not the Google console) replace
   the single SES MX record —
   `10 inbound-smtp.us-east-1.amazonaws.com` — with Google's MX set. This is the
   cutover instant. Keep TTLs low (300s) for a day beforehand so a rollback is
   fast.
   - **Keep SPF and DMARC**, but update SPF to include Google
     (`include:_spf.google.com`) **as well as** SES (`include:amazonses.com`) —
     SES still sends transactional mail as `no-reply@`, so removing the SES
     include breaks the app's outbound mail. This is the single easiest thing to
     get wrong.
   - Leave DKIM for SES in place; add Google's DKIM as a second selector.
3. **Verify inbound to Google works** before deleting anything. Mail
   `hello@insolvia.ai` from an external address and confirm it arrives in the
   Workspace mailbox. Do not proceed until this passes.
4. **Deactivate and delete the SES receipt rule set.** Set the account's active
   receipt rule set to none (or to another set), then destroy
   `insolvia-inbound-<env>`. Deleting the rules while the set is active is what
   causes the "silently not forwarding" state above, so remove the whole set.
5. **Delete the forwarder Lambda**, its IAM role, the DLQ, and the CloudWatch
   alarms — i.e. remove the `infra/modules/inbound_forwarding` module block from
   the env, and delete `services/inbound_forwarder/` if nothing else uses it.
6. **Empty and delete the S3 inbound bucket** (`insolvia-inbound-<env>`). It
   contains raw customer mail, so this is a privacy obligation, not tidiness.
   Terraform will refuse to destroy a non-empty bucket — empty it deliberately
   and check the lifecycle rule had been expiring objects as intended.
7. **Delete the SSM SecureString** `/insolvia/<env>/inbound-forward-to` and the
   `INBOUND_FORWARD_TO` GitHub environment secret. The private personal Gmail
   address no longer has any role in the system and should not linger in a
   parameter store.
8. **Remove the Gmail "Send mail as" entries** from the *personal* Gmail account
   — `hello@`, `support@`, `security@`. They will stop working anyway once
   replies come from the real Workspace mailboxes, and leaving them is a live
   path to sending as the company from a personal account.
9. **Delete the SES SMTP credentials** (the `AKIA…` IAM user from step 1 of the
   reply runbook) *if* nothing else uses SMTP. Note that the app's transactional
   `no-reply@` mail sends via an IAM role, not SMTP, so this is usually safe —
   confirm before deleting.
10. **Update the docs in the same PR.** This file, `README.md`, and any issue or
    plan entry that describes SES inbound forwarding. A runbook describing a
    system that no longer exists is worse than no runbook.

### What stays

**SES does not go away.** After the migration SES still owns all *outbound*
transactional mail from `no-reply@insolvia.ai` — the domain identity, DKIM,
custom MAIL FROM, and (by then) production access. Only the *inbound* half is
retired. Do not delete the SES domain identity.

### Verify after

- `dig +short MX insolvia.ai` returns Google's MX servers and **no**
  `inbound-smtp.us-east-1.amazonaws.com`.
- `dig +short TXT insolvia.ai` shows one SPF record including **both**
  `_spf.google.com` and `amazonses.com`.
- External mail to `hello@`, `support@`, and `security@` lands in the Workspace
  mailboxes.
- A reply from a Workspace mailbox arrives with `From: hello@insolvia.ai`.
- A transactional send from the app (as `no-reply@insolvia.ai`) still delivers
  and still passes SPF/DKIM/DMARC — check the receiving side's headers, do not
  assume.
- `aws ses describe-active-receipt-rule-set --region us-east-1` reports no
  Insolvia inbound rule set.
- The S3 inbound bucket and the forwarder Lambda no longer exist; `terraform
  plan` on the env is clean (no orphan resources, no pending destroys).

---

## Related

- [`AWS_SETUP.md`](AWS_SETUP.md) — AWS/GitHub bootstrap, deploy gating.
- [`MVP_PLAN.md`](MVP_PLAN.md) — Milestone 1 (`Foundation · Domain & Email`)
  issue breakdown and the SES-sandbox capability table.
- [`TERRAFORM_ARCHITECTURE.md`](TERRAFORM_ARCHITECTURE.md) — state model and
  environment layout.
