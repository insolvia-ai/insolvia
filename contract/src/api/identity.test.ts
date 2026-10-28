// Public routes, the caller's own identity, and the fail-closed 401 matrix.
import { ApiUnauthorizedException } from '@insolvia-ai/api-client';
import { describe, expect, it } from 'vitest';

import { contractClient } from '../support/client.ts';
import { seeded } from '../support/env.ts';
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

describe('public routes', () => {
  it('health answers without a token', async () => {
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
  it('a seeded firm user resolves to their firm', async () => {
    const client = contractClient(staffToken(seeded.firmAdmin.subject));
    const me = await client.api.me();
    expect(client.last().status).toBe(200);
    expectShape(client.last().body, PRINCIPAL);
    expect(me.subject).toBe(seeded.firmAdmin.subject);
    expect(me.firm?.name).toBe('Dev Firm');
    expect(me.firm?.isAdmin).toBe(true);
  });

  it('a signed-in stranger is authenticated but has no firm', async () => {
    const client = contractClient(staffToken(crypto.randomUUID()));
    const me = await client.api.me();
    expectShape(client.last().body, PRINCIPAL);
    expect(me.firm).toBeUndefined();
    expect(client.last().body).not.toHaveProperty('firm');
    // …and every firm-scoped route answers 403, not 401.
    await expectApiError(client.api.listCases(), 403, 'ForbiddenError');
  });

  it('updateMe changes the caller’s own name and signature block', async () => {
    const client = contractClient(staffToken(seeded.firmAdmin.subject));
    const updated = await client.api.updateMe({
      firstName: 'Dev',
      lastName: 'User',
      signatureBlock: { bar_number: '12345', bar_state: 'FL', firm_name: 'Dev Firm' },
    });
    expect(client.last().status).toBe(200);
    expectShape(client.last().body, PRINCIPAL);
    expect(updated.firm?.displayName).toBe('Dev User');
    await expectApiError(client.api.updateMe({ firstName: '' }), 400, 'ValidationError');
  });
});

describe('authentication fails closed', () => {
  const subject = seeded.firmAdmin.subject;

  it('the client refuses to send a protected call with no token', async () => {
    const client = contractClient();
    await expect(client.api.me()).rejects.toBeInstanceOf(ApiUnauthorizedException);
    expect(client.exchanges).toHaveLength(0);
  });

  it.each([
    ['an expired token', () => staffToken(subject, { exp: Math.floor(Date.now() / 1000) - 600 })],
    ['another issuer', () => staffToken(subject, { iss: 'https://cognito-idp.invalid/other' })],
    ['an ID token', () => staffToken(subject, { token_use: 'id' })],
    ['the portal’s client', () => portalToken(subject)],
    ['the MCP server’s client', () => mcpToken(subject)],
    ['a tampered signature', () => staffToken(subject).slice(0, -4) + 'AAAA'],
    ['garbage', () => 'not-a-jwt'],
  ])('%s is a 401 with the error envelope', async (_label, token) => {
    const client = contractClient(token());
    const body = await expectApiError(client.api.me(), 401, 'Unauthorized');
    expect(body.message).toBe('authentication required');
  });
});
