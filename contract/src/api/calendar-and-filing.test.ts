// The calendar (events, deadlines, the subscription feed) and electronic
// filing's staff surface (ADR 0024): the authorization, the credential vault,
// and the per-filing approval.
import { ApiReauthenticationRequiredException } from '@insolvia-ai/api-client';
import { describe, expect, it } from '@jest/globals';

import { firmAdmin, scratchCase } from '../support/actors.ts';
import { contractClient } from '../support/client.ts';
import { env, seeded } from '../support/env.ts';
import { CALENDAR_EVENT } from '../support/shapes.ts';
import { staffToken } from '../support/tokens.ts';
import { expectApiError, type Spec } from '../support/wire.ts';

const EVENT: Spec = {
  ...(CALENDAR_EVENT as Record<string, Spec>),
  attendees: ['string'],
  generated: 'boolean',
  'rule_id?': 'string',
  'rule_citation?': 'string',
  dismissed: 'boolean',
  created_by: 'string',
  created_at: 'string',
  updated_at: 'string',
};

describe('the calendar', () => {
  it('case and firm events: add, edit, dismiss, list, window and remove', async () => {
    const admin = firmAdmin();
    const { caseId } = await scratchCase();

    const hearing = await admin.api.addCaseEvent(caseId, {
      title: 'Meeting of creditors',
      start: '2026-11-20T15:00:00Z',
      end: '2026-11-20T16:00:00Z',
      location: 'Tampa',
      attendees: [seeded.firmAdmin.subject],
    });
    admin.wire(201, EVENT);
    expect(hearing.case_id).toBe(caseId);
    const moved = await admin.api.updateCaseEvent(caseId, hearing.id, {
      title: 'Meeting of creditors (continued)',
      start: '2026-11-27T15:00:00Z',
      end: '2026-11-27T16:00:00Z',
    });
    admin.wire(200, EVENT);
    expect(moved.title).toContain('continued');
    const dismissed = await admin.api.dismissCaseEvent(caseId, hearing.id, true);
    admin.wire(200, EVENT);
    expect(dismissed.dismissed).toBe(true);
    const caseEvents = await admin.api.listCaseEvents(caseId);
    admin.wire(200, { events: [EVENT] });
    expect(caseEvents.map((e) => e.id)).toContain(hearing.id);

    const offsite = await admin.api.addFirmEvent({
      title: 'Firm offsite',
      start: '2026-11-25',
      all_day: true,
    });
    admin.wire(201, EVENT);
    expect(offsite.case_id).toBeUndefined();
    expect(admin.last().body).not.toHaveProperty('case_id');
    await admin.api.updateFirmEvent(offsite.id, {
      title: 'Firm offsite',
      start: '2026-11-26',
      all_day: true,
      description: 'Moved a day.',
    });
    admin.wire(200, EVENT);
    await admin.api.dismissFirmEvent(offsite.id, false);
    admin.wire(200, EVENT);
    const firmEvents = await admin.api.listFirmEvents();
    admin.wire(200, { events: [EVENT] });
    expect(firmEvents.map((e) => e.id)).toContain(offsite.id);

    const window = await admin.api.getCalendar({ from: '2026-11-01', to: '2026-12-01' });
    admin.wire(200, { from: 'string', to: 'string', events: [EVENT] });
    expect(window.events.map((e) => e.id)).toEqual(
      expect.arrayContaining([hearing.id, offsite.id]),
    );
    const scoped = await admin.api.getCalendar({ from: '2026-11-01', to: '2026-12-01', caseId });
    expect(scoped.events.every((e) => e.case_id === caseId)).toBe(true);

    await expectApiError(
      admin.api.addFirmEvent({ title: '', start: 'not-a-date' }),
      400,
      'ValidationError',
    );
    await expectApiError(admin.api.getCalendar({ from: '2026-12-01', to: '2026-11-01' }), 400);

    await admin.api.removeCaseEvent(caseId, hearing.id);
    admin.wire(204);
    await admin.api.removeFirmEvent(offsite.id);
    admin.wire(204);
    await expectApiError(admin.api.removeFirmEvent(offsite.id), 404, 'NotFoundError');
  });

  it('mints a feed token whose feed is public, and revokes it', async () => {
    const admin = firmAdmin();
    const token = await admin.api.mintCalendarToken();
    // feedUrl is the client's (base URL + feedPath); the wire carries the path.
    admin.wire(201, { token: 'string', feedPath: 'string' });
    expect(token.feedUrl).toBe(`${env.apiUrl}${token.feedPath}`);
    const feed = await fetch(token.feedUrl);
    expect(feed.status).toBe(200);
    expect(feed.headers.get('content-type')).toContain('text/calendar');
    expect(await feed.text()).toContain('BEGIN:VCALENDAR');

    await admin.api.revokeCalendarToken();
    admin.wire(204);
    // A revoked feed token is a refused credential: 401, as the route answers.
    expect((await fetch(token.feedUrl)).status).toBe(401);
    await expectApiError(admin.api.revokeCalendarToken(), 404, 'NotFoundError');
  });
});

