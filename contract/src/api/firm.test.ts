// The firm and its settings: profile, people, questionnaire, document
// checklist, creditor library, court registry.
import { describe, expect, it } from '@jest/globals';

import { firmAdmin, firmUser, tag } from '../support/actors.ts';
import { seeded } from '../support/env.ts';
import { restoreChecklist, restoreQuestionnaire } from '../support/firm-settings.ts';
import { offlineOnly } from '../support/gates.ts';
import { ADDRESS, FIRM, FIRM_USER, SIGNATURE_BLOCK } from '../support/shapes.ts';
import { expectApiError } from '../support/wire.ts';

describe('the firm profile', () => {
  it('reads and updates the firm, refusing a bad chapter by field', async () => {
    const admin = firmAdmin();
    const firm = await admin.api.getFirm();
    admin.wire(200, FIRM);
    expect(firm.name).toBe(seeded.firmAdmin.firmName);

    try {
      const updated = await admin.api.updateFirm({
        defaultCourt: 'flmb',
        defaultDivision: 'tampa',
        defaultChapter: 7,
        letterhead: { name: 'Contract & Co', phone: '555-0100', email: 'office@insolvia.test' },
      });
      admin.wire(200, FIRM);
      expect(updated.defaultChapter).toBe(7);
      expect(updated.letterhead?.name).toBe('Contract & Co');

      await admin.api.updateFirm({
        defaultCourt: null,
        defaultDivision: null,
        defaultChapter: null,
        letterhead: null,
      });
      admin.wire(200, FIRM);

      await expectApiError(
        admin.api.updateFirm({ defaultChapter: 9 as never }),
        400,
        'ValidationError',
      );
    } finally {
      // Firm-wide, and there is no scratch firm: put it back as it was found.
      await admin.api.updateFirm({
        defaultCourt: firm.defaultCourt,
        defaultDivision: firm.defaultDivision,
        defaultChapter: firm.defaultChapter,
        letterhead: firm.letterhead,
      });
    }
  });

  it('lists the court registry', async () => {
    const admin = firmAdmin();
    const registry = await admin.api.listCourts();
    admin.wire(200, {
      releaseId: 'string',
      effectiveDate: 'string',
      districts: [
        {
          code: 'string',
          courtId: 'string|null',
          name: 'string',
          state: 'string',
          circuit: 'integer',
          website: 'string',
          divisions: [
            {
              code: 'string',
              name: 'string',
              officeCode: 'string|null',
              officeCodeVerified: 'boolean',
              courthouse: 'object|null',
              counties: [{ name: 'string', fips: 'string' }],
            },
          ],
          caseUpload: { status: 'string', verifiedAt: 'string|null' },
        },
      ],
    });
    expect(registry.districts.some((d) => d.code === 'flmb')).toBe(true);
  });
});

describe('the firm’s people', () => {
  offlineOnly(
    'adds pool accounts (a real Cognito pool, deployed) and signs in as one with the run’s own key',
  ).it('adds, edits, lists and removes a colleague — and guards the last admin', async () => {
    const admin = firmAdmin();
    const email = `colleague-${tag()}@insolvia.test`;
    const added = await admin.api.addFirmUser({
      email,
      firstName: 'Para',
      lastName: 'Legal',
      role: 'paralegal',
    });
    admin.wire(201, FIRM_USER);
    expect(added.email).toBe(email);
    expect(added.isAdmin).toBe(false);

    // The same address twice is the directory's conflict.
    await expectApiError(
      admin.api.addFirmUser({ email, firstName: 'Para', lastName: 'Legal', role: 'paralegal' }),
      409,
      'ConflictError',
    );
    await expectApiError(
      admin.api.addFirmUser({ email: 'nope', firstName: '', lastName: 'x', role: 'paralegal' }),
      400,
      'ValidationError',
    );

    const users = await admin.api.listFirmUsers();
    admin.wire(200, { users: [FIRM_USER] });
    expect(users.map((u) => u.subject)).toContain(added.subject);

    const directory = await admin.api.listFirmDirectory();
    admin.wire(200, {
      people: [
        {
          subject: 'string',
          firstName: 'string',
          lastName: 'string',
          displayName: 'string',
          role: 'string',
          signatureBlock: 'object|null',
        },
      ],
    });
    expect(directory.map((c) => c.subject)).toContain(added.subject);

    const edited = await admin.api.updateFirmUser(added.subject, {
      firstName: 'Paula',
      accessAllCases: true,
      signatureBlock: { bar_number: '999', address: { city: 'Tampa', state: 'FL' } },
    });
    const body = admin.wire(200, FIRM_USER) as { signatureBlock: unknown };
    expect(edited.firstName).toBe('Paula');
    expect(body.signatureBlock).not.toBeNull();
    admin.wire(200, { signatureBlock: SIGNATURE_BLOCK });
    admin.wire(200, { signatureBlock: { address: ADDRESS } });

    // A non-admin may not administer the firm.
    const colleague = firmUser(added.subject);
    await expectApiError(colleague.api.listFirmUsers(), 403, 'ForbiddenError');

    // The firm's only admin cannot demote themself.
    await expectApiError(
      admin.api.updateFirmUser(seeded.firmAdmin.subject, { isAdmin: false }),
      409,
    );

    await admin.api.removeFirmUser(added.subject);
    admin.wire(204);
    await expectApiError(admin.api.removeFirmUser(added.subject), 404, 'NotFoundError');
    await expectApiError(
      admin.api.updateFirmUser('no-such-subject', { firstName: 'x' }),
      404,
      'NotFoundError',
    );
  });
});

