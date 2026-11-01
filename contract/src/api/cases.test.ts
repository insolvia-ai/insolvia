// A matter from the directory to an archived case: firm clients, opening a
// case, its debtors, its lifecycle, who works it, its tasks and its notes.
import { describe, expect, it } from '@jest/globals';

import type { Debtor } from '@insolvia-ai/api-client';

import type { ContractClient } from '../support/client.ts';
import {
  SCRATCH_COURT,
  SCRATCH_DISTRICT,
  firmAdmin,
  firmUser,
  scratchCase,
  scratchClient,
  seededStaff,
  tag,
  trackScratchCase,
  trackScratchClient,
} from '../support/actors.ts';
import { seeded, subjectOf } from '../support/env.ts';
import { fixtureCases } from '../support/fixtures.ts';
import { CASE, DEBTOR, FIRM_CLIENT, NOTE, TASK } from '../support/shapes.ts';
import { needsSeeded } from '../support/gates.ts';
import { PEOPLE, TARGET } from '../support/target.ts';
import { expectApiError, withMessage } from '../support/wire.ts';

const TYPED = { source: 'staff_typed' } as const;

/** Every case id the admin can list, page by page. */
async function allCaseIds(options: { archived?: boolean } = {}): Promise<string[]> {
  const api = firmAdmin().api;
  const ids: string[] = [];
  let cursor: string | undefined;
  do {
    const page = await api.listCases({ ...options, limit: 100, cursor });
    ids.push(...page.cases.map((c) => c.id));
    cursor = page.nextCursor;
  } while (cursor !== undefined);
  return ids;
}

/** Seeded colleagues of the admin who see only the cases they are assigned. */
const UNLINKED = PEOPLE.filter(
  (p) =>
    p.kind === 'staff' &&
    p.firmName === seeded.firmAdmin.firmName &&
    !p.accessAllCases &&
    p.handle !== 'admin',
);

/**
 * A colleague who sees only the cases they are assigned (ADR 0009). Offline:
 * one the suite adds and removes again, signed for with the run's key.
 * Deployed: the seed's (staging's `paralegal`) — the real pool cannot be
 * signed in as someone the run just added.
 */
async function colleagueWithoutAccess(): Promise<{
  subject: string;
  client: ContractClient;
  done: () => Promise<void>;
}> {
  if (TARGET === 'deployed') {
    const handle = UNLINKED[0]!.handle;
    return { subject: subjectOf(handle), client: seededStaff(handle), done: async () => {} };
  }
  const admin = firmAdmin();
  const added = await admin.api.addFirmUser({
    email: `assignee-${tag()}@insolvia.test`,
    firstName: 'Assigned',
    lastName: 'Staff',
    role: 'staff',
    accessAllCases: false,
  });
  return {
    subject: added.subject,
    client: firmUser(added.subject),
    done: () => admin.api.removeFirmUser(added.subject),
  };
}

