// The MCP server over raw JSON-RPC (docs/reference/mcp-surface.md; the
// protocol revision backend/tests/Insolvia.Mcp.Tests/ProtocolTests.cs pins):
// protected-resource metadata, the 401 challenge, initialize, the static
// eight-tool listing, every tool, the error envelope — and the candidate
// flow across services, proposed here and reviewed through the API.
import { expect, it } from '@jest/globals';

import { firmAdmin, scratchCase } from '../support/actors.ts';
import { env, seeded } from '../support/env.ts';
import { fixtureCase } from '../support/fixtures.ts';
import { mcpToken, staffToken } from '../support/tokens.ts';
import { offlineOnly } from '../support/gates.ts';
import { expectShape, withMessage } from '../support/wire.ts';

// The MCP server signs in with tokens only the run's own key mints (Google ID
// tokens for admin, the MCP harness's client for MCP); #583 brings it to the
// deployed target, through its own client.
const NOT_YET_DEPLOYED = 'signs with the run’s own key; the deployed MCP server target is #583';

const PROTOCOL_VERSION = '2026-07-28';
const META = {
  'io.modelcontextprotocol/protocolVersion': PROTOCOL_VERSION,
  'io.modelcontextprotocol/clientInfo': { name: 'insolvia-contract', version: '0' },
  'io.modelcontextprotocol/clientCapabilities': {},
};
const TOOLS = [
  'whoami',
  'list_cases',
  'get_case',
  'list_case_records',
  'get_case_record',
  'propose_case_records',
  'check_proposals',
  'withdraw_proposal',
];
const READ_ONLY = new Set([
  'whoami',
  'list_cases',
  'get_case',
  'list_case_records',
  'get_case_record',
  'check_proposals',
]);

let rpcId = 0;

async function rpc(
  method: string,
  params: Record<string, unknown> = {},
  options: { token?: string | null; headers?: Record<string, string>; legacy?: boolean } = {},
): Promise<{ status: number; headers: Headers; body: Record<string, unknown> | null }> {
  const token = options.token === undefined ? mcpToken(seeded.firmAdmin.subject) : options.token;
  const response = await fetch(env.mcpUrl, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json, text/event-stream',
      // A legacy (initialize-era) harness sends neither header nor envelope.
      ...(options.legacy ? {} : { 'MCP-Protocol-Version': PROTOCOL_VERSION, 'Mcp-Method': method }),
      ...(token === null ? {} : { Authorization: `Bearer ${token}` }),
      ...options.headers,
    },
    body: JSON.stringify({
      jsonrpc: '2.0',
      id: ++rpcId,
      method,
      params: options.legacy ? params : { ...params, _meta: META },
    }),
  });
  const text = await response.text();
  return {
    status: response.status,
    headers: response.headers,
    body: text === '' ? null : (JSON.parse(text) as Record<string, unknown>),
  };
}

interface ToolResult {
  readonly isError?: boolean;
  readonly structuredContent: Record<string, unknown>;
  readonly content: readonly { type: string; text: string }[];
}

async function call(
  tool: string,
  args: Record<string, unknown>,
  token?: string,
): Promise<ToolResult> {
  const response = await rpc(
    'tools/call',
    { name: tool, arguments: args },
    { headers: { 'Mcp-Name': tool }, ...(token === undefined ? {} : { token }) },
  );
  withMessage(JSON.stringify(response.body), () => expect(response.status).toBe(200));
  expectShape(response.body, { jsonrpc: 'string', id: 'integer', result: 'object' });
  const result = (response.body as { result: ToolResult }).result;
  expectShape(result, {
    structuredContent: 'object',
    content: [{ type: 'string', text: 'string' }],
  });
  // The text block is the same JSON, for harnesses without structured output.
  expect(JSON.parse(result.content[0]!.text)).toEqual(result.structuredContent);
  return result;
}

async function refusal(tool: string, args: Record<string, unknown>, code: string, token?: string) {
  const result = await call(tool, args, token);
  expect(result.isError).toBe(true);
  expectShape(result.structuredContent, {
    error: { code: 'string', message: 'string', 'fields?': 'object' },
  });
  const error = (result.structuredContent as { error: { code: string } }).error;
  expect(error.code).toBe(code);
  return error as { code: string; message: string; fields?: Record<string, string> };
}

const CASE_SHAPE = {
  id: 'string',
  chapter: 'integer',
  district: 'string',
  status: 'string',
  createdAt: 'string',
} as const;