describe('the intake questionnaire', () => {
  it('reads the default, saves a customisation, and resets', async () => {
    const admin = firmAdmin();
    const SECTION = {
      id: 'string',
      title: 'string',
      covers: 'string',
      switchable: 'boolean',
      enabled: 'boolean',
      instructions: 'string|null',
      defaultInstructions: 'string',
    } as const;
    const QUESTIONNAIRE = {
      isDefault: 'boolean',
      updatedAt: 'string|null',
      updatedBy: 'string|null',
      sections: [SECTION],
    } as const;

    const initial = await admin.api.getFirmQuestionnaire();
    admin.wire(200, QUESTIONNAIRE);
    expect(initial.sections.length).toBeGreaterThan(0);

    try {
      const sections = initial.sections.map((s) => ({
        id: s.id,
        enabled: s.switchable ? false : s.enabled,
        instructions: 'Contract instructions.',
      }));
      const saved = await admin.api.saveFirmQuestionnaire({ sections });
      admin.wire(200, QUESTIONNAIRE);
      expect(saved.isDefault).toBe(false);
      expect(saved.updatedBy).toBe(seeded.firmAdmin.subject);

      await expectApiError(
        admin.api.saveFirmQuestionnaire({
          sections: [{ id: 'nonsense' as never, enabled: true, instructions: null }],
        }),
        400,
        'ValidationError',
      );

      const reset = await admin.api.resetFirmQuestionnaire();
      admin.wire(200, QUESTIONNAIRE);
      expect(reset.isDefault).toBe(true);
    } finally {
      await restoreQuestionnaire(initial);
    }
  });
});

describe('the document checklist', () => {
  it('reads the default, saves a custom list, and resets', async () => {
    const admin = firmAdmin();
    const ITEM = { title: 'string', kind: 'string', description: 'string|null' } as const;
    const CHECKLIST = {
      isDefault: 'boolean',
      updatedAt: 'string|null',
      updatedBy: 'string|null',
      items: [ITEM],
      defaultItems: [ITEM],
    } as const;

    const initial = await admin.api.getFirmDocumentChecklist();
    admin.wire(200, CHECKLIST);
    expect(initial.defaultItems.length).toBeGreaterThan(0);

    try {
      const saved = await admin.api.saveFirmDocumentChecklist({
        items: [
          { title: 'Two pay stubs', kind: 'pay_stub', description: 'The most recent two.' },
          { title: 'Credit report', kind: 'credit_report' },
        ],
      });
      admin.wire(200, CHECKLIST);
      expect(saved.isDefault).toBe(false);
      expect(saved.items.map((i) => i.kind)).toEqual(['pay_stub', 'credit_report']);

      await expectApiError(
        admin.api.saveFirmDocumentChecklist({ items: [{ title: '', kind: 'pay_stub' }] }),
        400,
        'ValidationError',
      );

      const reset = await admin.api.resetFirmDocumentChecklist();
      admin.wire(200, CHECKLIST);
      expect(reset.isDefault).toBe(true);
    } finally {
      await restoreChecklist(initial);
    }
  });
});

describe('the creditor library', () => {
  it('adds, edits, lists and removes a library creditor', async () => {
    const admin = firmAdmin();
    const LIBRARY_CREDITOR = {
      id: 'string',
      name: 'string',
      address: ADDRESS,
      additional_notice_parties: ['object'],
      preferred: 'boolean',
      'notes?': 'string',
      created_at: 'string',
      updated_at: 'string',
    } as const;

    const name = `Example Lender ${tag()}`;
    const added = await admin.api.addLibraryCreditor({
      name,
      address: { line1: '1 Main St', city: 'Tampa', state: 'FL', postal_code: '33602' },
      preferred: true,
    });
    admin.wire(201, LIBRARY_CREDITOR);
    expect(added.name).toBe(name);

    const updated = await admin.api.updateLibraryCreditor(added.id, {
      name,
      address: { line1: '2 Main St', city: 'Tampa', state: 'FL', postal_code: '33602' },
      notes: 'Contract note',
    });
    admin.wire(200, LIBRARY_CREDITOR);
    expect(updated.notes).toBe('Contract note');

    const listed = await admin.api.listLibraryCreditors();
    admin.wire(200, { creditors: [LIBRARY_CREDITOR] });
    expect(listed.map((c) => c.id)).toContain(added.id);

    await expectApiError(admin.api.addLibraryCreditor({ name: '' }), 400, 'ValidationError');

    await admin.api.removeLibraryCreditor(added.id);
    admin.wire(204);
    await expectApiError(admin.api.removeLibraryCreditor(added.id), 404, 'NotFoundError');
    await expectApiError(admin.api.updateLibraryCreditor(added.id, { name }), 404, 'NotFoundError');
  });
});
