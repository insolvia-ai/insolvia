// Public routes, the caller's own identity, and the fail-closed 401 matrix.
import { ApiUnauthorizedException } from '@insolvia-ai/api-client';
import { describe, expect, it } from '@jest/globals';

import { firmAdmin, seededStaff, tokenOf } from '../support/actors.ts';
import { contractClient } from '../support/client.ts';
import { seeded, subjectOf } from '../support/env.ts';
import { offlineOnly } from '../support/gates.ts';
import { EXPECTED_API_ENVIRONMENT, PEOPLE } from '../support/target.ts';
import { mcpToken, portalToken, staffToken } from '../support/tokens.ts';
import { expectApiError, expectShape } from '../support/wire.ts';

const PRINCIPAL = {
  subject: 'string',
  username: 'string|null',
  clientId: 'string',
  scopes: ['string'],
  expiresAt: 'integer|null',
  'firm?': {
    id: 'string',
    name: 'string',
    role: 'string',
    firstName: 'string',
    lastName: 'string',
    displayName: 'string',
    isAdmin: 'boolean',
  },
} as const;

const STAFF = PEOPLE.filter((p) => p.kind === 'staff');

describe('public routes', () => {
  it('health answers without a token, naming the environment this run targets', async () => {
    const client = contractClient();
    const health = await client.api.health();
    expect(client.last().status).toBe(200);
    expectShape(client.last().body, {
      status: 'string',
      service: 'string',
      version: 'string',
      environment: 'string',
    });
    expect(health.status).toBe('ok');
    expect(health.service).toBe('insolvia-api');
    // A green run against the wrong environment is worse than a red one: the
    // one field that cannot be right by accident.
    expect(health.environment).toBe(EXPECTED_API_ENVIRONMENT);
  });

  it('the waitlist accepts a submission and validates by field', async () => {
    const client = contractClient();
    const confirmation = await client.api.joinWaitlist({
      name: 'Contract Suite',
      firm: 'Contract & Co',
      email: 'waitlist@insolvia.test',
      currentSoftware: 'none',
    });
    expect(client.last().status).toBe(201);
    expectShape(client.last().body, { id: 'string', submittedAt: 'string' });
    expect(confirmation.id).not.toBe('');

    const refused = await expectApiError(
      client.api.joinWaitlist({ name: '', firm: '', email: 'not-an-address' }),
      400,
      'ValidationError',
    );
    expect(Object.keys(refused.fields ?? {})).toEqual(
      expect.arrayContaining(['name', 'firm', 'email']),
    );
  });
});

describe('who am I', () => {
  it('every seeded firm user resolves to the firm the seed puts them in', async () => {
    // Where a broken seed (an account with no membership, or a pool recreated
    // under firm rows naming an old subject) shows as a missing `firm` block.
    for (const who of STAFF) {
      const client = seededStaff(who.handle);
      const me = await client.api.me();
      client.wire(200, PRINCIPAL);
      expect(me.subject).toBe(subjectOf(who.handle));
      expect(me.firm?.name).toBe(who.firmName);
      expect(me.firm?.isAdmin).toBe(who.isAdmin);
      expect(me.firm?.role).toBe(who.role);
    }
    // …and the firm's staff list holds every colleague the seed names.
    const listed = (await firmAdmin().api.listFirmUsers()).map((u) => u.email);
    const colleagues = STAFF.filter((p) => p.firmName === seeded.firmAdmin.firmName);
    expect(listed).toEqual(expect.arrayContaining(colleagues.map((p) => p.email)));
  });

  offlineOnly('signs a token for a subject no pool holds').it(
    'a signed-in stranger is authenticated but has no firm',
    async () => {
      const client = contractClient(staffToken(crypto.randomUUID()));
      const me = await client.api.me();
      expectShape(client.last().body, PRINCIPAL);
      expect(me.firm).toBeUndefined();
      expect(client.last().body).not.toHaveProperty('firm');
      // …and every firm-scoped route answers 403, not 401.
      await expectApiError(client.api.listCases(), 403, 'ForbiddenError');
    },
  );

  it('updateMe changes the caller’s own name and signature block', async () => {
    const client = firmAdmin();
    const before = await client.api.me();
    const own = (await client.api.listFirmDirectory()).find((p) => p.subject === before.subject);
    const firstName = before.firm?.firstName ?? 'Contract';
    const lastName = before.firm?.lastName ?? 'Suite';
    try {
      const updated = await client.api.updateMe({
        firstName,
        lastName,
        signatureBlock: { bar_number: '12345', bar_state: 'FL', firm_name: 'Contract & Co' },
      });
      expect(client.last().status).toBe(200);
      expectShape(client.last().body, PRINCIPAL);
      expect(updated.firm?.displayName).toBe(`${firstName} ${lastName}`);
      await expectApiError(client.api.updateMe({ firstName: '' }), 400, 'ValidationError');
    } finally {
      // The seeded person is put back as the run found them.
      await client.api.updateMe({
        firstName,
        lastName,
        signatureBlock: (own?.signatureBlock ?? null) as never,
      });
    }
  });
});

describe('authentication fails closed', () => {
  it('the client refuses to send a protected call with no token', async () => {
    const client = contractClient();
    await expect(client.api.me()).rejects.toBeInstanceOf(ApiUnauthorizedException);
    expect(client.exchanges).toHaveLength(0);
  });

  it.each([
    ['a tampered signature', () => tokenOf('admin').slice(0, -4) + 'AAAA'],
    ['garbage', () => 'not-a-jwt'],
  ])('%s is a 401 with the error envelope', async (_label, token) => {
    const client = contractClient(token());
    const body = await expectApiError(client.api.me(), 401, 'Unauthorized');
    expect(body.message).toBe('authentication required');
  });

  offlineOnly('breaks one claim of a token signed with the run’s own key').describe(
    'a token with one claim broken',
    () => {
      it.each([
        [
          'an expired token',
          () => staffToken(subjectOf('admin'), { exp: Math.floor(Date.now() / 1000) - 600 }),
        ],
        [
          'another issuer',
          () => staffToken(subjectOf('admin'), { iss: 'https://cognito-idp.invalid/other' }),
        ],
        ['an ID token', () => staffToken(subjectOf('admin'), { token_use: 'id' })],
        ['the portal’s client', () => portalToken(subjectOf('admin'))],
        ['the MCP server’s client', () => mcpToken(subjectOf('admin'))],
      ])('%s is a 401 with the error envelope', async (_label, token) => {
        const client = contractClient(token());
        const body = await expectApiError(client.api.me(), 401, 'Unauthorized');
        expect(body.message).toBe('authentication required');
      });
    },
  );
});