describe('the client directory', () => {
  it('creates, reads, edits, stages, archives and merges firm clients', async () => {
    const admin = firmAdmin();
    const surname = `Directory-${tag()}`;
    const created = await admin.api.createFirmClient({
      name: { given: 'Dana', surname },
      email: `dana-${tag()}@insolvia.test`,
      residence_address: { line1: '1 Elm St', city: 'Tampa', state: 'FL', postal_code: '33602' },
      lead_source: 'referral',
    });
    admin.wire(201, FIRM_CLIENT);
    expect(created.status).toBe('active');
    trackScratchClient(created.id);

    const read = await admin.api.getFirmClient(created.id);
    admin.wire(200, FIRM_CLIENT);
    expect(read.name.surname).toBe(surname);

    const updated = await admin.api.updateFirmClient(created.id, {
      name: { given: 'Dana', middle: 'Q', surname },
      phone: '555-0101',
    });
    admin.wire(200, FIRM_CLIENT);
    expect(updated.phone).toBe('555-0101');

    const staged = await admin.api.setFirmClientProspectStage(created.id, 'consultation_scheduled');
    admin.wire(200, FIRM_CLIENT);
    expect(staged.prospect_stage).toBe('consultation_scheduled');
    const unstaged = await admin.api.setFirmClientProspectStage(created.id, null);
    admin.wire(200, FIRM_CLIENT);
    expect(unstaged.prospect_stage).toBeUndefined();
    expect(admin.last().body).not.toHaveProperty('prospect_stage');

    const listed = await admin.api.listFirmClients();
    admin.wire(200, { clients: [FIRM_CLIENT] });
    expect(listed.map((c) => c.id)).toContain(created.id);

    const noCases = await admin.api.listFirmClientCases(created.id);
    admin.wire(200, { cases: ['object'] });
    expect(noCases).toEqual([]);

    // Merge a duplicate into it; the duplicate is archived and points at it,
    // and the duplicate's case follows to the survivor with its copied
    // fields untouched.
    const duplicate = await scratchClient('Dana');
    const duplicateCase = trackScratchCase(
      (await admin.api.createCase({ chapter: 7, ...SCRATCH_COURT, clientIds: [duplicate.id] })).id,
    );
    const debtor1 = async () =>
      (await admin.api.listDebtors(duplicateCase)).find((d) => d.filing_role === 'debtor_1');
    const before = await debtor1();
    const merged = await admin.api.mergeFirmClient(created.id, duplicate.id);
    admin.wire(200, { client: FIRM_CLIENT, merged: FIRM_CLIENT });
    expect(merged.merged.merged_into).toBe(created.id);
    expect(merged.merged.status).toBe('archived');
    const after = await debtor1();
    expect(after?.client_id).toBe(created.id);
    expect(after?.name).toEqual(before?.name);
    const survivorCases = await admin.api.listFirmClientCases(created.id);
    expect(survivorCases.map((c) => c.case.id)).toContain(duplicateCase);
    await expectApiError(admin.api.mergeFirmClient(created.id, duplicate.id), 409, 'ConflictError');
    await expectApiError(admin.api.mergeFirmClient(created.id, created.id), 400, 'ValidationError');

    const archived = await admin.api.setFirmClientStatus(created.id, 'archived');
    admin.wire(200, FIRM_CLIENT);
    expect(archived.status).toBe('archived');
    await admin.api.setFirmClientStatus(created.id, 'active');
    admin.wire(200, FIRM_CLIENT);

    await expectApiError(admin.api.createFirmClient({ name: {} }), 400, 'ValidationError');
    await expectApiError(admin.api.getFirmClient('no-such-client'), 404, 'NotFoundError');
  });
});

describe('the seeded clients', () => {
  // Read-only: the fixture's clients are found by following the fixture
  // cases' debtors, never created here.
  async function seededDebtors(): Promise<{ caseId: string; debtor: Debtor }[]> {
    const api = firmAdmin().api;
    const found: { caseId: string; debtor: Debtor }[] = [];
    for (const fixture of await fixtureCases()) {
      for (const debtor of await api.listDebtors(fixture.case.id)) {
        if (debtor.filing_role === 'debtor_1' || debtor.filing_role === 'debtor_2') {
          found.push({ caseId: fixture.case.id, debtor });
        }
      }
    }
    expect(found.length).toBeGreaterThan(0);
    return found;
  }

  it('every seeded debtor is a copy of a client it still agrees with', async () => {
    const api = firmAdmin().api;
    for (const { caseId, debtor } of await seededDebtors()) {
      withMessage(`${caseId} ${debtor.filing_role}`, () => expect(debtor.client_id).toBeTruthy());
      const client = await api.getFirmClient(debtor.client_id!);
      expect(client.status).toBe('active');
      expect(client.name).toEqual(debtor.name);
      expect(debtor.differs_from_client).toEqual([]);
      const copied = Object.entries(debtor.provenance)
        .filter(([path]) => path.split('.')[0]!.split('[')[0] === 'name')
        .map(([, entry]) => entry);
      expect(copied.length).toBeGreaterThan(0);
      for (const entry of copied) {
        expect(entry).toEqual({ source: 'client', client_id: debtor.client_id });
      }
    }
  });

  it('the seeded clients are in the firm’s directory', async () => {
    const listed = (await firmAdmin().api.listFirmClients()).map((c) => c.id);
    const seededIds = (await seededDebtors()).map(({ debtor }) => debtor.client_id);
    expect(listed).toEqual(expect.arrayContaining(seededIds));
  });

  it('each seeded client lists its fixture case in the role it holds', async () => {
    const api = firmAdmin().api;
    for (const { caseId, debtor } of await seededDebtors()) {
      const entries = await api.listFirmClientCases(debtor.client_id!);
      const roles = new Map(entries.map((e) => [e.case.id, e.filing_role]));
      withMessage(`client ${debtor.client_id}`, () =>
        expect(roles.get(caseId)).toBe(debtor.filing_role),
      );
    }
  });
});