describe('electronic filing — the staff surface', () => {
  const AUTHORIZATION: Spec = {
    text: { version: 'string', digest: 'string', text: 'string' },
    current_version: 'string',
    current: 'boolean',
    signature: 'object|null',
  };
  const CREDENTIAL: Spec = {
    id: 'string',
    login: 'string',
    courts: ['string'],
    status: 'string',
    created_at: 'string',
    updated_at: 'string',
  };

  it('authorization → credential → approval refusals → withdrawal', async () => {
    const admin = firmAdmin();
    const status = await admin.api.getFilingAuthorization();
    admin.wire(200, AUTHORIZATION);
    expect(status.signature).toBeNull();

    // Without a signed authorization there is no vault to enrol into.
    const enrolment = {
      login: 'contract-filer',
      password: 'synthetic-not-a-secret',
      totp_seed: 'JBSWY3DPEHPK3PXP',
      courts: ['flmb'],
    };
    await expectApiError(admin.api.enrolFilingCredential(enrolment), 403, 'ForbiddenError');

    // Signing needs a RECENT sign-in: an hour-old auth_time is refused.
    const stale = contractClient(
      staffToken(seeded.firmAdmin.subject, { auth_time: Math.floor(Date.now() / 1000) - 3600 }),
    );
    const text = { version: status.text.version, digest: status.text.digest };
    await expect(stale.api.signFilingAuthorization(text)).rejects.toBeInstanceOf(
      ApiReauthenticationRequiredException,
    );
    await expectApiError(stale.api.signFilingAuthorization(text), 403, 'ReauthenticationRequired');

    await expectApiError(
      admin.api.signFilingAuthorization({ version: text.version, digest: 'stale-digest' }),
      409,
    );
    const signed = await admin.api.signFilingAuthorization(text);
    admin.wire(201, {
      ...(AUTHORIZATION as Record<string, Spec>),
      signature: {
        id: 'string',
        text_version: 'string',
        text_digest: 'string',
        signed_at: 'string',
      },
    });
    expect(signed.current).toBe(true);
    await expectApiError(admin.api.signFilingAuthorization(text), 409);

    const credential = await admin.api.enrolFilingCredential(enrolment);
    admin.wire(201, CREDENTIAL);
    // The vault never hands a secret back.
    expect(JSON.stringify(admin.last().body)).not.toContain(enrolment.password);
    const credentials = await admin.api.listFilingCredentials();
    admin.wire(200, { credentials: [CREDENTIAL] });
    expect(credentials.map((c) => c.id)).toContain(credential.id);
    await expectApiError(
      admin.api.enrolFilingCredential({ ...enrolment, totp_seed: '' }),
      400,
      'ValidationError',
    );

    // Approval: the basis is readable; approving an unready set is refused.
    const { caseId } = await scratchCase();
    const view = await admin.api.getFilingApproval(caseId);
    admin.wire(200, {
      basis: {
        scheme: 'string',
        digest: 'string',
        ready: 'boolean',
        blockers: ['string'],
        registryRelease: 'string',
        documents: ['object'],
        checklist: ['object'],
        fee: { handling: 'string', verified: 'boolean', detail: 'string' },
        signInMaxAgeSeconds: 'integer',
        approvalTtlSeconds: 'integer',
      },
      'approval?': 'object',
      'filing?': 'object',
    });
    expect(view.basis.ready).toBe(false);
    expect(view.basis.blockers.length).toBeGreaterThan(0);
    await expectApiError(admin.api.approveFiling(caseId, { digest: view.basis.digest }), 409);
    await expectApiError(
      stale.api.approveFiling(caseId, { digest: view.basis.digest }),
      403,
      'ReauthenticationRequired',
    );
    await expectApiError(admin.api.cancelFilingApproval(caseId), 409);
    await expectApiError(
      admin.api.resolveFiling(caseId, 'no-such-filing', { outcome: 'not_filed' }),
      404,
      'NotFoundError',
    );

    await admin.api.revokeFilingCredential(credential.id);
    admin.wire(204);
    await expectApiError(admin.api.revokeFilingCredential(credential.id), 404, 'NotFoundError');

    await admin.api.enrolFilingCredential(enrolment);
    const destroyed = await admin.api.withdrawFilingAuthorization();
    admin.wire(200, { credentials_revoked: 'integer' });
    expect(destroyed).toBe(1);
    await expectApiError(admin.api.withdrawFilingAuthorization(), 404, 'NotFoundError');
  });
});
