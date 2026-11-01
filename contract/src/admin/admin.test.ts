// The admin service over raw HTTP — all seven routes (ADR 0011): a Google
// Workspace ID token is the only credential it accepts, and a firm user's
// Cognito token must die in verification (the cross-issuer 401).
import { expect, it } from '@jest/globals';

import { tag } from '../support/actors.ts';
import { contractClient } from '../support/client.ts';
import { env, seeded } from '../support/env.ts';
import { fixtureCase } from '../support/fixtures.ts';
import { FIRM, FIRM_USER } from '../support/shapes.ts';
import { googleIdToken, staffToken } from '../support/tokens.ts';
import { offlineOnly } from '../support/gates.ts';
import { expectApiError, expectShape, type Spec, withMessage } from '../support/wire.ts';

// The admin service signs in with tokens only the run's own key mints (Google ID
// tokens for admin, the MCP harness's client for MCP); #583 brings it to the
// deployed target, through its own client.
const NOT_YET_DEPLOYED = 'signs with the run’s own key; the deployed admin service target is #583';

interface Answer {
  readonly status: number;
  readonly body: unknown;
}

async function admin(
  method: string,
  path: string,
  body?: unknown,
  token: string | null = googleIdToken(),
): Promise<Answer> {
  const response = await fetch(`${env.adminUrl}${path}`, {
    method,
    headers: {
      Accept: 'application/json',
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...(token === null ? {} : { Authorization: `Bearer ${token}` }),
    },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  const text = await response.text();
  return { status: response.status, body: text === '' ? '' : JSON.parse(text) };
}

const ENVELOPE: Spec = { error: 'string', 'message?': 'string', 'fields?': 'object' };
const ADMIN_FIRM: Spec = { ...(FIRM as Record<string, Spec>), userCount: 'integer' };

function refused(answer: Answer, status: number, error: string): Record<string, unknown> {
  withMessage(JSON.stringify(answer.body), () => expect(answer.status).toBe(status));
  expectShape(answer.body, ENVELOPE, 'error');
  expect((answer.body as { error: string }).error).toBe(error);
  return answer.body as Record<string, unknown>;
}

offlineOnly(NOT_YET_DEPLOYED).describe('admin: authentication', () => {
  it('health is public', async () => {
    const answer = await admin('GET', '/health', undefined, null);
    expect(answer.status).toBe(200);
    expectShape(answer.body, { status: 'string', environment: 'string' });
  });

  it.each([
    ['no token', () => null],
    // The security invariant: a VALID firm-pool access token, signed by a key
    // this service trusts, is still not a staff credential.
    [
      'a firm user’s Cognito access token (cross-issuer)',
      () => staffToken(seeded.firmAdmin.subject),
    ],
    ['another Workspace domain', () => googleIdToken({ hd: 'example.com' })],
    ['a personal account (no hd)', () => googleIdToken({ hd: undefined })],
    ['an unverified email', () => googleIdToken({ email_verified: false })],
    ['another audience', () => googleIdToken({ aud: 'someone-else.apps.googleusercontent.com' })],
    ['a non-Google issuer', () => googleIdToken({ iss: env.issuerUrl })],
    ['an expired token', () => googleIdToken({ exp: Math.floor(Date.now() / 1000) - 600 })],
  ])('%s is a 401', async (_label, token) => {
    const answer = await admin('GET', '/v1/firms', undefined, token());
    const body = refused(answer, 401, 'Unauthorized');
    expect(body.message).toBe('authentication required');
  });
});

offlineOnly(NOT_YET_DEPLOYED).describe('admin: firm provisioning', () => {
  it('provisions a firm whose first admin can use the API at once', async () => {
    const email = `first-admin-${tag()}@insolvia.test`;
    const name = `Provisioned ${tag()} LLP`;
    const provisioned = await admin('POST', '/v1/firms', {
      name,
      admin: { email, firstName: 'First', lastName: 'Admin' },
    });
    withMessage(JSON.stringify(provisioned.body), () => expect(provisioned.status).toBe(201));
    expectShape(provisioned.body, { firm: FIRM, admin: FIRM_USER });
    const { firm, admin: first } = provisioned.body as {
      firm: { id: string; name: string; status: string };
      admin: { subject: string; isAdmin: boolean; email: string };
    };
    expect(firm.name).toBe(name);
    expect(firm.status).toBe('active');
    expect(first.isAdmin).toBe(true);

    // The same account twice is the directory's conflict, before any row.
    refused(
      await admin('POST', '/v1/firms', {
        name: `${name} again`,
        admin: { email, firstName: 'First', lastName: 'Admin' },
      }),
      409,
      'ConflictError',
    );
    const invalid = refused(await admin('POST', '/v1/firms', { name: '' }), 400, 'ValidationError');
    expect(typeof invalid.fields).toBe('object');
    refused(await admin('POST', '/v1/firms', 'not an object'), 400, 'ValidationError');

    // The tenant API resolves the new admin into the new firm (one store).
    const api = contractClient(staffToken(first.subject));
    const me = await api.api.me();
    expect(me.firm?.id).toBe(firm.id);

    // Read back: the list, one firm, its users.
    const list = await admin('GET', '/v1/firms');
    expect(list.status).toBe(200);
    expectShape(list.body, { firms: [ADMIN_FIRM] });
    const listed = (list.body as { firms: { id: string; userCount: number }[] }).firms;
    expect(listed.find((f) => f.id === firm.id)?.userCount).toBe(1);

    const one = await admin('GET', `/v1/firms/${firm.id}`);
    expect(one.status).toBe(200);
    expectShape(one.body, ADMIN_FIRM);
    refused(await admin('GET', '/v1/firms/no-such-firm'), 404, 'NotFoundError');

    const users = await admin('GET', `/v1/firms/${firm.id}/users`);
    expect(users.status).toBe(200);
    expectShape(users.body, { users: [FIRM_USER] });
    refused(await admin('GET', '/v1/firms/no-such-firm/users'), 404, 'NotFoundError');

    // Suspension is immediate in the tenant API, and reversible.
    const suspended = await admin('PATCH', `/v1/firms/${firm.id}`, { status: 'suspended' });
    expect(suspended.status).toBe(200);
    expectShape(suspended.body, FIRM);
    expect((suspended.body as { status: string }).status).toBe('suspended');
    const blocked = await api.api.listCases().catch((error: { statusCode: number }) => error);
    expect((blocked as { statusCode: number }).statusCode).toBe(403);
    const reactivated = await admin('PATCH', `/v1/firms/${firm.id}`, { status: 'active' });
    expect(reactivated.status).toBe(200);
    await api.api.listCases();
    refused(
      await admin('PATCH', `/v1/firms/${firm.id}`, { status: 'deleted' }),
      400,
      'ValidationError',
    );
    refused(
      await admin('PATCH', '/v1/firms/no-such-firm', { status: 'active' }),
      404,
      'NotFoundError',
    );

    // A stranded invitation can be re-sent; unknown people and firms are 404s.
    const resent = await admin('POST', `/v1/firms/${firm.id}/users/${first.subject}/resend-invite`);
    expect(resent.status).toBe(204);
    expect(resent.body).toBe('');
    refused(
      await admin('POST', `/v1/firms/${firm.id}/users/no-such-subject/resend-invite`),
      404,
      'NotFoundError',
    );
    refused(
      await admin('POST', `/v1/firms/no-such-firm/users/${first.subject}/resend-invite`),
      404,
      'NotFoundError',
    );
  });

  it('a second firm’s admin cannot reach the seeded firm’s case (ADR 0009)', async () => {
    const provisioned = await admin('POST', '/v1/firms', {
      name: `Other Firm ${tag()}`,
      admin: { email: `other-${tag()}@insolvia.test`, firstName: 'Other', lastName: 'Admin' },
    });
    const other = contractClient(
      staffToken((provisioned.body as { admin: { subject: string } }).admin.subject),
    );
    const seededCase = await fixtureCase(7);
    // Another firm's case is not "forbidden" — it does not exist, for this caller.
    await expectApiError(other.api.getCase(seededCase.id), 404, 'NotFoundError');
    await expectApiError(other.api.listDebtors(seededCase.id), 404, 'NotFoundError');
    const { cases } = await other.api.listCases();
    expect(cases).toEqual([]);
  });
});