describe('a case, opened from a client', () => {
  it('opens, reads, lists, edits and pages cases', async () => {
    const admin = firmAdmin();
    const client = await scratchClient();
    const opened = await admin.api.createCase({
      chapter: 7,
      ...SCRATCH_COURT,
      clientIds: [client.id],
    });
    admin.wire(201, CASE);
    trackScratchCase(opened.id);
    expect(opened.status).toBe('intake');
    expect(opened.court).toBe('flnb');
    // The printed name is derived from the reference by the registry (#360).
    expect(opened.district).toBe(SCRATCH_DISTRICT);
    expect(opened.createdBy).toBe(seeded.firmAdmin.subject);

    const read = await admin.api.getCase(opened.id);
    admin.wire(200, CASE);
    expect(read.id).toBe(opened.id);

    const page = await admin.api.listCases({ limit: 1 });
    admin.wire(200, { cases: [CASE], 'nextCursor?': 'string' });
    expect(page.cases).toHaveLength(1);
    expect(typeof page.nextCursor).toBe('string');
    const next = await admin.api.listCases({ limit: 1, cursor: page.nextCursor });
    admin.wire(200, { cases: [CASE], 'nextCursor?': 'string' });
    expect(next.cases[0]?.id).not.toBe(page.cases[0]?.id);

    const all = await admin.api.listCases({ limit: 100 });
    // The seeded fixture cases are this firm's, beside the scratch ones.
    expect(all.cases.length).toBeGreaterThanOrEqual(3);

    const updated = await admin.api.updateCase(opened.id, {
      chapter: 13,
      court: { court: 'flnb', division: 'tallahassee' },
      filedAt: '2026-01-15',
    });
    admin.wire(200, CASE);
    expect(updated.chapter).toBe(13);
    expect(updated.division).toBe('tallahassee');
    expect(updated.filedAt).toBe('2026-01-15');
    // Read back from the store, not the response.
    const reread = await admin.api.getCase(opened.id);
    expect(reread.chapter).toBe(13);
    expect(reread.district).toBe(SCRATCH_DISTRICT);
    expect(reread.updatedAt >= opened.updatedAt).toBe(true);
    const cleared = await admin.api.updateCase(opened.id, { filedAt: null });
    admin.wire(200, CASE);
    expect(cleared.filedAt).toBeUndefined();

    // The docket facts are recorded, then cleared — absent, never null.
    const docketed = await admin.api.updateCase(opened.id, {
      judge: 'Hon. Contract Judge',
      officeFileNumber: 'CON-0001',
    });
    admin.wire(200, CASE);
    expect(docketed.judge).toBe('Hon. Contract Judge');
    expect(docketed.officeFileNumber).toBe('CON-0001');
    await admin.api.updateCase(opened.id, { judge: null, officeFileNumber: null });
    const undocketed = admin.wire(200, CASE);
    expect(undocketed).not.toHaveProperty('judge');
    expect(undocketed).not.toHaveProperty('officeFileNumber');

    await expectApiError(
      admin.api.createCase({ chapter: 7, court: 'nowhere', division: 'x', clientIds: [client.id] }),
      400,
      'ValidationError',
    );
    await expectApiError(
      admin.api.createCase({ chapter: 7, ...SCRATCH_COURT, clientIds: [] }),
      400,
      'ValidationError',
    );
    await expectApiError(admin.api.getCase('no-such-case'), 404, 'NotFoundError');
    await expectApiError(
      admin.api.updateCase(opened.id, { chapter: 9 as never }),
      400,
      'ValidationError',
    );
  });

  it('moves through statuses, keeps its history, archives, copies and deletes', async () => {
    const admin = firmAdmin();
    const { caseId, client } = await scratchCase();

    await admin.api.updateCase(caseId, { status: 'ready_to_file' });
    admin.wire(200, CASE);
    const history = await admin.api.listCaseStatusHistory(caseId);
    admin.wire(200, {
      history: [
        {
          changedAt: 'string',
          changedBy: 'string',
          fromStatus: 'string',
          toStatus: 'string',
          'filingId?': 'string',
        },
      ],
    });
    expect(history.at(-1)).toMatchObject({ fromStatus: 'intake', toStatus: 'ready_to_file' });

    const archived = await admin.api.setCaseArchived(caseId, true);
    admin.wire(200, CASE);
    expect(archived.archivedBy).toBe(seeded.firmAdmin.subject);
    expect(await allCaseIds()).not.toContain(caseId);
    expect(await allCaseIds({ archived: true })).toContain(caseId);
    const restored = await admin.api.setCaseArchived(caseId, false);
    admin.wire(200, CASE);
    expect(restored.archivedAt).toBeUndefined();
    expect(await allCaseIds()).toContain(caseId);

    // A record on the source, so the copy has a collection to carry: copying
    // an empty case never exercised the order the copy is written in.
    await admin.api.addCaseEntity(caseId, 'creditors', {
      name: 'Example Bank',
      provenance: { name: { source: 'staff_typed' } },
    });
    const copy = await admin.api.copyCase(caseId);
    admin.wire(201, CASE);
    expect(copy.id).not.toBe(caseId);
    expect(copy.status).toBe('intake');
    const copied = await admin.api.listCaseEntities(copy.id, 'creditors');
    expect(copied.map((c) => c.name)).toEqual(['Example Bank']);
    // The copy's Debtor 1 names its source on every value…
    const copyDebtor = (await admin.api.listDebtors(copy.id)).find(
      (d) => d.filing_role === 'debtor_1',
    );
    const sources = Object.values(copyDebtor?.provenance ?? {}).map(
      (entry) => (entry as { copied_from_case_id?: string }).copied_from_case_id,
    );
    expect(new Set(sources)).toEqual(new Set([caseId]));
    // …and the client the copy was opened for stays retained, with no stage.
    const retained = await admin.api.getFirmClient(client.id);
    expect(typeof retained.first_retained_at).toBe('string');
    expect(retained.first_retained_at! <= new Date().toISOString().slice(0, 10)).toBe(true);
    expect(admin.last().body).not.toHaveProperty('prospect_stage');

    await admin.api.deleteCase(copy.id);
    admin.wire(204);
    await expectApiError(admin.api.getCase(copy.id), 404, 'NotFoundError');
    await expectApiError(admin.api.deleteCase(copy.id), 404, 'NotFoundError');
    expect(await allCaseIds()).not.toContain(copy.id);
    expect(await allCaseIds({ archived: true })).not.toContain(copy.id);
  });

  needsSeeded(
    TARGET === 'offline' || UNLINKED.length > 0,
    'colleague without access to every case',
  ).it('assigns colleagues, and an unassigned colleague cannot see the case', async () => {
    const admin = firmAdmin();
    const { caseId } = await scratchCase();
    const colleague = await colleagueWithoutAccess();
    try {
      await expectApiError(colleague.client.api.getCase(caseId), 404, 'NotFoundError');

      await admin.api.assignCase(caseId, colleague.subject);
      admin.wire(204);
      const assignees = await admin.api.listCaseAssignees(caseId);
      admin.wire(200, {
        assignees: [{ subject: 'string', assignedAt: 'string', assignedBy: 'string' }],
      });
      expect(assignees.map((a) => a.subject)).toContain(colleague.subject);
      await colleague.client.api.getCase(caseId);
      colleague.client.wire(200, CASE);
      const theirs = await colleague.client.api.listCases({ limit: 100 });
      expect(theirs.cases.map((c) => c.id)).toContain(caseId);

      await admin.api.unassignCase(caseId, colleague.subject);
      admin.wire(204);
      await expectApiError(colleague.client.api.getCase(caseId), 404, 'NotFoundError');
      await expectApiError(admin.api.assignCase(caseId, 'no-such-subject'), 404, 'NotFoundError');
    } finally {
      await colleague.done();
    }
  });
});

