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
// - `call` and `listTools` note each tool called and the server's listing,
//   for the mcp coverage gate (src/coverage.ts): every listed tool called.
import { expect } from '@jest/globals';
import { Client } from '@modelcontextprotocol/sdk/client';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { CallToolResultSchema, type Tool } from '@modelcontextprotocol/sdk/types.js';

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
   * envelope; returns the envelope. See REFUSALS below for why this is not
   * `callTool`.
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
      const result = (await client.request(
        { method: 'tools/call', params: { name: tool, arguments: args } },
        CallToolResultSchema,
      )) as ToolResult;
      return expectRefusal(bothForms(tool, result), code);
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

// REFUSALS go through the SDK's generic `request` for `tools/call`, not
// `callTool`. Once a client has listed the tools, `callTool` (SDK 1.32.2)
// validates `structuredContent` against the tool's `outputSchema` even on
// an `isError` result — its own comment says it should not — and this
// server's refusals carry `{"error": {...}}` there, which no output schema
// declares. So a TypeScript-SDK harness that lists first (the MCP Inspector
// does) sees every refusal as a -32602 instead of the envelope. Found by
// #583; the server-side fix is its own change, because it moves the wire.
// `request` is the same transport, handshake and result schema, minus that
// one check.
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
export function expectRefusal(
  result: ToolResult,
  code: string,
): { code: string; message: string; fields?: Record<string, string> } {
  withMessage(JSON.stringify(result.structuredContent), () => expect(result.isError).toBe(true));
  expectShape(result.structuredContent, {
    error: { code: 'string', message: 'string', 'fields?': 'object' },
  });
  const error = (result.structuredContent as { error: { code: string } }).error;
  withMessage(JSON.stringify(error), () => expect(error.code).toBe(code));
  return error as { code: string; message: string; fields?: Record<string, string> };
}
