// The admin service through the portal's own client, @insolvia-ai/admin-client
// (ADR 0011, ADR 0029 rule 2): a Google Workspace ID token is the only
// credential it accepts, and a firm user's Cognito token must die in
// verification (the cross-issuer 401).
//
// Staff sign-in is not automated on any deployed environment (ADR 0029): the
// verifier requires Google's `hd` claim, which only a real Workspace user's
// token carries. So the deployed target checks admin SIGNED OUT — health,
// CORS, and every route refusing both no credential and a seeded person's
// real Cognito token — and the signed-in specs run offline, where the run's
// own key signs Google-shaped tokens for the same verifier.
import { describe, expect, it } from '@jest/globals';
import type { AdminClient } from '@insolvia-ai/admin-client';

import { adminClient, adminWire, expectAdminError } from '../support/admin.ts';
import { tag, tokenOf } from '../support/actors.ts';
import { preflight, signedOut } from '../support/browser.ts';
import { contractClient } from '../support/client.ts';
import { env } from '../support/env.ts';
import { fixtureCase } from '../support/fixtures.ts';
import { offlineOnly } from '../support/gates.ts';
import { FIRM, FIRM_USER } from '../support/shapes.ts';
import { EXPECTED_API_ENVIRONMENT } from '../support/target.ts';
import { googleIdToken, staffToken } from '../support/tokens.ts';
import { expectApiError, expectShape, type Spec, withMessage } from '../support/wire.ts';

const SIGNS_GOOGLE = 'signs Google-shaped staff tokens with the run’s own key (ADR 0029)';

const ENVELOPE: Spec = { error: 'string', 'message?': 'string', 'fields?': 'object' };
const ADMIN_FIRM: Spec = { ...(FIRM as Record<string, Spec>), userCount: 'integer' };

/** A staff client, signed in with an offline-minted Workspace ID token. */
function staff(overrides: Record<string, unknown> = {}): AdminClient {
  return adminClient(() => googleIdToken(overrides));
}

// Every route the client has a method for, called with ids that need not
// exist: authentication runs before any lookup.
const ROUTES: readonly [string, string, string, (client: AdminClient) => Promise<unknown>][] = [
  ['listFirms', 'GET', '/v1/firms', (c) => c.listFirms()],
  ['getFirm', 'GET', '/v1/firms/no-such-firm', (c) => c.getFirm('no-such-firm')],
  [
    'provisionFirm',
    'POST',
    '/v1/firms',
    (c) =>
      c.provisionFirm({
        name: 'Never Provisioned LLP',
        admin: { email: 'never@insolvia.test', firstName: 'Never', lastName: 'Made' },
      }),
  ],
  [
    'setFirmStatus',
    'PATCH',
    '/v1/firms/no-such-firm',
    (c) => c.setFirmStatus('no-such-firm', 'suspended'),
  ],
  ['listFirmUsers', 'GET', '/v1/firms/no-such-firm/users', (c) => c.listFirmUsers('no-such-firm')],
  [
    'resendInvite',
    'POST',
    '/v1/firms/no-such-firm/users/no-such-subject/resend-invite',
    (c) => c.resendInvite('no-such-firm', 'no-such-subject'),
  ],
];

describe('admin, signed out (both targets)', () => {
  it('health is public and names this environment', async () => {
    const answer = await signedOut('GET', '/health');
    expect(answer.status).toBe(200);
    expectShape(answer.body, { status: 'string', environment: 'string' });
    expect((answer.body as { environment: string }).environment).toBe(EXPECTED_API_ENVIRONMENT);
  });

  it('CORS admits the portal’s origin for the client’s requests, and no other', async () => {
    for (const method of ['GET', 'POST', 'PATCH']) {
      const headers = await preflight('/v1/firms', env.adminPortalOrigin, method);
      withMessage(`${method} preflight`, () => {
        expect(headers.get('access-control-allow-origin')).toBe(env.adminPortalOrigin);
        expect(headers.get('access-control-allow-methods')).toContain(method);
        expect(headers.get('access-control-allow-headers')?.toLowerCase()).toContain(
          'authorization',
        );
      });
    }
    // The answer itself carries the header too, or the browser drops it.
    const health = await signedOut('GET', '/health', env.adminPortalOrigin);
    expect(health.headers.get('access-control-allow-origin')).toBe(env.adminPortalOrigin);

    const foreign = await preflight('/v1/firms', 'https://evil.example.test', 'GET');
    expect(foreign.get('access-control-allow-origin')).toBeNull();
  });

  it.each(ROUTES)('%s refuses no credential at all (%s %s)', async (_name, method, path) => {
    const answer = await signedOut(method, path);
    withMessage(JSON.stringify(answer.body), () => expect(answer.status).toBe(401));
    expectShape(answer.body, ENVELOPE, 'error');
    expect(answer.body).toMatchObject({
      error: 'Unauthorized',
      message: 'authentication required',
    });
  });

  // The security invariant (ADR 0011): a VALID firm-pool access token — on
  // the deployed target the seeded admin's real one, from the real pool — is
  // still not a staff credential.
  it.each(ROUTES)(
    '%s refuses a seeded firm user’s Cognito token (%s %s)',
    async (_name, _method, _path, call) => {
      const client = adminClient(() => tokenOf('admin'));
      const body = await expectAdminError(call(client), 401, 'Unauthorized');
      expect(body.message).toBe('authentication required');
    },
  );
});

