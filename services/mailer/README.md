# Mailer

Mailer is the shared outbound-email platform for the services in this
repository. Applications submit versioned HTTP requests; Mailer owns durable
admission, private content storage, queueing, SES delivery, attachments,
suppression, and normalized feedback.

## Production flow

1. An application role signs `POST /v1/services/<service>/messages` with AWS
   SigV4.
2. The ingress Lambda verifies that the caller role is registered for the route.
3. Mailer stores an internal manifest in S3 and enqueues a small SQS pointer.
4. The sender Lambda validates the content, suppression state, kill switch, and
   attachment scan results before calling SES.
5. SES events are normalized and sent to the application's status queue.

Attachments use `POST /v1/services/<service>/attachment-uploads` to obtain a
15-minute presigned `PUT`; raw attachment bytes never pass through API Gateway.

## Suppression, and who is allowed to add to it

Step 4 above refuses to send to a suppressed address. Entries reach that table
two ways:

- **Automatically**, from the SES feedback path — a permanent bounce or any
  complaint (`entrypoints/feedback_lambda.py`).
- **On request**, from a registered caller: `POST
  /v1/services/<service>/suppressions` with `reason: "unsubscribe"`
  (`contracts/suppression-request.schema.json`). This is how a person clicking
  the unsubscribe link in an email actually stops the mail.

Both write the same one-way hash to the same table, on purpose: an opt-out
stored anywhere else would need the sender to check two places, and the day
someone forgets the second check is the day an unsubscribed person gets
emailed.

**The mailer does not verify that the caller has the address owner's consent.**
It cannot — it sees a SigV4-signed request from a registered service and
nothing else. Proving the request came from the address's owner belongs to the
caller; for `insolvia_api` that proof is an HMAC token (`services/api`
`core/unsubscribe.py`) which the mailer never sees and holds no key for. What
bounds that split is the operation itself: the worst a compromised caller
achieves is stopping its own mail to an address — never reading the table,
never sending anything. The ingress role's IAM grant on the suppressions table
is `PutItem` and nothing else, so "cannot enumerate who unsubscribed" is
enforced and not merely intended.

A caller that wants a mail client to render its own unsubscribe button sets
`list_unsubscribe_url` on the message; the sender turns it into
`List-Unsubscribe` + `List-Unsubscribe-Post` (RFC 2369 / RFC 8058). The mailer
never mints or interprets that URL — the caller composed the body, so the
caller owns the link.

## Code boundaries

The source tree makes the runtime boundary explicit:

```text
src/insolvia_mailer/
├── core/          # contracts, validation, MIME construction, and ports
├── api/           # the single Flask application and versioned routes
├── adapters/      # AWS, in-memory, and Mailpit implementations
└── entrypoints/   # Lambda handlers and the development HTTP server
```

API Gateway and the development server both invoke the same Flask application.
Only their injected adapters differ. `core` has no framework or infrastructure
dependencies, `api` depends only on `core`, and an architecture test enforces
those dependency rules.

## Development flow

```bash
docker compose up --build
```

- Mailer API: <http://127.0.0.1:8026>
- Mailpit inbox: <http://127.0.0.1:8025>

The development server uses the same Flask routes and schemas without SigV4.
Product email is delivered only to Mailpit, which has no outbound relay
configured.

Example:

```bash
curl -i http://127.0.0.1:8026/v1/services/insolvia_api/messages \
  -H 'content-type: application/json' \
  --data @contracts/examples/message.json
```

## Privacy and retention

- Access logs contain method, route, status, latency, and request ID only.
- DynamoDB records contain identifiers, hashes, states, and timestamps—not
  message content.
- Successfully delivered S3 content is deleted promptly.
- Abandoned, failed, and quarantined content expires after 14 days.
- Delivery metadata expires after 90 days; suppressions require explicit
  removal.

See [operations.md](docs/operations.md) for alarms and recovery.
