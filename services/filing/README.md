# services/filing — the filing worker

The one principal that opens the CM/ECF credential vault, and the one that
files a case: [ADR 0024](../../docs/adr/0024-electronic-filing-path.md) PR 7.
An image Lambda on its own SQS queue ([ADR 0018](../../docs/adr/0018-sqs-queue-and-worker-lambda-over-step-functions.md)'s
shape), one approved filing per message.

```bash
./services/filing/scripts/dev-setup.sh          # venv + this machine's dev AWS
./services/filing/scripts/dev-test.sh           # ruff + mypy + pytest, exactly as CI
./services/filing/scripts/dev-up.sh             # the fake CM/ECF + the worker polling YOUR dev queue
./services/filing/scripts/dev-filing-proof.sh   # the whole flow on real dev AWS, as the worker's role
```

## What a job does

The message is the API's — `{kind, version, approval_id, filing_id, case_id}`,
ids only (`services/api` `core/filing_approval.filing_job_message`). The worker
re-reads everything else, and either goes on or stops:

1. **Claim** a `FILING#<filing_id>` record in the case partition — a
   conditional write, so one filing is only ever in flight once.
2. **Consume** the approval with the API's own `consume_approval`, over the
   digest recomputed from the stores by the API's own `basis_for_case`.
3. The **kill switch**, the **driver** for the court (none is verified yet:
   every real court hands back), and the **host fence** for every origin the
   driver will contact — all before the credential is opened.
4. Each packet file's bytes, checked against the SHA-256 the approval bound.
5. **Open** the credential (`credential.open`, purpose `sign_in`), sign in
   with a TOTP computed at the court's ask, open the case, upload.
6. **Re-check** immediately before the final submit: the kill switch, the
   credential (opened again — a revocation stops it here), the approval, and
   its digest recomputed again.
7. **Mark** `at_final_submit`, durably — then click.
8. Record the court's confirmation (`submitted`), then store the receipt with
   the case (`filed`).

### The state machine

```
claimed ─► signed_in ─► uploading ─► at_final_submit ─► submitted ─► filed
   │           │            │               │               │
   └───────────┴────────────┴─► handed_back └─► outcome_unknown ◄┘
```

Every transition is a conditional write on (state, attempt). Before the mark,
any stop is `handed_back`: nothing reached the court. After it, anything
uncertain is `outcome_unknown`, which is **never retried** — somebody looks
the debtor up on the court's own query first. A redelivered message never
starts a second filing: a terminal record is a no-op; `at_final_submit`
becomes `outcome_unknown`; `submitted` finishes storing the receipt (no court
contact); a pre-submit state is left alone inside the attempt's lease and
handed back after it. `core/filings.py` owns the table; `core/hand_back.py`
owns what each stop tells the attorney, every one ending at the filing-set
checklist.

`filed` is the *filing's* state. The case's `status=filed`, its court case
number and the pin freeze are ADR 0024 PR 8.

## The environment host fence and the kill switch

`core/fence.py` is a constant allowlist per environment — **local: loopback
only (the fake); staging: nothing; production: nothing** — enforced before the
credential is opened and again in `adapters/http/fenced_client.py` before any
connection object exists (each redirect hop included). No environment
variable can widen it; a court joins only in a reviewed diff (PR 10, after its
driver opened the fixture case on that court's training database). The kill
switch is the SSM parameter `/insolvia/<env>/filing/submissions-enabled`,
created `"false"` everywhere and read fresh before every run and every final
submit; only `"true"` means on. Locally `FILING_SUBMISSIONS_ENABLED` stands in
for it.

## The fake CM/ECF

`fake/fake_cmecf/` — PACER-style sign-in with a real TOTP check, the Open BK
Case screens, upload, review, submit, a confirmation with a case number, and
fault modes: `bad_login`, `totp_rejected`, `slow`, `upload_500`,
`timeout_after_submit`, `duplicate_case`, `unexpected_screen`,
`receipt_missing`. It counts every final submit it *receives*. It lives
outside `src/`, so the image never contains it, and it is never deployed:

- **Not to staging.** A deployed fake would be a second web service to run,
  fence and keep alive for no information the in-process fake does not give,
  and staging's fence must say "no court host" until PR 10 adds the real
  training databases — the staging check that matters (a court's real screens
  and its view of our egress) only a training database can answer. The fake
  runs in CI on every PR (the unit tier, in-process on 127.0.0.1) and on a
  laptop against the real dev queue, tables and vault.
- **Never to production.** The Lambda's configuration refuses
  `FAKE_CMECF_URL` outside `local`, and `resolve_driver` refuses a composed
  fake anywhere but local.

## Why the worker imports `insolvia_api.core`

The approval is bound to a digest of the filing set (`services/api`
`core/filing_approval.approval_basis`), and the filing set is built by the
forms engine (`core/filing_set.py` → `packet_assembly` → `forms_hub` → the form
projections). The worker must recompute that digest **byte for byte as the API
does**, twice per run — so it imports the API's own functions rather than a
copy (a copy that drifted would void approvals, or worse, pass one it should
not). Moving the closure into `packages/insolvia_core` would move the whole
forms engine, which no second service otherwise needs; so the image copies
`services/api/src` and the worker reaches exactly the modules
`tests/unit/test_architecture.py`'s `ALLOWED_API` names — the approval, the
filing set, the packet record, the drawing primitives, and two stores — never
the API's web layer. The one composition both call is
`filing_approval.basis_for_case`. Consequence: a change under
`services/api/src` re-runs this service's PR check and redeploys it
(`filing-pr.yml`, `release.yml`), and `pypdf` is pinned to the API's exact
version (`tests/unit/test_packaging.py`).

## The role

`insolvia-<env>-filing-role` (created by `infra/modules/filing_credentials`).
Its grants, all told: the vault (GetItem, Decrypt under the vault purpose,
the access-log append — `filing_credentials`), the queue (consume —
`filing_queue`), and `infra/modules/filing_worker`'s: GetItem/Query and
PutItem/UpdateItem on the case table, the case key through DynamoDB and S3
only, GetObject on `cases/*/packets/*`, PutObject on `cases/*/filings/*`, and
GetParameter on the kill switch. Locally the poller assumes the same role, so
a laptop run is held to exactly these.

## Three environments

| | local | staging | production |
|---|---|---|---|
| Runs as | the poller, assuming the worker role | the Lambda | the Lambda |
| Court it may reach | the fake, on loopback | none (PR 10 adds training DBs) | none (PR 10) |
| Kill switch | `FILING_SUBMISSIONS_ENABLED` | SSM, created off | SSM, created off |
| What a real job does | files against the fake | hands back (`court_not_verified`) | hands back |

**Not testable locally**, with the nearest approximation: the SQS → Lambda
event source mapping (the poller is the same consume-run-delete contract at
batch size 1); a real court's screens and its view of our egress (the fake,
and PR 10's training databases); PACER's real MFA (the fake runs real TOTP).
