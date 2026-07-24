# SES production access — the request runbook

Issue **#80 / 6.8**, the last item in Milestone 6. This is the runbook for
getting `insolvia.ai` out of the Amazon SES sandbox.

> **Submitting is a human action and cannot be automated from this repo.** It
> is a form in the AWS console (or a Support case), tied to an AWS account and
> answered by a human reviewer. Everything this repo *can* do — the privacy
> policy, the unsubscribe path, bounce/complaint handling, suppression, alarms
> — is done. What follows is the part a person does, plus the exact text to
> paste.

---

## What the sandbox actually costs us

| Capability | In the sandbox |
|---|---|
| Sending to a **verified** SES identity | ✅ Works |
| Sending to **anyone else** | ❌ Rejected — every real user |
| Daily volume | 200 messages / 24 h |
| Send rate | 1 message / second |
| Receiving mail at `@insolvia.ai` | ✅ Unaffected — inbound is Google Workspace |
| Humans replying from Workspace mailboxes | ✅ Unaffected — sends via Google |

So: the app cannot send a welcome, verification, or password-reset email to a
single real attorney until this lands. It blocks every authenticated flow, not
just a nice-to-have. See [`EMAIL_SETUP.md`](EMAIL_SETUP.md) for why inbound is
unaffected.

---

## Pre-submission checklist

AWS reviews the *sending programme*, not just the account. Reviewers routinely
reject requests that cannot answer "what happens when someone complains" and
"how does a recipient opt out". Every row below is a thing they look for, and
every one of them is already built — but **verify, don't assume**, because a
rejected request costs days and a second rejection is materially harder.