describe('the debtors of a case', () => {
  it('saves Debtor 1, links Debtor 2 from a client, and copies both ways', async () => {
    const admin = firmAdmin();
    const { caseId, client } = await scratchCase();

    const debtors = await admin.api.listDebtors(caseId);
    admin.wire(200, { debtors: [DEBTOR] });
    expect(debtors).toHaveLength(1);
    expect(debtors[0]?.client_id).toBe(client.id);

    const saved = await admin.api.putDebtor(caseId, 'debtor_1', {
      name: { given: 'Changed', surname: client.name.surname ?? 'x' },
      phone: '555-0199',
      tax_id: { kind: 'ssn', value: '987-65-4320' },
      provenance: { 'name.given': TYPED, 'name.surname': TYPED, phone: TYPED, tax_id: TYPED },
    });
    const body = admin.wire(200, DEBTOR) as Record<string, unknown>;
    expect(saved.tax_id).toEqual({ kind: 'ssn', last_four: '4320' });
    // The full number never comes back.
    expect(JSON.stringify(body)).not.toContain('987-65-4320');
    // A debtor saved with provenance is a debtor read back with it.
    const reread = (await admin.api.listDebtors(caseId)).find((d) => d.id === saved.id);
    expect(reread?.name?.given).toBe('Changed');
    expect(reread?.provenance['name.given']).toMatchObject(TYPED);

    await expectApiError(
      admin.api.putDebtor(caseId, 'debtor_1', {
        name: { given: 'NoProvenance' },
      }),
      400,
      'ValidationError',
    );

    // The case's copy now differs from the client: re-copy it from the client.
    const recopied = await admin.api.copyDebtorFromClient(caseId, 'debtor_1');
    admin.wire(200, DEBTOR);
    expect(recopied.name?.given).toBe(client.name.given);
    expect(recopied.differs_from_client).toEqual([]);
    const fromClient = { source: 'client', client_id: client.id };
    expect(recopied.provenance['name.given']).toEqual(fromClient);
    // The questionnaire echoing what it loaded keeps the client's provenance.
    const SERVER_OWNED = new Set([
      'id',
      'case_id',
      'filing_role',
      'created_at',
      'updated_at',
      'client_id',
      'differs_from_client',
    ]);
    const echo = Object.fromEntries(
      Object.entries(recopied).filter(([key]) => !SERVER_OWNED.has(key)),
    );
    const echoed = await admin.api.putDebtor(caseId, 'debtor_1', echo as never);
    admin.wire(200, DEBTOR);
    expect(echoed.provenance['name.given']).toEqual(fromClient);

    // …or push the case's copy back to the client.
    await admin.api.putDebtor(caseId, 'debtor_1', {
      name: { given: 'Pushed', surname: client.name.surname ?? 'x' },
      provenance: { 'name.given': TYPED, 'name.surname': TYPED },
    });
    await admin.api.copyDebtorToClient(caseId, 'debtor_1');
    admin.wire(200, DEBTOR);
    expect((await admin.api.getFirmClient(client.id)).name.given).toBe('Pushed');

    // Debtor 2 arrives by linking a second client (201: copied in).
    const spouse = await scratchClient('Spouse');
    const linked = await admin.api.linkDebtorClient(caseId, 'debtor_2', spouse.id);
    admin.wire(201, DEBTOR);
    expect(linked.filing_role).toBe('debtor_2');
    expect(linked.client_id).toBe(spouse.id);
    // Re-linking an existing debtor moves only the link (200).
    await admin.api.linkDebtorClient(caseId, 'debtor_2', spouse.id);
    admin.wire(200, DEBTOR);
    // The same client cannot hold both roles.
    await expectApiError(
      admin.api.linkDebtorClient(caseId, 'debtor_2', client.id),
      400,
      'ValidationError',
    );

    const clientCases = await admin.api.listFirmClientCases(spouse.id);
    admin.wire(200, { cases: [{ filing_role: 'string', case: CASE }] });
    expect(clientCases[0]?.filing_role).toBe('debtor_2');

    await expectApiError(admin.api.listDebtors('no-such-case'), 404, 'NotFoundError');
  });
});

