// The MCP server through the official TypeScript SDK (docs/reference/
// mcp-surface.md; ADR 0029 rule 2): protected-resource metadata, the 401
// challenge, the handshake, the static eight-tool listing, every tool, the
// error envelope — and the candidate flow across services, proposed here and
// reviewed through the API.
//
// Signed in on both targets. Offline, the run's own key mints the MCP
// client's token; deployed, the seeded firm admin signs in by SRP through an
// MCP harness's real app client, and the server admits that token only if
// the client is on its allowlist (AUTH_CLIENT_IDS) — #583 verified it is.
//
// The 2026-07-28 transport (server/discover, the Mcp-Method and Mcp-Name
// headers and their mismatch refusal) is not here: no released TypeScript SDK
// speaks it, and ProtocolTests.cs owns it (support/mcp.ts says so).
import { afterAll, beforeAll, describe, expect, it } from '@jest/globals';
import {
  discoverOAuthProtectedResourceMetadata,
  extractWWWAuthenticateParams,
} from '@modelcontextprotocol/sdk/client/auth.js';
import { StreamableHTTPError } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { SUPPORTED_PROTOCOL_VERSIONS } from '@modelcontextprotocol/sdk/types.js';

import { firmAdmin, scratchCase, tokenOf } from '../support/actors.ts';
import { env, seeded } from '../support/env.ts';
import { fixtureCase } from '../support/fixtures.ts';
import { offlineOnly } from '../support/gates.ts';
import { connectMcp, type McpConnection, mcpTokenOfFirmAdmin } from '../support/mcp.ts';
import { mcpToken } from '../support/tokens.ts';
import { expectShape, withMessage } from '../support/wire.ts';

const MINTS = 'mints a token the real pool never issued (ADR 0029)';

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

const CASE_SHAPE = {
  id: 'string',
  chapter: 'integer',
  district: 'string',
  status: 'string',
  createdAt: 'string',
} as const;

function metadataUrl(): string {
  const url = new URL(env.mcpUrl);
  return `${url.origin}/.well-known/oauth-protected-resource${url.pathname}`;
}

/** Connecting as `token` must fail on a 401 carrying the RFC 9728 challenge. */
async function expectChallenged(token: string | null): Promise<void> {
  const responses: Response[] = [];
  let caught: unknown;
  try {
    await (await connectMcp(token, responses)).close();
  } catch (error) {
    caught = error;
  }
  withMessage('the SDK connected', () => expect(caught).toBeInstanceOf(StreamableHTTPError));
  expect((caught as StreamableHTTPError).code).toBe(401);
  const refused = responses.at(-1)!;
  expect(refused.headers.get('www-authenticate')?.startsWith('Bearer ')).toBe(true);
  // What a harness reads to find the authorization server, by the SDK's parser.
  expect(extractWWWAuthenticateParams(refused).resourceMetadataUrl?.href).toBe(metadataUrl());
}

let mcp: McpConnection;

beforeAll(async () => {
  mcp = await connectMcp(await mcpTokenOfFirmAdmin());
});

afterAll(async () => {
  await mcp?.close();
});

describe('mcp: transport', () => {
  it('publishes RFC 9728 protected-resource metadata naming the issuer', async () => {
    const metadata = await discoverOAuthProtectedResourceMetadata(env.mcpUrl);
    expectShape(metadata, {
      resource: 'string',
      authorization_servers: ['string'],
      bearer_methods_supported: ['string'],
    });
    expect(metadata.resource).toBe(env.mcpUrl);
    expect(metadata.authorization_servers).toEqual([env.issuerUrl]);
  });

  it('challenges a harness with no token', async () => {
    await expectChallenged(null);
  });

  // The app's own token: valid, from the same pool, but not issued to an MCP
  // client — the allowlist keeps the two disjoint (mcp-surface.md).
  it('challenges the app’s own token, which no MCP client was issued', async () => {
    await expectChallenged(tokenOf('admin'));
  });

  offlineOnly(MINTS).it('challenges an expired MCP token', async () => {
    await expectChallenged(
      mcpToken(seeded.firmAdmin.subject, { exp: Math.floor(Date.now() / 1000) - 600 }),
    );
  });
});