| # | What AWS looks for | Where it lives | How to verify |
|---|---|---|---|
| 1 | A **live website** describing the business | `www.insolvia.ai` | `curl -sSf https://www.insolvia.ai/` returns 200 |
| 2 | A **published privacy policy** | `apps/insolvia_marketing/app/routes/privacy.tsx` | `curl -sSf https://www.insolvia.ai/privacy` returns 200 |
| 3 | A **working opt-out** | `/unsubscribe` → API → mailer suppression | See *Proving the unsubscribe path works* below |
| 4 | **Bounce and complaint handling** | `services/mailer` feedback Lambda (issue 6.7) | Suppression is automatic on permanent bounce and on any complaint |
| 5 | **Alarms** on bounce/complaint rates | `infra/modules/mailer` — `<name>-ses-bounce-rate` (>5%) and `<name>-ses-complaint-rate` (>0.1%) | Alarms exist; confirm the SNS topic has a **confirmed** subscription, or they fire into nothing |
| 6 | **DKIM, SPF, DMARC** aligned | `infra/modules/email` | `dig` checks in [`EMAIL_SETUP.md § Verifying`](EMAIL_SETUP.md#verifying) |
| 7 | A **verified domain identity** | `infra/envs/shared` | `aws ses get-identity-verification-attributes --identities insolvia.ai` |

### Two prerequisites that are NOT yet true

Both are deliberate decisions, not oversights, and both must be resolved
before submitting:

1. **`www.insolvia.ai` is parked offline.** `infra/envs/prod/main.tf` sets
   `site_enabled = false` — CloudFront serves a 403 and rows 1 and 2 above
   both fail. Nothing is destroyed; flip it to `true` and apply prod infra:

   ```bash
   ./scripts/prod-deploy.sh prod-infra --input mode=apply
   ```

   Do this *first*, then confirm both URLs return 200. Submitting with a dead
   website is the single most likely cause of a rejection.

   Staging (`staging-www.insolvia.ai`) is live and serves the same pages, so
   the copy can be reviewed before prod goes public — but a reviewer will look
   at the production URL, and staging is `noindex` / `Disallow: /` by design.

2. **Google DKIM is published but not switched on**, and DMARC is at
   `p=none`. Row 6 passes for the SES half regardless — SES signs its own mail
   with its own DKIM CNAMEs, which is what this request is about. The Google
   half is a separate click documented in
   [`EMAIL_SETUP.md § Remaining setup steps`](EMAIL_SETUP.md#remaining-setup-steps).
   Not a blocker for this request; worth doing anyway.

---

## Proving the unsubscribe path works

Do this against **staging** before submitting, so the answer to "how does a
recipient opt out" is something observed rather than something believed.

The path has three hops (`docs/adr/0001` — the client stays dumb, so the page
holds no AWS access and the API holds the signing key):

```
email footer link / mail client one-click
  → marketing  GET+POST /unsubscribe          (a page; no credentials)
  → API        POST /v1/unsubscribe           (verifies the HMAC token)
  → mailer     POST /v1/services/<id>/suppressions   (SigV4; writes the store)
  → sender Lambda refuses every later send to that address
```

What to check:

1. Send yourself a transactional message through the staging mailer. In the
   sandbox the recipient must be a verified SES identity — use one.
2. In the received message: the footer carries an **Unsubscribe** link next to
   the privacy link, and the raw headers carry `List-Unsubscribe` and
   `List-Unsubscribe-Post: List-Unsubscribe=One-Click` (RFC 2369 / RFC 8058).
   Gmail and Outlook render their own unsubscribe control from those.
3. Click the link. It lands on `staging-www.insolvia.ai/unsubscribe`, which
   **asks for confirmation** rather than acting on the GET — corporate mail
   gateways follow every link in an incoming message, and acting on a GET
   would unsubscribe people who never clicked.
4. Confirm. The page reports success.
5. Send the same message again. It must **not** arrive: the mailer's sender
   checks the suppression store before every send.
6. Confirm the record holds only a hash:

   ```bash
   aws dynamodb scan --table-name insolvia-mailer-suppressions-staging \
     --projection-expression 'recipient_hash,reason' --max-items 5
   ```

   `reason` should read `unsubscribe`. The plaintext address is not stored —
   the only question the table answers is "is this address suppressed", and a
   hash answers it without keeping a list of everyone who opted out.

If you want to describe the mechanism in the request text, describing exactly
this is far more convincing than "we honour unsubscribes".

---

## Submitting

**Where:** SES console → *Account dashboard* → **Request production access**.
(It opens a Support case of type *Service limit increase* → *SES Sending
Limits*. Going through the console pre-fills the account context; a raw
Support case works too but is more to fill in.)

**Region matters.** Sandbox status is per-region. Everything Insolvia runs is
`us-east-1` (a CloudFront ACM requirement — see the root `CLAUDE.md`), so make
the request in **us-east-1**. Being granted production access in another
region does nothing for us.

### Form answers

| Field | Answer |
|---|---|
| Mail type | **Transactional** |
| Website URL | `https://www.insolvia.ai` |
| Use case description | The text below |
| Additional contacts | `hello@insolvia.ai` |
| Preferred contact language | English |
| Acknowledgement | Yes — we only send to recipients who requested it, and we comply with the AWS Service Terms and AUP |

### Use-case description — paste this

> Insolvia is bankruptcy case-preparation software for consumer-bankruptcy law
> firms in the United States. Our users are attorneys and their staff who sign
> up for an account on our website.
>
> **What we send.** Transactional account email only: a welcome message on
> signup, an email-address verification message, and password resets. Every
> message is triggered by an action the recipient took in our application. We
> do not send marketing campaigns, we do not send to purchased or rented
> lists, and we have never imported an address list from anywhere.
>
> **How we collect addresses.** A person enters their own work email address
> when they create an account or join our early-access list on
> https://www.insolvia.ai. There is no other source of addresses.
>
> **Bounces and complaints.** An SES configuration set publishes every
> delivery, bounce, complaint, and rejection event to an SNS topic, which a
> dedicated Lambda function consumes. Any complaint, and any permanent bounce,
> automatically adds the recipient to our own suppression store. Our sending
> path checks that store before every single send and refuses to deliver to a
> suppressed address, so a complaint or a hard bounce results in exactly one
> message to that recipient and never a second. CloudWatch alarms notify us at
> a 5% bounce rate and a 0.1% complaint rate — both well below the AWS review
> thresholds — and we operate a kill switch that stops all outbound mail
> immediately without a deployment.
>
> **Opt-out.** Every message carries an unsubscribe link in its footer and
> `List-Unsubscribe` plus `List-Unsubscribe-Post: List-Unsubscribe=One-Click`
> headers (RFC 8058), so a recipient can opt out either from our page or
> directly from their mail client. Both write to the same suppression store
> that bounces and complaints write to, so a single check in the sending path
> honours all three. Opt-outs are honoured immediately and are never expired
> or reset. Recipients can also email hello@insolvia.ai and we suppress the
> address by hand.
>
> **Privacy.** Our privacy policy is published at
> https://www.insolvia.ai/privacy and describes what we collect, how long we
> keep it, and how to have it deleted. Suppression records are stored as a
> one-way hash of the address rather than the address itself.
>
> **Authentication.** The sending domain `insolvia.ai` is verified, DKIM-signed
> with a custom MAIL FROM domain (`mail.insolvia.ai`), covered by SPF, and has
> a DMARC record published.
>
> **Volume.** We are pre-launch. We expect fewer than 50 messages per day
> initially, growing to roughly 1,000–2,000 per day within twelve months as
> firms onboard. Our request is for the standard production sending quota; we
> do not need an elevated limit.

Adjust the volume figures if reality has moved — they are the one part of this
text that goes stale, and a figure that is obviously wrong invites questions
about the rest.

### Requested limits

The default production quota (50,000 messages/day, 14 messages/second) is far
more than the numbers above, so **do not request an increase beyond the
default**. Asking for headroom you cannot justify is a reason to be asked for
more detail, which costs a round trip.

---

## After it is granted

1. **Remove the sandbox warning** from
   [`EMAIL_SETUP.md`](EMAIL_SETUP.md) — the boxed "Outbound is still in the SES
   sandbox" section and the `### SES production access` note under *Remaining
   setup steps*, plus the sandbox capability table in
   [`MVP_PLAN.md`](MVP_PLAN.md) Milestone 1.
2. **Close issue #80** and mark 6.8 done in `MVP_PLAN.md` Milestone 6.
3. **Send a real end-to-end message** to an address that is *not* a verified
   SES identity — a personal Gmail is ideal. That single send is the only
   thing that actually proves the sandbox is behind us. Check the receiving
   headers for SPF, DKIM, and DMARC passing; do not assume.
4. **Confirm an SNS subscription** on the mailer's alarm topic if one is not
   already confirmed (`mailer_alarms_topic_arn` in the env outputs). Terraform
   manages no subscriptions on purpose — an email subscription needs a human
   to click a confirmation link.
5. **Watch the reputation dashboard** for the first few weeks. SES suspends
   accounts over sustained bounce/complaint rates, and the first real sends
   are when a bad address list would show up. Ours is not a list, but the
   first production send is still the first time the pipeline meets addresses
   nobody verified by hand.

## If it is rejected

A rejection is usually one of three things, in order of likelihood:

1. **The website or privacy policy did not load.** Check `site_enabled` —
   this is the failure mode this runbook exists to prevent.
2. **The use-case description was too thin.** The reply will ask for detail
   on bounce/complaint handling or opt-out. Answer in the same case rather
   than opening a new one; the text above already contains the answers.
3. **Volume looked inconsistent** with a pre-launch product. Reconcile the
   figures and reply.

Reply in the existing case. Opening a second request while one is open reads
as evasion and slows everything down.

---

## Related

- [`EMAIL_SETUP.md`](EMAIL_SETUP.md) — the address map, DNS records, and the
  Google-Workspace-inbound / SES-outbound split.
- [`MVP_PLAN.md`](MVP_PLAN.md) — Milestone 6 issue breakdown, and the M1
  sandbox capability table.
- [`adr/0001-client-stays-dumb-trust-boundary.md`](adr/0001-client-stays-dumb-trust-boundary.md)
  — why the unsubscribe path has three hops instead of one.
- `services/mailer/README.md` — the suppression store, and who is allowed to
  write to it.
