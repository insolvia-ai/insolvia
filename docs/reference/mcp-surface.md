# The MCP surface

The tool surface [ADR 0013](../adr/0013-mcp-server-replaces-direct-pms-integration.md)
promised: which tools expose the case domain to an attorney's AI harness, what
an agent write is allowed to mean, and the vocabulary the server speaks. This
is the design artifact issues 12.2–12.4 implement
([#261](https://github.com/insolvia-ai/insolvia/issues/261)–[#263](https://github.com/insolvia-ai/insolvia/issues/263));
where it lives is [ADR 0016](../adr/0016-mcp-server-is-its-own-service.md).

Three rules are inherited, not designed here, and every decision below is
downstream of them:

- **One trust boundary** ([ADR 0001](../adr/0001-client-stays-dumb-trust-boundary.md)):
  an MCP client is a client. It never touches a data store; the server brokers
  every read and write, exactly as `services/api` does for the app.
- **A case belongs to a firm** ([ADR 0009](../adr/0009-a-case-belongs-to-a-firm.md)):
  an MCP session resolves to a Cognito `sub` with firm permissions, looked up
  in our store on every call, never cached, failing closed. The firm is never
  an argument.
- **Confirm-before-entry** ([`case-data-model.md`](case-data-model.md)):
  agent-written data lands as candidate records and becomes case data only on
  human confirmation. There is no tool that writes a case record, so the
  invariant holds structurally — not as a check the write path performs, but
  as a write path that does not exist.

Protocol facts below were verified against the MCP specification, revision
**2026-07-28** (current at time of writing —
[modelcontextprotocol.io](https://modelcontextprotocol.io/specification/2026-07-28/)).

## Protocol posture

- **Remote server, Streamable HTTP.** One MCP endpoint accepting POST; each
  JSON-RPC request is its own HTTP request. Revision 2026-07-28 removed
  protocol-level sessions and the standalone GET stream, so a fully stateless,
  single-JSON-response server is spec-conformant — which is exactly the shape
  Lambda wants. Our tools never need a mid-call SSE stream: every call is a
  bounded read or a bounded write.
- **Older harnesses still speak the handshake era** (`2025-11-25` and earlier:
  `initialize`, `Mcp-Session-Id`). Version negotiation and era compatibility
  are the official MCP Python SDK's job — 12.3 builds on the SDK rather than
  hand-rolling JSON-RPC, and which eras the real harnesses actually speak is a
  12.5 measurement (see [Constraints from harnesses](#constraints-from-harnesses)).
- **Tools only.** No MCP resources, prompts, or subscriptions in v1. Tool
  support is the one capability every harness has; resource support is uneven.
  Revisit after 12.5 if a harness turns out to want documents as resources.
- **Every tool returns `structuredContent`** conforming to its `outputSchema`,
  plus the serialized JSON as a text block (the spec's backwards-compatibility
  SHOULD). Read-only tools carry `readOnlyHint: true` annotations; nothing on
  this surface is destructive, so no tool carries `destructiveHint`.

## Identity and authorization — the seam 12.2 implements

The MCP server is an **OAuth 2.1 resource server**. The spec-level
requirements (all MUST, revision 2026-07-28):

- Serve **OAuth 2.0 Protected Resource Metadata** (RFC 9728) at its canonical
  URI, naming the authorization server; answer unauthenticated requests with
  **401 + `WWW-Authenticate`** carrying the `resource_metadata` URL and a
  `scope` hint.
- **Validate every access token** and validate that it was issued for this
  server; invalid or expired tokens answer 401; insufficient scope answers
  403 with `error="insufficient_scope"`.
- The authorization server must offer RFC 8414 or OpenID Connect discovery —
  **our Cognito pool already provides OIDC discovery**, so ADR 0013's "auth
  follows the standards seam we already have" holds: the pool is the AS, the
  MCP server is the resource, and authorization stays in our store per
  ADR 0009.

Two Cognito facts are constraints 12.2 must design around, recorded here so it
starts from them rather than rediscovering them:

1. **Cognito has no dynamic client registration and no Client ID Metadata
   Document support.** The spec makes both optional (CIMD is SHOULD, DCR is
   deprecated-but-MAY) and explicitly allows pre-registered client IDs. So
   v1 is **one pre-registered app client per harness**, created in
   `infra/modules/auth` like the existing app clients. Whether the harnesses
   we claim can work with a pre-registered client is a 12.5 question.
2. **Cognito access tokens carry `client_id` and `scope`, not an RFC 8707
   `aud`.** Audience binding is approximated the way `services/api` already
   does it: verify issuer + signature + `token_use` and match `client_id`
   against an allowlist of *MCP* client ids — disjoint from the app's client
   id, so an app token is not an MCP token and vice versa. State this gap in
   12.2's implementation, don't paper over it.

Token verification itself is `insolvia_core.auth`, unchanged. After the token,
every call resolves the accessor (firm user by `sub`, firm status) from the
store — the two reads ADR 0009 costs, bought for the same reason: an admin who
cuts an agent's access expects it cut now, not within the hour.

## The tool surface

Eight tools. Granularity call first, because it shapes everything:

**Per-entity-type access goes through two generic record tools with an
`entity_type` enum, not a dedicated tool pair per entity.** The case model has
23 case-scoped types; dedicated tools would put ~46 tool definitions into
every harness session's context, and the entity list moves with the annual
form cycle — a new SOFA question must not be a new tool. The enum mirrors how
the store itself works (*list one entity type within a case* is the dominant
access pattern the data model records). The cost is honest: a generic tool's
`inputSchema` cannot type 23 payloads, so payload validation happens
server-side in the same core parse functions the API uses — which is where it
lives anyway, per ADR 0001. Case listing and reading stay dedicated tools
because they are the entry points a harness reasons about.

| Tool | Does | Requires |
|---|---|---|
| `whoami` | The caller's firm, name, and per-feature permissions — or the fact that they have no firm | authenticated only (the `/v1/me` of this surface) |
| `list_cases` | The cases the caller may see, newest first, paginated | `cases: view_only` |
| `get_case` | One case root + petition-status summary + per-entity-type record counts | `cases: view_only` |
| `list_case_records` | One entity type's records within one case, paginated | per entity type — see the gate table |
| `get_case_record` | One record by id | per entity type — same table |
| `propose_case_records` | Write a batch of **candidate** records for human review | `intake: add_edit` |
| `check_proposals` | The review status of this surface's candidates: pending / accepted / corrected / rejected / withdrawn | `intake: view_only` |
| `withdraw_proposal` | Retract the caller's own still-pending candidate | `intake: add_edit` |

### Schemas

Entity payload shapes are owned by
[`case-data-model.md`](case-data-model.md) and are not restated here; a
record's wire shape is the API's wire shape (`case_json`, `debtor_json`, …) so
the two surfaces cannot drift. Sketches use JSON-Schema shorthand.

```
whoami
  in:  {}
  out: { firm: { id, name } | null,
         displayName: string,
         isAdmin: boolean, accessAllCases: boolean,
         permissions: { <feature>: "add_edit" | "view_only" | "hidden" } }

list_cases
  in:  { status?: "intake"|"ready_to_file"|"filed", limit?: int (1–100, default 25), cursor?: string }
  out: { cases: [ <case wire shape> ], nextCursor?: string }

get_case
  in:  { caseId: string }
  out: { case: <case wire shape>,
         recordCounts: { <entity_type>: int } }

list_case_records
  in:  { caseId: string, entityType: <enum below>, limit?: int, cursor?: string }
  out: { records: [ <that entity's wire shape> ], nextCursor?: string }

get_case_record
  in:  { caseId: string, entityType: <enum>, recordId: string }
  out: { record: <wire shape> }

propose_case_records
  in:  { caseId: string,
         proposals: [ { entityType: <enum>,
                        payload: object,              // mirrors the target entity, like extraction_candidate.payload
                        externalRef?: { system, externalId, externalUrl },
                        note?: string } ] }           // 1–25 per call
  out: { candidates: [ { candidateId, entityType, status: "pending" } ] }

check_proposals
  in:  { caseId: string, candidateIds?: [string], status?: <status enum>, limit?: int, cursor?: string }
  out: { candidates: [ { candidateId, entityType,
                         status: "pending"|"accepted"|"corrected"|"rejected"|"withdrawn",
                         confirmedBy?, confirmedAt?,
                         correctedPayload?,           // what the human changed it to
                         resultingRecordId? } ],      // the case record acceptance created
         nextCursor?: string }

withdraw_proposal
  in:  { caseId: string, candidateId: string }
  out: { candidateId, status: "withdrawn" }
```

The `entity_type` enum is the case-scoped entity list of the data model
(`petition`, `debtor`, `creditor`, `claim`, `asset`, `exemption`, …). The
server publishes only the types its store actually implements at any given
time; the enum grows with the model, never a new tool.

Three deliberate absences:

- **No tool returns a full tax identifier.** Records serialize with last-four
  only, the default representation the data model mandates. The audited
  full-value read exists for the e-filing path and is *not a tool* — there is
  no argument an agent can pass to receive an SSN.
- **No document bytes.** `document` records (metadata) are readable through
  the record tools; upload and download stay in the app, where the two-step
  presigned flow and its human live. A harness that needs to hand us a PMS
  document is a 12.5 finding, not a v1 feature.
- **No case creation.** A case root is confirmed case data, and ADR 0013's
  invariant says an agent never writes that. v1 agents target a case a human
  already opened (found via `list_cases`). If harness reality demands
  agent-initiated matters, the invariant-compatible shape is a *case
  candidate* reviewed like any other — recorded as an open question, not
  designed here.

### Permission gates

The four axes of ADR 0009 apply unchanged; `@requires`-equivalent checks run
in the MCP service's tool layer, below auth, failing closed. The per-entity
gate map, mirroring the API's routes:

| Entity types | Feature |
|---|---|
| case root (via `list_cases`/`get_case`) | `cases` |
| `document` | `documents` |
| everything else (petition, debtor, creditor, claim, asset, …) | `intake` |

**A tool the caller may not use is listed but refuses — list-visible,
call-denied.** `tools/list` returns the same static eight tools for every
authenticated session; a call the caller's permissions do not admit returns a
`permission_denied` tool error. Reasons, in order: the refusal is a fact about
*the caller's own account* — exactly what `insolvia_core.errors.ForbiddenError`
argues must not hide behind a 404, because there is nothing to enumerate and
hiding it sends an honest agent (and the human supporting it) hunting for a
missing tool instead of an unassigned permission; a static list is cacheable
and spares us `listChanged` notifications; and permissions are revocable
mid-hour (ADR 0009), so a filtered list would be stale the moment it mattered.
The anti-oracle rule protects *other tenants*, and it carries over where it
belongs: another firm's `caseId` answers `not_found`, indistinguishable from a
case that does not exist.

## Candidate writes, end to end

The write half of the surface is one flow:

```
harness                         MCP service                    review (8.9, in-app)
   │  propose_case_records         │                                │
   ├──────────────────────────────►│ validate shape (core parsers,  │
   │                               │ shape-and-type only — intake   │
   │                               │ is progressive)                │
   │                               │ write candidate rows:          │
   │                               │   status: pending              │
   │                               │   origin: {channel: "mcp",     │
   │                               │     client_id, subject,        │
   │                               │     proposed_at}   ◄ from the  │
   │  {candidateIds, pending}      │   external_ref       token,    │
   │◄──────────────────────────────┤   payload            never an  │
   │                               │                      argument  │
   │                               │              a human accepts / │
   │                               │              corrects / rejects│
   │                               │◄───────────────────────────────┤
   │                               │ acceptance writes the CASE     │
   │                               │ record with provenance:        │
   │                               │   source: imported             │
   │                               │   extraction_id: candidate id  │
   │                               │   confirmed_by / confirmed_at  │
   │  check_proposals              │ and external_refs carried over │
   ├──────────────────────────────►│                                │
   │  {accepted, resultingRecordId}│                                │
   │◄──────────────────────────────┤                                │
```

Load-bearing points:

- **Candidates are the same shape extraction review needs.** An MCP proposal
  is an `extraction_candidate` row generalised: `document_id` becomes
  optional (an agent proposal has no source document) and an `origin` block
  records which OAuth client and which subject proposed it — attribution the
  way `uploaded_by` attributes a document. One review queue, one status
  vocabulary, one confirmation act; 8.9's UI reviews both streams without
  knowing which is which beyond the origin it displays.
- **Provenance `source` is `imported`.** The data model already rules on
  this: machine-supplied is machine-supplied, and the source system does not
  change who is signing the form. The store's invariant 2 (unconfirmed
  machine data cannot exist on a case record) would reject a leaked candidate
  even if the MCP service had a case-record write path — which it does not.
- **Confirmation is discovered by polling.** Review happens hours after the
  agent session ends, on a stateless transport, so `check_proposals` is the
  contract: the harness (or the attorney's next session) asks and gets
  status, the human's corrections, and the resulting record id. A corrected
  candidate is the harness's feedback signal, exactly as it is extraction's.
  Push (the 2026-07-28 `subscriptions/listen` stream) is not assumed until
  12.5 shows a harness that can hold one.
- **Withdrawal is the proposer's own.** Only the subject that proposed a
  candidate may withdraw it, and only while `pending` — a wrong batch should
  not sit in a paralegal's review queue. `withdrawn` is a new terminal status
  beside the model's `pending | accepted | corrected | rejected`; withdrawn
  candidates are retained like rejected ones (they measure agent quality the
  same way corrections measure extraction quality).
- **Duplicate proposals are an annoyance, not corruption.** A retried
  `propose_case_records` creates duplicate pending candidates; the human
  rejects the extras. No idempotency token in v1 — added if 12.5 shows
  harness retry behaviour making it a real cost.
- The dedupe rule for creditors carries over: a proposed `creditor` matching
  an existing one by name-plus-address is a *suggestion to the reviewing
  human*, never an automatic merge.

## Pagination

Identical to the API's contract, because it is the same store underneath:
`limit` (1–100, default 25) plus an opaque `cursor`; `nextCursor` present in
the output only when there is a next page — absent, never null. Cursors are
bound to the listing that minted them (`by-firm` vs `by-assignee`, per
ADR 0009), so a permission change mid-pagination answers `validation_failed`
rather than silently skipping cases. A cursor is never decoded client-side.

## Error vocabulary

Three layers, matching where the spec puts each failure:

| Layer | Carries | Examples |
|---|---|---|
| HTTP | transport auth | 401 invalid/expired token (+ `WWW-Authenticate` with `resource_metadata`); 403 `insufficient_scope` (OAuth scope, not firm permissions) |
| JSON-RPC protocol errors | the call was malformed | unknown tool (−32602), unparseable arguments, unsupported protocol version, header mismatch (−32020) |
| Tool execution errors | the domain said no | `isError: true`, content = the message, `structuredContent.error` below |

Domain failures reuse `insolvia_core.errors` — the tool layer maps exception
classes to machine-readable codes exactly as the API layer maps them to
statuses, so the reasoning each class's docstring carries (anti-oracle 404,
honest 403, retryable 409) governs both surfaces:

```
structuredContent on error: { error: { code, message, fields? } }
```

| `insolvia_core.errors` | code | Semantics carried over |
|---|---|---|
| `ValidationError` | `validation_failed` | bad argument, bad cursor, oversized batch |
| `FieldValidationError` | `validation_failed` + `fields` | per-field messages, keyed by field path |
| `NotFoundError` | `not_found` | does not exist **or** not the caller's to know — one answer, on purpose |
| `ForbiddenError` | `permission_denied` | no firm, disabled user, or a feature the firm has not granted — a fact about the caller's own account |
| `ConflictError` | `conflict` | the resource exists and the caller may see it, but its state refuses (e.g. withdrawing an already-reviewed candidate) |

Unexpected exceptions are a generic `internal` tool error with no detail —
GLBA logging rules apply to this service exactly as to the API: one JSON line
per request, metadata only, never payloads.

## Limits

- Request body ≤ 256 KiB (the debtor route's ceiling — proposals carry
  entity-sized payloads).
- 1–25 proposals per `propose_case_records` call.
- `limit` ≤ 100 on every listing.
- Rate limiting is a spec MUST for tool invocations; the mechanism belonged
  to 12.3, which chose API Gateway stage throttling (10 req/s sustained,
  bursts to 20 — half the API's numbers, ahead of the Lambda so a
  retry-happy harness gets 429s before consuming concurrency;
  `infra/modules/mcp_service`).

## Constraints from harnesses

12.5 verifies this design against Claude Desktop, ChatGPT, and an MCP
inspector. This section stays open; findings land here (and revise the
surface) rather than in 12.5's issue thread:

- **Registration mechanics.** Can each harness complete the OAuth flow
  against a pre-registered Cognito app client (no DCR, no CIMD)? If a major
  harness requires DCR, 12.2 needs a registration facade — measure first.
- **Protocol era.** Which spec revision does each harness actually speak —
  the 2026-07-28 stateless shape, or the `initialize`/`Mcp-Session-Id`
  handshake era the SDK must bridge?
- **Context budget.** Does an 8-tool surface with a 23-value enum fit the
  harnesses' tool-listing budgets, and does the generic-record design
  actually help or hinder the model's tool selection?
- **Output tolerance.** Are full-record listings (a claim with notice
  parties, a debtor with provenance) within what harnesses render usefully,
  or do we need a summary shape?
- **Polling ergonomics.** Do agents actually re-ask `check_proposals`, or
  does confirmation feedback need a different channel (`subscriptions/listen`
  support, or surfacing it to the *attorney* in-app instead)?
- **Retry behaviour.** Do harness retries create duplicate proposals often
  enough to justify an idempotency token?
- **Document hunger.** Do harnesses want to push PMS documents at us (an
  upload path candidate-review can't express yet)?

## Not here, on purpose

- **OAuth flow mechanics, scopes, token lifetimes** — 12.2
  ([#261](https://github.com/insolvia-ai/insolvia/issues/261)), inside the
  constraints above.
- **Service scaffolding, Lambda/API Gateway wiring, all three environments** —
  12.3 ([#262](https://github.com/insolvia-ai/insolvia/issues/262)), placed by
  [ADR 0016](../adr/0016-mcp-server-is-its-own-service.md).
- **The review UI** that accepts, corrects, or rejects candidates — the
  extraction-review work (8.9); this surface only feeds its queue.
- **Candidate item shapes in the store** — owned by
  [`case-data-model.md`](case-data-model.md); the `origin` block and the
  `withdrawn` status defined above amend it when 12.3 lands them.

## Related

- [ADR 0013](../adr/0013-mcp-server-replaces-direct-pms-integration.md) — why
  an MCP server exists at all
- [ADR 0016](../adr/0016-mcp-server-is-its-own-service.md) — where it runs
- [`case-data-model.md`](case-data-model.md) — every entity these tools expose
- [ADR 0001](../adr/0001-client-stays-dumb-trust-boundary.md) ·
  [ADR 0009](../adr/0009-a-case-belongs-to-a-firm.md) ·
  [ADR 0012](../adr/0012-shared-python-domain-package.md)
- [MCP specification 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/)
