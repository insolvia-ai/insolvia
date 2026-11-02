// The MCP server through the official TypeScript SDK — what an agent's
// harness runs (ADR 0029 rule 2). `connectMcp` is the SDK's own `Client` over
// its Streamable HTTP transport, protocol setup included: `connect()` is the
// initialize handshake and version negotiation, exactly as Claude or the MCP
// Inspector make it. No JSON-RPC is built by hand.
//
// The SDK speaks revision 2025-11-25 (its latest), which the server bridges;
// the 2026-07-28 surface no released TypeScript SDK speaks yet (server/
// discover, the Mcp-Method and Mcp-Name headers) is pinned by the server's own
// protocol tests, backend/tests/Insolvia.Mcp.Tests/ProtocolTests.cs.
//
// Two instruments, as support/client.ts has for the API:
// - the transport's `fetch` records each HTTP response, so a spec can read
//   what the SDK itself does not surface — the 401's WWW-Authenticate
//   challenge, through the SDK's own parser;
// - `call`, `refuse` and `listTools` note each tool called and the server's
//   listing, for the mcp coverage gate (src/coverage.ts): every listed tool
//   called.
import { expect } from '@jest/globals';
import { Client } from '@modelcontextprotocol/sdk/client';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import type { Tool } from '@modelcontextprotocol/sdk/types.js';

import { noteCall, noteToolListing } from '../coverage.ts';
import { env, seeded } from './env.ts';
import { mcpSignIn } from './session.ts';
import { TARGET } from './target.ts';
import { mcpToken } from './tokens.ts';
import { expectShape, withMessage } from './wire.ts';

export interface ToolResult {
  readonly isError?: boolean;
  readonly structuredContent: Record<string, unknown>;
  readonly content: readonly { type: string; text: string }[];
}

export interface McpConnection {
  /** The SDK client itself, for what the protocol answers outside a tool. */
  readonly client: Client;
  readonly transport: StreamableHTTPClientTransport;
  /** Every HTTP response the transport received — status and headers — oldest first. */
  readonly responses: Response[];
  /** `tools/list`, noted for the gate. */
  listTools(): Promise<Tool[]>;
  /** `tools/call`, noted for the gate; asserts the result carries both forms. */
  call(tool: string, args: Record<string, unknown>): Promise<ToolResult>;
  /**
   * `tools/call` that the server must refuse with `code` in its error
   * envelope; returns the envelope. Through `callTool` after `tools/list`,
   * as an agent calls — see REFUSALS below.
   */
  refuse(
    tool: string,
    args: Record<string, unknown>,
    code: string,
  ): Promise<{ code: string; message: string; fields?: Record<string, string> }>;
  close(): Promise<void>;
}

function transportFor(token: string | null, responses: Response[]): StreamableHTTPClientTransport {
  return new StreamableHTTPClientTransport(new URL(env.mcpUrl), {
    ...(token === null ? {} : { requestInit: { headers: { Authorization: `Bearer ${token}` } } }),
    fetch: async (input, init) => {
      const response = await fetch(input, init);
      // Status and headers only, never a clone: an unread clone tees the
      // body, and the SDK's `await body.cancel()` on a 202 then waits for
      // the other branch forever.
      responses.push(new Response(null, { status: response.status, headers: response.headers }));
      return response;
    },
  });
}

/**
 * An SDK client, connected — the initialize handshake done — as the bearer
 * `token`, or with none. Rejects as the SDK does when the server refuses.
 */
export async function connectMcp(
  token: string | null,
  responses: Response[] = [],
): Promise<McpConnection> {
  const transport = transportFor(token, responses);
  const client = new Client({ name: 'insolvia-contract', version: '0' });
  await client.connect(transport);
  let listed: Promise<unknown> | undefined;
  return {
    client,
    transport,
    responses,
    async listTools() {
      const { tools } = await client.listTools();
      noteToolListing(tools.map((t) => t.name));
      return tools;
    },
    async call(tool, args) {
      noteCall('mcp', tool);
      return bothForms(
        tool,
        (await client.callTool({ name: tool, arguments: args })) as ToolResult,
      );
    },
    async refuse(tool, args, code) {
      noteCall('mcp', tool);
      // The SDK caches each tool's output validator from the listing; list
      // first so callTool checks the refusal as an agent's harness would.
      listed ??= client.listTools();
      await listed;
      const result = await client.callTool({ name: tool, arguments: args });
      return expectRefusal(result as unknown as RefusalResult, code);
    },
    close: () => client.close(),
  };
}

function bothForms(tool: string, result: ToolResult): ToolResult {
  withMessage(`${tool}: ${JSON.stringify(result)}`, () =>
    expectShape(result, {
      structuredContent: 'object',
      content: [{ type: 'string', text: 'string' }],
    }),
  );
  // The text block is the same JSON, for harnesses without structured output.
  expect(JSON.parse(result.content[0]!.text)).toEqual(result.structuredContent);
  return result;
}

// REFUSALS carry no `structuredContent`: `isError`, and the envelope
// `{"error": {code, message, fields?}}` as the text block's JSON — the
// spec's tool-error shape, since structured content MUST match the tool's
// outputSchema and no output schema declares the envelope. Once a client has
// listed the tools, `callTool` (SDK 1.32.2) validates any `structuredContent`
// it gets against that schema, error or not; when the server put the
// envelope there, every refusal reached such a harness (the MCP Inspector,
// most agents) as a -32602. `refuse` lists first, so it would again.
interface RefusalResult {
  readonly isError?: boolean;
  readonly structuredContent?: unknown;
  readonly content: readonly { type: string; text: string }[];
}

let deployedToken: Promise<string> | undefined;

/**
 * The seeded firm administrator's MCP bearer. Offline, minted for the run's
 * MCP client; deployed, a real SRP sign-in through an MCP harness's client
 * (CONTRACT_AUTH_MCP_CLIENT_ID), made once per test file.
 */
export async function mcpTokenOfFirmAdmin(): Promise<string> {
  if (TARGET === 'offline') return mcpToken(seeded.firmAdmin.subject);
  deployedToken ??= mcpSignIn('admin').then((r) => r.AccessToken);
  return deployedToken;
}

/** A tool result that must be a refusal with `code` in the error envelope. */
function expectRefusal(
  result: RefusalResult,
  code: string,
): { code: string; message: string; fields?: Record<string, string> } {
  withMessage(JSON.stringify(result), () => {
    expect(result.isError).toBe(true);
    expect(result).not.toHaveProperty('structuredContent');
    expectShape(result.content, [{ type: 'string', text: 'string' }]);
  });
  const envelope: unknown = JSON.parse(result.content[0]!.text);
  expectShape(envelope, {
    error: { code: 'string', message: 'string', 'fields?': 'object' },
  });
  const error = (envelope as { error: { code: string } }).error;
  withMessage(JSON.stringify(error), () => expect(error.code).toBe(code));
  return error as { code: string; message: string; fields?: Record<string, string> };
}