describe('tasks', () => {
  it('creates, reads, updates, lists (mine too) and deletes a task', async () => {
    const admin = firmAdmin();
    const { caseId } = await scratchCase();
    const created = await admin.api.createTask(caseId, {
      subject: 'Collect pay stubs',
      dueDate: '2026-12-01',
      assigneeSubject: seeded.firmAdmin.subject,
      formSeries: 'B106',
    });
    admin.wire(201, TASK);
    expect(created.done).toBe(false);

    const read = await admin.api.getTask(caseId, created.id);
    admin.wire(200, TASK);
    expect(read.subject).toBe('Collect pay stubs');

    const done = await admin.api.updateTask(caseId, created.id, { done: true, dueDate: null });
    admin.wire(200, TASK);
    expect(done.done).toBe(true);
    expect(typeof done.completedAt).toBe('string');
    expect(admin.last().body).not.toHaveProperty('dueDate');

    const listed = await admin.api.listTasks(caseId);
    admin.wire(200, { tasks: [TASK] });
    expect(listed.map((t) => t.id)).toContain(created.id);

    const mine = await admin.api.listMyTasks({ scope: 'firm' });
    admin.wire(200, { tasks: [TASK] });
    expect(mine.map((t) => t.id)).toContain(created.id);

    await expectApiError(admin.api.createTask(caseId, { subject: '' }), 400, 'ValidationError');
    await admin.api.deleteTask(caseId, created.id);
    admin.wire(204);
    await expectApiError(admin.api.getTask(caseId, created.id), 404, 'NotFoundError');
  });
});

describe('notes', () => {
  it('adds, edits, lists and deletes a note', async () => {
    const admin = firmAdmin();
    const { caseId } = await scratchCase();
    const added = await admin.api.addNote(caseId, {
      text: 'Called the client.',
      form_series: 'B101',
    });
    admin.wire(201, NOTE);
    const edited = await admin.api.putNote(caseId, added.id, { text: 'Called twice.' });
    admin.wire(200, NOTE);
    expect(edited.text).toBe('Called twice.');
    expect(edited.author_subject).toBe(seeded.firmAdmin.subject);
    const listed = await admin.api.listNotes(caseId);
    admin.wire(200, { notes: [NOTE] });
    expect(listed.map((n) => n.id)).toContain(added.id);
    await expectApiError(
      admin.api.addNote(caseId, { text: 'x'.repeat(2001) }),
      400,
      'ValidationError',
    );
    await admin.api.deleteNote(caseId, added.id);
    admin.wire(204);
    await expectApiError(admin.api.putNote(caseId, added.id, { text: 'x' }), 404, 'NotFoundError');
  });
});
