# services/mailer — agent rules

Shared outbound-email platform (apps own their templates and copy). Human docs:
[`README.md`](README.md). Run with `scripts/dev-up.sh` (Mailpit).

- **Layered `core / api / adapters / entrypoints`**; `tests/test_architecture.py`
  enforces the direction (`core` depends on nothing else; `api` only on `core`).
- **Caller identity, sender, configuration set, and status routing come from the
  registered service — never the request body.** A caller cannot spoof who it is
  or where feedback goes by setting a field.
- **S3 object keys and internal SQS messages are implementation details** — not
  part of the API contract.
- **The production API is IAM-authenticated (SigV4)** — the only application
  ingress.
- **Never write message bodies, recipients, subjects, filenames, or upload URLs
  to logs or DynamoDB.**
- **Local Mailpit must never relay externally.**