describe('mcp: the protocol', () => {
  it('the handshake negotiates a revision the SDK speaks, and the tools capability', () => {
    expect(SUPPORTED_PROTOCOL_VERSIONS).toContain(mcp.transport.protocolVersion);
    expectShape(mcp.client.getServerVersion(), { name: 'string', version: 'string' });
    expect(mcp.client.getServerCapabilities()?.tools).toBeDefined();
  });

  it('tools/list is the static eight, each with an output schema and annotations', async () => {
    const tools = await mcp.listTools();
    expect(tools.map((t) => t.name)).toEqual(TOOLS);
    for (const tool of tools) {
      expectShape(tool, {
        name: 'string',
        'description?': 'string',
        inputSchema: 'object',
        outputSchema: 'object',
        annotations: { readOnlyHint: 'boolean' },
      });
      withMessage(tool.name, () =>
        expect(tool.annotations?.readOnlyHint).toBe(READ_ONLY.has(tool.name)),
      );
    }
  });

  it('an unknown tool is a protocol-level error, not a tool result', async () => {
    const outcome = await mcp.client
      .callTool({ name: 'no_such_tool', arguments: {} })
      .then((result) => ({ result }))
      .catch((error: unknown) => ({ error }));
    expect('error' in outcome || outcome.result.isError === true).toBe(true);
  });
});

describe('mcp: the eight tools', () => {
  it('whoami names the caller’s firm and permissions', async () => {
    const result = await mcp.call('whoami', {});
    expectShape(result.structuredContent, {
      firm: { id: 'string', name: 'string' },
      displayName: 'string',
      isAdmin: 'boolean',
      accessAllCases: 'boolean',
      permissions: 'object',
    });
    expect((result.structuredContent.firm as { name: string }).name).toBe(
      seeded.firmAdmin.firmName,
    );
  });

  offlineOnly(MINTS).it('a signed-in stranger has no firm: whoami says so', async () => {
    const stranger = await connectMcp(mcpToken(crypto.randomUUID()));
    try {
      const result = await stranger.call('whoami', {});
      expect(result.structuredContent.firm).toBeNull();
      await stranger.refuse('list_cases', {}, 'permission_denied');
    } finally {
      await stranger.close();
    }
  });

  it('list_cases, get_case, list_case_records and get_case_record read the fixture', async () => {
    const fixture = await fixtureCase(13);
    const page = await mcp.call('list_cases', { limit: 1 });
    expectShape(page.structuredContent, { cases: [CASE_SHAPE], 'nextCursor?': 'string' });
    expect(typeof page.structuredContent.nextCursor).toBe('string');
    const next = await mcp.call('list_cases', {
      limit: 100,
      cursor: page.structuredContent.nextCursor,
    });
    expectShape(next.structuredContent, { cases: [CASE_SHAPE] });
    await mcp.refuse('list_cases', { cursor: 'not-a-cursor' }, 'validation_failed');

    const one = await mcp.call('get_case', { caseId: fixture.id });
    expectShape(one.structuredContent, { case: CASE_SHAPE, recordCounts: 'object' });
    const counts = one.structuredContent.recordCounts as Record<string, number>;
    expect(counts.creditors).toBeGreaterThan(0);
    await mcp.refuse('get_case', { caseId: 'no-such-case' }, 'not_found');

    const records = await mcp.call('list_case_records', {
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
    const record = await mcp.call('get_case_record', {
      caseId: fixture.id,
      entityType: 'creditors',
      recordId: first.id,
    });
    expectShape(record.structuredContent, { record: { id: 'string' } });
    expect((record.structuredContent.record as { name: string }).name).toBe(viaApi.name);

    // The case's clients, last four of the tax id only.
    const clients = await mcp.call('list_case_records', {
      caseId: fixture.id,
      entityType: 'clients',
    });
    expectShape(clients.structuredContent, {
      records: [{ id: 'string', name: 'object', 'tax_id_last_four?': 'string' }],
    });
    await mcp.refuse(
      'get_case_record',
      {
        caseId: fixture.id,
        entityType: 'creditors',
        recordId: 'no-such-record',
      },
      'not_found',
    );
  });

  it('propose → check → (review in the API) → check; and withdraw', async () => {
    const { caseId } = await scratchCase();
    const proposed = await mcp.call('propose_case_records', {
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

    const withdrawn = await mcp.call('withdraw_proposal', {
      caseId,
      candidateId: dropped!.candidateId,
    });
    expectShape(withdrawn.structuredContent, { candidateId: 'string', status: 'string' });
    expect(withdrawn.structuredContent.status).toBe('withdrawn');
    await mcp.refuse(
      'withdraw_proposal',
      { caseId, candidateId: dropped!.candidateId },
      'conflict',
    );

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

    const checked = await mcp.call('check_proposals', { caseId });
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
    const created = await mcp.call('get_case_record', {
      caseId,
      entityType: 'creditors',
      recordId: outcome!.resultingRecordId!,
    });
    expect((created.structuredContent.record as { name: string }).name).toBe('Corrected Bank');

    const invalid = await mcp.refuse(
      'propose_case_records',
      {
        caseId,
        proposals: [{ entityType: 'creditors', payload: { name: 42 } }],
      },
      'validation_failed',
    );
    expect(Object.keys(invalid.fields ?? {})).toContain('proposals[0].payload.name');
  });
});