offlineOnly(NOT_YET_DEPLOYED).describe('transport', () => {
  it('publishes RFC 9728 protected-resource metadata naming the issuer', async () => {
    const url = new URL(env.mcpUrl);
    const response = await fetch(
      `${url.origin}/.well-known/oauth-protected-resource${url.pathname}`,
    );
    expect(response.status).toBe(200);
    const metadata = (await response.json()) as {
      resource: unknown;
      authorization_servers: unknown;
    };
    expectShape(metadata, {
      resource: 'string',
      authorization_servers: ['string'],
      bearer_methods_supported: ['string'],
    });
    expect(metadata.resource).toBe(env.mcpUrl);
    expect(metadata.authorization_servers).toEqual([env.issuerUrl]);
  });

  it('challenges a missing, expired, or other-client token with a 401', async () => {
    for (const token of [
      null,
      mcpToken(seeded.firmAdmin.subject, { exp: Math.floor(Date.now() / 1000) - 600 }),
      // The app's own token: valid, but not issued to an MCP client.
      staffToken(seeded.firmAdmin.subject),
    ]) {
      const response = await rpc('tools/list', {}, { token });
      expect(response.status).toBe(401);
      const challenge = response.headers.get('www-authenticate') ?? '';
      expect(challenge.startsWith('Bearer ')).toBe(true);
      expect(challenge).toContain('resource_metadata=');
    }
  });

  it('rejects a header/body mismatch at the protocol layer', async () => {
    const response = await rpc(
      'tools/call',
      { name: 'whoami', arguments: {} },
      { headers: { 'Mcp-Name': 'somebody_else' } },
    );
    expect(response.status).toBe(400);
    expect((response.body as { error: { code: number } }).error.code).toBe(-32020);
  });
});

offlineOnly(NOT_YET_DEPLOYED).describe('the protocol', () => {
  it('server/discover names the supported revisions and the tool capability', async () => {
    // Revision 2026-07-28 has no handshake: discovery is a plain request.
    const response = await rpc('server/discover');
    withMessage(JSON.stringify(response.body), () => expect(response.status).toBe(200));
    expectShape(response.body, {
      jsonrpc: 'string',
      id: 'integer',
      result: { supportedVersions: ['string'], capabilities: { tools: 'object' } },
    });
    const result = (response.body as { result: { supportedVersions: string[] } }).result;
    expect(result.supportedVersions).toContain(PROTOCOL_VERSION);
  });

  it('initialize still answers an initialize-era harness', async () => {
    const response = await rpc(
      'initialize',
      {
        protocolVersion: '2025-06-18',
        capabilities: {},
        clientInfo: { name: 'insolvia-contract', version: '0' },
      },
      { legacy: true },
    );
    withMessage(JSON.stringify(response.body), () => expect(response.status).toBe(200));
    expectShape(response.body, {
      jsonrpc: 'string',
      id: 'integer',
      result: {
        protocolVersion: 'string',
        capabilities: { tools: 'object' },
        serverInfo: { name: 'string', version: 'string' },
      },
    });
  });

  it('tools/list is the static eight, each with an output schema and annotations', async () => {
    const response = await rpc('tools/list');
    expect(response.status).toBe(200);
    const tools = (response.body as { result: { tools: Record<string, unknown>[] } }).result.tools;
    expect(tools.map((t) => t.name)).toEqual(TOOLS);
    for (const tool of tools) {
      expectShape(tool, {
        name: 'string',
        'description?': 'string',
        inputSchema: 'object',
        outputSchema: 'object',
        annotations: { readOnlyHint: 'boolean' },
      });
      withMessage(String(tool.name), () =>
        expect((tool.annotations as { readOnlyHint: boolean }).readOnlyHint).toBe(
          READ_ONLY.has(String(tool.name)),
        ),
      );
    }
  });

  it('an unknown tool is a protocol-level error, not a tool result', async () => {
    const response = await rpc(
      'tools/call',
      { name: 'no_such_tool', arguments: {} },
      { headers: { 'Mcp-Name': 'no_such_tool' } },
    );
    const body = response.body as { error?: unknown; result?: { isError?: boolean } };
    expect(body.error !== undefined || body.result?.isError === true).toBe(true);
  });
});

