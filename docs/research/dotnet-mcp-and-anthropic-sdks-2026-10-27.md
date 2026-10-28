# .NET MCP and Anthropic SDKs — 2026-10-27

> **Research for [#515](https://github.com/insolvia-ai/insolvia/issues/515)**
> (plan 18.4 of the .NET + Postgres rewrite, ADR 0028 § Libraries). Can the
> official C# SDKs do what the Python service and worker do today, so that
> 18.17 (job pipeline: AI review and extraction) and 18.21 (MCP service) need
> no hand-written protocol or HTTP client? Two spikes, no network calls to
> Anthropic, no key. Code: [`backend/spikes/sdk/`](../../backend/spikes/sdk/)
> (standalone projects, in no solution; throwaway).

## 1. Summary

| SDK | Verdict | Version tested |
|---|---|---|
| MCP — `ModelContextProtocol.AspNetCore` | **Use it, with two small shims we own** (a JSON-response shim, a Host check) | 2.3.0 (net10.0; Python side `mcp==2.1.1`) |
| Anthropic — `Anthropic` | **Use it.** Request bodies are byte-for-byte JSON-equal to the Python adapters' | 13.3.0 (Python side `anthropic==1.3.0`) |

No hand-written JSON-RPC fallback is needed. If it ever were, its size is
sketched in § 2.6.

## 2. MCP

### 2.1 What was run

[`backend/spikes/sdk/mcp/`](../../backend/spikes/sdk/mcp/): an ASP.NET Core
host with two stub tools (`whoami`, `list_cases`, canned data), stateless,
with our own bearer verification. **The Python wire pin was ported, not
driven:** the protocol-level cases of
`services/mcp/tests/unit/test_protocol.py` became an xUnit class
(`tests/ProtocolTests.cs`) that sends the same real JSON-RPC POSTs (the
`2026-07-28` shape: `_meta` envelope, `MCP-Protocol-Version` / `Mcp-Method` /
`Mcp-Name`) with real RS256 tokens over a real Kestrel socket. Porting beat
driving the Python test because that test's fixtures are the Python domain
(`deps`, `firm_store`); only its HTTP calls matter, and those are reproduced
exactly.

A second check goes the other way:
[`python_client_interop.py`](../../backend/spikes/sdk/mcp/python_client_interop.py)
drives the C# server with the **Python SDK 2.1.1 client**, the nearest thing
to a real harness we have.

### 2.2 Revision and transport

- **Revision:** `test_protocol.py` pins `PROTOCOL_VERSION = "2026-07-28"`
  ([`mcp-surface.md`](../reference/mcp-surface.md) cites the same revision).
  C# SDK 2.3.0 serves it: `server/discover` answers
  `supportedVersions: ["2026-07-28"]` and every result carries
  `resultType: "complete"` and the `serverInfo` `_meta` stamp, as the revision
  requires.
- **Stateless is the default** (`HttpServerSessionMode.Stateless`; the SDK
  documents it as the default "as of the 2026-07-28 revision"). GET and DELETE
  are refused. The legacy `initialize` handshake **also** works statelessly:
  the Python client in `mode="legacy"` negotiated `2025-11-25` and ran every
  call, which matches today's "SDK bridges older harnesses" posture.
- **Mcp-Method / Mcp-Name validation is built in.** A body/header mismatch is
  a 400 with JSON-RPC error `-32020`, exactly the pinned case.

### 2.3 Results

All run with `dotnet test` in `mcp/tests` (19 tests green) plus the interop
script (both modes green).

| Python case (`test_protocol.py`) | C# result |
|---|---|
| unauthenticated request is challenged (401, `Bearer`, `resource_metadata=`) | pass |
| protected-resource metadata names the authorization server (RFC 9728) | pass |
| wrong Host header rejected (421) | pass, **but via our middleware** (§ 2.4) |
| expired token is a 401 | pass |
| listing is the static set, with `outputSchema` and `readOnlyHint` | pass (needs `OutputSchemaType`, § 2.4) |
| listing identical for a permission-stripped caller | pass |
| `whoami` answers `structuredContent` + text block | pass |
| domain refusal carries `{error:{code,…}}` (`isError`) | pass (validation and permission variants) |
| permission refusal is `permission_denied` | pass |
| validation refusal names `fields` | pass |
| unknown tool is a protocol error | pass |
| header/body mismatch is rejected, `-32020`, 400 | pass, built in |
| candidate flow end to end; clients round trip | **not ported**: they test `insolvia_core`, not the protocol seam (18.21 ports them against the real stores) |

Extra cases the Python suite does not have, added because 18.21 depends on
them: an app-client token (different `client_id`), a wrong-key token and an
`id` token are each a 401 (the client-id allow-list as the audience check works
the same way); a 300 KiB body is refused (Kestrel limit, the 256 KiB ceiling in
`mcp-surface.md` § Limits); GET is refused.

### 2.4 Gaps and how they were closed

1. **No JSON-response mode.** The Python server runs `json_response=True`.
   C# SDK 2.3.0 has no such option: every POST answers `text/event-stream`
   with exactly one `data:` event (`RawSdkTests` pins this). The spec lets a
   client handle either (the Python 2.1.1 client was only run against the
   shimmed JSON form), but the service's posture and `CLAUDE.md` say JSON. **Closed** with
   `JsonResponseShim`, about 40 lines of middleware: buffer the response, and
   if it is a 200 `text/event-stream` with exactly one event, re-emit the
   payload as `application/json`. Anything else passes through. Stateless mode
   cannot emit a second message, so "exactly one" always holds; keep the
   fall-through anyway. Tested both ways (`Json_response_not_sse_with_the_shim`,
   `RawSdkTests`). **Risk to carry into 18.21:** the shim replaces
   `IHttpResponseBodyFeature`; verify it under the Lambda hosting adapter
   (§ 2.5), which buffers anyway.
2. **No DNS-rebinding protection.** The Python SDK pins `Host` (421) and
   validates `Origin` (present-and-unlisted is a 403). The C# SDK does neither.
   `Host` is **closed** with a ten-line middleware (421, patterns like
   `127.0.0.1:*` on local only; ASP.NET's `HostFiltering` would answer 400, a
   wire difference). **`Origin` validation is not ported or tested**: add it
   in 18.21 (the Python rule is "absent is fine, present and unlisted is 403").
3. **`outputSchema` for a tool that returns `CallToolResult`.** The Python
   service returns `Annotated[CallToolResult, Model]` to publish a typed
   schema while controlling `isError`. In C# a bare `CallToolResult` return
   advertises the schema *of `CallToolResult` itself* (a silent wrong answer).
   `[McpServerTool(OutputSchemaType = typeof(ListCasesResult))]` is the exact
   equivalent and is asserted in the listing test. Every error-envelope tool
   needs it.
4. **`resource_metadata` in the 401 challenge is derived from the request
   Host** (observed `http://127.0.0.1:8788/.well-known/…`). The metadata
   document's `resource` is the configured value, as in Python. Behind
   CloudFront and Lambda set `McpAuthenticationOptions.ResourceMetadataUri`
   explicitly instead of trusting the forwarded host.

### 2.5 The bearer-verifier hook

There is no `TokenVerifier` seam like Python's. The C# idiom is ASP.NET's
authentication pipeline, and it is a better fit: a custom
`AuthenticationHandler` (`InsolviaBearerHandler`) calls our `ITokenVerifier`
on every request (RS256, issuer, `token_use`, `client_id` allow-list, no
`aud`, the checks `insolvia_core.auth` makes), and `AddMcp(...)` supplies the
challenge scheme that writes `WWW-Authenticate` and serves
`/.well-known/oauth-protected-resource/mcp`. `MapMcp("/mcp").RequireAuthorization()`
puts it all in front of the SDK. A failed verification is a bare 401 with the
challenge; nothing about the token is logged. The spike verified with
`Microsoft.IdentityModel.JsonWebTokens` against a static key; 18.21 swaps in
the JWKS provider from 18.13 (the shared API host's verifier should be this
same class).

Tool code reads the verified subject from `HttpContext.User`
(`IHttpContextAccessor`), per call, with no caching, matching the "resolve the
accessor from the store every call" rule.

**Not tested here:** hosting on Lambda (`Amazon.Lambda.AspNetCoreServer`) behind
the existing function URL / API shape. That needs the real deploy path;
nearest local approximation is the Kestrel run above. 18.21 should add the
adapter and rerun `ProtocolTests` through it.

### 2.6 Fallback sketch (not needed)

A hand-written server would be roughly 300–400 lines: JSON-RPC envelope and
error mapping, `server/discover`, `tools/list`, `tools/call` with argument
schema validation, header/body validation (`-32020`), the 401 challenge and
RFC 9728 document, the legacy `initialize` bridge, and `resultType` /
`serverInfo` stamping. The SDK does all of it (plus tool schema generation
from C# signatures) for ~100 lines of our shims.

## 3. Anthropic

### 3.1 What was run

[`backend/spikes/sdk/anthropic/`](../../backend/spikes/sdk/anthropic/):

1. `capture_python_requests.py` runs the real `AnthropicReviewModel` and
   `AnthropicExtractionModel` (PDF and PNG) with the SDK's HTTP client on a
   mock transport, and records method, path, headers, timeout and JSON body
   into `fixtures/*.json`, alongside the real `REVIEW_SYSTEM_PROMPT`,
   `REVIEW_OUTPUT_SCHEMA` and the `credit_report` extraction prompt and schema.
2. `ModelAdapters.cs` builds the same requests with the C# SDK and the same
   client policy.
3. `WireParityTests.cs` replays the fixtures through a recording
   `HttpMessageHandler` injected as `AnthropicClient.HttpClient` and compares
   with `JsonNode.DeepEquals`. Nothing leaves the process.

### 3.2 What matched (10 tests green)

- **Messages API, structured outputs, content blocks.** The serialized body
  for the review (string user content, `system`, `output_config.format` =
  `{type: json_schema, schema}`), the extraction PDF (`document` block,
  base64 source, then the instruction `text` block) and the extraction PNG
  (`image` block) are **deep-equal to what Python sends**, including the
  complete prompts and schemas. `max_tokens` 16000, no `thinking`, no sampling
  parameters. Path `/v1/messages`.
- **Model id.** `claude-opus-5` goes through as a plain string (`Model` is a
  union that accepts a raw id). See § 3.4 on the model choice itself.
- **240 s timeout, one retry.** `Timeout = 240s`, `MaxRetries = 1` on the
  client. A 529/5xx then success is exactly 2 attempts; persistent 5xx stops at
  exactly 2; a per-attempt timeout (200 ms stand-in) is retried once, 2
  attempts; 401 and 403 are not retried and surface as
  `AnthropicUnauthorizedException` / `AnthropicForbiddenException`, the C#
  counterparts of `AuthenticationError` / `PermissionDeniedError`.
- **Response surface the adapters read:** `Model`, `StopReason` (including
  `refusal`), `Usage.InputTokens` / `OutputTokens`, text content via
  `TryPickText`. `HttpResponse.RequestID` exists for the log line
  (`WithRawResponse`); not exercised.

### 3.3 Differences to carry into 18.17

1. **`anthropic-version` header.** C# 13.3.0 sends `2026-10-21`; Python 1.3.0
   sends `2023-06-01`. The newer version rejects `temperature`, `top_p` and
   `top_k` with a 400 (and `document` blocks in assistant turns). Our two
   requests send none of these, and a test asserts it stays so. Do not add
   sampling parameters in the .NET port.
2. **v13 shape change.** `MessageParam` is now a union (`UserMessageParam`,
   `AssistantMessageParam`, `SystemMessageParam`); the older
   `new() { Role = Role.User, … }` form in docs and the claude-api skill's
   samples does not compile. Use `new UserMessageParam { Content = … }`.
3. **Timeout is a whole-call budget.** Python's `timeout=240.0` sets httpx
   connect/read/write/pool to 240 s each; C#'s `Timeout` bounds the complete
   call including reading the body, per attempt. For our non-streaming
   calls the worst case stays ≈ 2 × 240 s plus backoff, so the worker's
   900 s arithmetic in `review_model.py` still holds.
4. **Structured output schema** goes in as
   `OutputConfig.Format = new JsonOutputFormat { Schema = Dictionary<string, JsonElement> }`;
   the schema constants must be loaded as `JsonElement`s (the spike clones
   each top-level property).

### 3.4 Model note (for the maintainer, not for the spike)

The Python code and ADR 0019 pin `claude-opus-5` deliberately (Opus tier,
ZDR-eligible). The current Opus is `claude-opus-5-5` at a lower price; moving
to it changes behavior (thinking can no longer be disabled, effort defaults to
`medium`, forced `tool_choice` is a 400), none of which our two requests use
(they send neither `thinking` nor `tool_choice`, so both models run adaptive).
Keep it a single constant, as now; the spike's parity tests parametrize
nothing on it. Whether to move is an ADR 0019 decision, not part of the port.

### 3.5 Not verified

Live behavior (no key, by design): real refusals, `max_tokens` stops, and
whether the API accepts the `2026-10-21` version for these exact requests
(documented to; unproven here). 18.17 should run one real call per adapter
behind its local/staging seams before the switch.

## 4. Recommendation

- **18.21 (MCP service):** build on `ModelContextProtocol.AspNetCore` 2.3.0
  pinned exact. Own: the JSON-response shim, `Host` 421 and `Origin` 403
  checks, `OutputSchemaType` on every tool that returns `CallToolResult`, an
  explicit `ResourceMetadataUri`. Port `test_protocol.py` from
  `ProtocolTests.cs` plus the two domain cases against the real stores; keep
  `RawSdkTests` so an SDK upgrade that adds a JSON mode (or changes the SSE
  shape) breaks a test, which is the point of the wire pin. Run it through the
  Lambda adapter.
- **18.17 (job pipeline):** use `Anthropic` 13.3.0 pinned exact, the parity
  fixtures as the starting regression test (regenerate with the capture script
  if a prompt or schema changes; it is the only place the two languages must
  agree), `MaxRetries = 1` and `Timeout = 240s`. Do not add sampling
  parameters.

## 5. Reproduce

```bash
# Anthropic: wire parity (fixtures are committed; regenerate only if Python changes)
cd backend/spikes/sdk/anthropic && dotnet test
# regenerate: python3.12 -m venv v && v/bin/pip install -r services/api/requirements.txt -r services/api/requirements-worker.txt   (run from services/api)
#   cd services/api && ../../v/bin/python ../../backend/spikes/sdk/anthropic/capture_python_requests.py

# MCP: protocol cases
cd backend/spikes/sdk/mcp/tests && dotnet test

# MCP: Python 2.1.1 client against the C# server (venv: services/mcp/requirements.txt)
cd backend/spikes/sdk/mcp && dotnet run > run.log &
python python_client_interop.py run.log
```