offlineOnly(SIGNS_GOOGLE).describe('admin: staff token verification', () => {
  it.each([
    ['another Workspace domain', { hd: 'example.com' }],
    ['a personal account (no hd)', { hd: undefined }],
    ['an unverified email', { email_verified: false }],
    ['another audience', { aud: 'someone-else.apps.googleusercontent.com' }],
    ['a non-Google issuer', { iss: env.issuerUrl }],
    ['an expired token', { exp: Math.floor(Date.now() / 1000) - 600 }],
  ])('%s is a 401', async (_label, overrides) => {
    const body = await expectAdminError(staff(overrides).listFirms(), 401, 'Unauthorized');
    expect(body.message).toBe('authentication required');
  });
});

offlineOnly(SIGNS_GOOGLE).describe('admin: firm provisioning', () => {
  it('provisions a firm whose first admin can use the API at once', async () => {
    const admin = staff();
    const email = `first-admin-${tag()}@insolvia.test`;
    const name = `Provisioned ${tag()} LLP`;
    const provisioned = await admin.provisionFirm({
      name,
      admin: { email, firstName: 'First', lastName: 'Admin' },
    });
    adminWire(201, { firm: FIRM, admin: FIRM_USER });
    const { firm, admin: first } = provisioned;
    expect(firm.name).toBe(name);
    expect(firm.status).toBe('active');
    expect(first.isAdmin).toBe(true);

    // The same account twice is the directory's conflict, before any row.
    await expectAdminError(
      admin.provisionFirm({
        name: `${name} again`,
        admin: { email, firstName: 'First', lastName: 'Admin' },
      }),
      409,
      'ConflictError',
    );
    // A field error reaches the caller as fields, by name. The bodies the
    // portal's types would never build are cast past them: the client sends
    // what it is given.
    const invalid = await expectAdminError(
      admin.provisionFirm({ name: '' } as never),
      400,
      'ValidationError',
    );
    expect(typeof invalid.fields).toBe('object');
    await expectAdminError(admin.provisionFirm('not an object' as never), 400, 'ValidationError');

    // The tenant API resolves the new admin into the new firm (one store).
    const api = contractClient(staffToken(first.subject));
    const me = await api.api.me();
    expect(me.firm?.id).toBe(firm.id);

    // Read back: the list, one firm, its users.
    const listed = await admin.listFirms();
    adminWire(200, { firms: [ADMIN_FIRM] });
    expect(listed.find((f) => f.id === firm.id)?.userCount).toBe(1);

    const one = await admin.getFirm(firm.id);
    adminWire(200, ADMIN_FIRM);
    expect(one.id).toBe(firm.id);
    await expectAdminError(admin.getFirm('no-such-firm'), 404, 'NotFoundError');

    const users = await admin.listFirmUsers(firm.id);
    adminWire(200, { users: [FIRM_USER] });
    expect(users.map((u) => u.subject)).toEqual([first.subject]);
    await expectAdminError(admin.listFirmUsers('no-such-firm'), 404, 'NotFoundError');

    // Suspension is immediate in the tenant API, and reversible.
    const suspended = await admin.setFirmStatus(firm.id, 'suspended');
    adminWire(200, FIRM);
    expect(suspended.status).toBe('suspended');
    const blocked = await api.api.listCases().catch((error: { statusCode: number }) => error);
    expect((blocked as { statusCode: number }).statusCode).toBe(403);
    const reactivated = await admin.setFirmStatus(firm.id, 'active');
    adminWire(200, FIRM);
    expect(reactivated.status).toBe('active');
    await api.api.listCases();
    await expectAdminError(
      admin.setFirmStatus(firm.id, 'deleted' as never),
      400,
      'ValidationError',
    );
    await expectAdminError(admin.setFirmStatus('no-such-firm', 'active'), 404, 'NotFoundError');

    // A stranded invitation can be re-sent; unknown people and firms are 404s.
    await admin.resendInvite(firm.id, first.subject);
    expect(adminWire(204)).toBe('');
    await expectAdminError(admin.resendInvite(firm.id, 'no-such-subject'), 404, 'NotFoundError');
    await expectAdminError(admin.resendInvite('no-such-firm', first.subject), 404, 'NotFoundError');
  });

  it('a second firm’s admin cannot reach the seeded firm’s case (ADR 0009)', async () => {
    const provisioned = await staff().provisionFirm({
      name: `Other Firm ${tag()}`,
      admin: { email: `other-${tag()}@insolvia.test`, firstName: 'Other', lastName: 'Admin' },
    });
    const other = contractClient(staffToken(provisioned.admin.subject));
    const seededCase = await fixtureCase(7);
    // Another firm's case is not "forbidden" — it does not exist, for this caller.
    await expectApiError(other.api.getCase(seededCase.id), 404, 'NotFoundError');
    await expectApiError(other.api.listDebtors(seededCase.id), 404, 'NotFoundError');
    const { cases } = await other.api.listCases();
    expect(cases).toEqual([]);
  });
});
