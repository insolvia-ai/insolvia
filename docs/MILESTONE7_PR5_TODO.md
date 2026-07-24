# PR5 handoff — SES production access (#80) — TEMP, delete after

The mailer stack (PRs 1–4) is code-complete. This branch is a placeholder for
the final issue **6.8 · Request SES production access**. Nothing is built here
yet. Delete this file when PR5 is done.

## What still needs doing

1. **Privacy policy page** — real `/privacy` route on `apps/insolvia_marketing`
   (React Router v7), covering data handling + transactional email + a contact
   address. PR4's email footer already links `https://www.insolvia.ai/privacy`,
   so this URL must go live before submitting.

2. **Unsubscribe / opt-out path** — *decision still open* (I was stopped before
   you chose). Bounce/complaint already auto-suppress via the feedback path
   (PR2) and alarms (PR3); this is the marginal user-initiated piece. Options:
   - **Minimal (recommended):** a public, HMAC-token-gated unsubscribe endpoint
     in `services/mailer` that writes to the existing suppression store; small
     SSM secret `/insolvia/<env>/mailer/unsubscribe-secret` + IAM read; tests
     against the memory store. Self-contained to the mailer.
   - **Defer:** document the design as the one remaining pre-submission item.
   - **Full:** marketing `/unsubscribe` → API → mailer, across all three.
   Transactional mail (welcome/verify/reset) does not legally require it, but
   AWS review likes to see an opt-out process.

3. **Submission runbook** — new `docs/SES_PRODUCTION_ACCESS.md` (or a section in
   `docs/EMAIL_SETUP.md`): the exact request text — use case (transactional auth
   mail for a consumer-bankruptcy case-prep SaaS), bounce/complaint handling
   (PR3 alarms + auto-suppression), the opt-out process, volume estimates, and
   the step-by-step. **Submitting is a human AWS-support action** — it cannot be
   automated from this repo.

4. **Bookkeeping** — update `docs/MVP_PLAN.md` Milestone 6 statuses (6.1–6.7
   land in this stack; 6.8 remains until the request is submitted).

## The stack these sit on (bottom → top)

`main` → `claude/mailer-service` (#73,#77) → `claude/mailer-infra` (#75,#74) →
`claude/mailer-monitoring` (#79) → `claude/mailer-api-integration` (#76,#78) →
`claude/mailer-ses-prod-prep` (this branch).

**Before applying any infra:** apply `infra/envs/shared` first (it gained the
`insolvia-mailer-*` S3 deploy-role statement), then per-env — from merged
`main`, never a feature branch.