offlineOnly(NOT_YET_DEPLOYED).describe('the eight tools', () => {
  it('whoami names the caller’s firm and permissions', async () => {
    const result = await call('whoami', {});
    expectShape(result.structuredContent, {
      firm: { id: 'string', name: 'string' },
      displayName: 'string',
      isAdmin: 'boolean',
      accessAllCases: 'boolean',
      permissions: 'object',
    });
    expect((result.structuredContent.firm as { name: string }).name).toBe('Dev Firm');

    // A signed-in stranger has no firm: whoami says so instead of refusing.
    const stranger = await call('whoami', {}, mcpToken(crypto.randomUUID()));
    expect(stranger.structuredContent.firm).toBeNull();
    await refusal('list_cases', {}, 'permission_denied', mcpToken(crypto.randomUUID()));
  });

  it('list_cases, get_case, list_case_records and get_case_record read the fixture', async () => {
    const fixture = await fixtureCase(13);
    const page = await call('list_cases', { limit: 1 });
    expectShape(page.structuredContent, { cases: [CASE_SHAPE], 'nextCursor?': 'string' });
    expect(typeof page.structuredContent.nextCursor).toBe('string');
    const next = await call('list_cases', {
      limit: 100,
      cursor: page.structuredContent.nextCursor,
    });
    expectShape(next.structuredContent, { cases: [CASE_SHAPE] });
    await refusal('list_cases', { cursor: 'not-a-cursor' }, 'validation_failed');

    const one = await call('get_case', { caseId: fixture.id });
    expectShape(one.structuredContent, { case: CASE_SHAPE, recordCounts: 'object' });
    const counts = one.structuredContent.recordCounts as Record<string, number>;
    expect(counts.creditors).toBeGreaterThan(0);
    await refusal('get_case', { caseId: 'no-such-case' }, 'not_found');

    const records = await call('list_case_records', {
      caseId: fixture.id,
      entityType: 'creditors',
      limit: 100,
    });
    expectShape(records.structuredContent, {
      records: [{ id: 'string', case_id: 'string', provenance: 'object' }],
      'nextCursor?': 'string',
    });
    const first = (records.structuredContent.records as { id: string }[])[0]!;
    // The same record the API serves, field for field.
    const viaApi = await firmAdmin().api.getCaseEntity(fixture.id, 'creditors', first.id);
    const record = await call('get_case_record', {
      caseId: fixture.id,
      entityType: 'creditors',
      recordId: first.id,
    });
    expectShape(record.structuredContent, { record: { id: 'string' } });
    expect((record.structuredContent.record as { name: string }).name).toBe(viaApi.name);

    // The case's clients, last four of the tax id only.
    const clients = await call('list_case_records', { caseId: fixture.id, entityType: 'clients' });
    expectShape(clients.structuredContent, {
      records: [{ id: 'string', name: 'object', 'tax_id_last_four?': 'string' }],
    });
    await refusal(
      'get_case_record',
      { caseId: fixture.id, entityType: 'creditors', recordId: 'no-such-record' },
      'not_found',
    );
  });

  it('propose → check → (review in the API) → check; and withdraw', async () => {
    const { caseId } = await scratchCase();
    const proposed = await call('propose_case_records', {
      caseId,
      proposals: [
        { entityType: 'creditors', payload: { name: 'Proposed Bank' }, note: 'from the PMS' },
        { entityType: 'creditors', payload: { name: 'Withdrawn Bank' } },
      ],
    });
    expectShape(proposed.structuredContent, {
      candidates: [{ candidateId: 'string', entityType: 'string', status: 'string' }],
    });
    const [kept, dropped] = proposed.structuredContent.candidates as {
      candidateId: string;
      status: string;
    }[];
    expect(kept!.status).toBe('pending');

    const withdrawn = await call('withdraw_proposal', {
      caseId,
      candidateId: dropped!.candidateId,
    });
    expectShape(withdrawn.structuredContent, { candidateId: 'string', status: 'string' });
    expect(withdrawn.structuredContent.status).toBe('withdrawn');
    await refusal('withdraw_proposal', { caseId, candidateId: dropped!.candidateId }, 'conflict');

    // The firm reviews the agent's proposal in the API's queue.
    const admin = firmAdmin();
    const queue = await admin.api.listExtractionCandidates(caseId, 'pending');
    const candidate = queue.find((c) => c.id === kept!.candidateId);
    expect(candidate?.origin.channel).toBe('mcp');
    const reviewed = await admin.api.reviewExtractionCandidate(caseId, kept!.candidateId, {
      action: 'accept',
      correctedPayload: { name: 'Corrected Bank' },
    });
    admin.wire(200, { candidate: 'object', record: { id: 'string' } });
    expect(reviewed.candidate.status).toBe('corrected');

    const checked = await call('check_proposals', { caseId });
    expectShape(checked.structuredContent, {
      candidates: [
        {
          candidateId: 'string',
          entityType: 'string',
          status: 'string',
          'confirmedBy?': 'string',
          'confirmedAt?': 'string',
          'correctedPayload?': 'object',
          'resultingRecordId?': 'string',
        },
      ],
      'nextCursor?': 'string',
    });
    const outcome = (
      checked.structuredContent.candidates as {
        candidateId: string;
        resultingRecordId?: string;
        status: string;
      }[]
    ).find((c) => c.candidateId === kept!.candidateId);
    expect(outcome?.status).toBe('corrected');
    const created = await call('get_case_record', {
      caseId,
      entityType: 'creditors',
      recordId: outcome!.resultingRecordId!,
    });
    expect((created.structuredContent.record as { name: string }).name).toBe('Corrected Bank');

    const invalid = await refusal(
      'propose_case_records',
      { caseId, proposals: [{ entityType: 'creditors', payload: { name: 42 } }] },
      'validation_failed',
    );
    expect(Object.keys(invalid.fields ?? {})).toContain('proposals[0].payload.name');
  });
});
