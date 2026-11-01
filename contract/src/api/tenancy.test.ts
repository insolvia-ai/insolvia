// Tenancy as the client sees it (ADR 0009, ADR 0029 rule 1): another firm's
// administrator, holding a perfectly good token, finds nothing of ours — a
// 404 rather than a 403, so no route is a probe for which ids exist.
//
// The other firm is the one property a single firm cannot express. Offline it
// is provisioned through the admin service for this run and signed for with
// the run's key; deployed it is the seed's second firm (staging's
// `outsider`), and a seed with one firm (seeds/dev.json) skips these by name.
import { beforeAll, expect, it } from '@jest/globals';

import { firmAdmin, freshFirmAdmin, scratchCase, seededStaff, tag } from '../support/actors.ts';
import type { ContractClient } from '../support/client.ts';
import { contractClient } from '../support/client.ts';
import { env, seeded } from '../support/env.ts';
import { needsSeeded } from '../support/gates.ts';
import { hasPerson, person, TARGET } from '../support/target.ts';
import { googleIdToken, staffToken } from '../support/tokens.ts';
import { expectApiError } from '../support/wire.ts';

const OUTSIDER =
  TARGET === 'offline' ||
  (hasPerson('outsider') && person('outsider').firmName !== seeded.firmAdmin.firmName);

async function provisionedOutsider(): Promise<ContractClient> {
  const response = await fetch(`${env.adminUrl}/v1/firms`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${googleIdToken()}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({
      name: `Other Firm ${tag()}`,
      admin: { email: `outsider-${tag()}@insolvia.test`, firstName: 'Other', lastName: 'Admin' },
    }),
  });
  expect(response.status).toBe(201);
  const { admin } = (await response.json()) as { admin: { subject: string } };
  return contractClient(staffToken(admin.subject));
}

needsSeeded(OUTSIDER, 'second firm').describe('another firm', () => {
  let outsider: ContractClient;
  beforeAll(async () => {
    outsider = TARGET === 'offline' ? await provisionedOutsider() : seededStaff('outsider');
  });

  it('finds none of our case, its debtors, documents, approval or client', async () => {
    const { caseId, client } = await scratchCase();

    await expectApiError(outsider.api.getCase(caseId), 404, 'NotFoundError');
    await expectApiError(outsider.api.listDebtors(caseId), 404, 'NotFoundError');
    await expectApiError(outsider.api.listDocuments(caseId), 404, 'NotFoundError');
    await expectApiError(outsider.api.getFilingApproval(caseId), 404, 'NotFoundError');
    await expectApiError(outsider.api.getFirmClient(client.id), 404, 'NotFoundError');
    await expectApiError(outsider.api.listFirmClientCases(client.id), 404, 'NotFoundError');
    const theirs = await outsider.api.listCases({ limit: 100 });
    expect(theirs.cases.map((c) => c.id)).not.toContain(caseId);
    // Our own administrator still sees it, so the 404s are about the caller.
    await firmAdmin().api.getCase(caseId);
  });

  it('can neither see nor revoke our filing credential', async () => {
    const admin = await freshFirmAdmin();
    const status = await admin.api.getFilingAuthorization();
    const signedHere = !status.current;
    if (signedHere) {
      await admin.api.signFilingAuthorization({
        version: status.text.version,
        digest: status.text.digest,
      });
    }
    const credential = await admin.api.enrolFilingCredential({
      login: `contract-filer-${tag()}`,
      password: 'synthetic-not-a-secret',
      totp_seed: 'JBSWY3DPEHPK3PXP',
      courts: ['flmb'],
    });
    try {
      const listed = await outsider.api.listFilingCredentials();
      expect(listed.map((c) => c.id)).not.toContain(credential.id);
      await expectApiError(
        outsider.api.revokeFilingCredential(credential.id),
        404,
        'NotFoundError',
      );
      expect((await admin.api.listFilingCredentials()).map((c) => c.id)).toContain(credential.id);
    } finally {
      await admin.api.revokeFilingCredential(credential.id);
      if (signedHere) await admin.api.withdrawFilingAuthorization();
    }
  });
});
