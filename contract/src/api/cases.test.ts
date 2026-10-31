// A matter from the directory to an archived case: firm clients, opening a
// case, its debtors, its lifecycle, who works it, its tasks and its notes.
import { describe, expect, it } from 'vitest';

import {
  SCRATCH_COURT,
  firmAdmin,
  firmUser,
  scratchCase,
  scratchClient,
  tag,
} from '../support/actors.ts';
import { seeded } from '../support/env.ts';
import { CASE, DEBTOR, FIRM_CLIENT, NOTE, TASK } from '../support/shapes.ts';
import { expectApiError } from '../support/wire.ts';

const TYPED = { source: 'staff_typed' } as const;

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

    const cases = await admin.api.listFirmClientCases(created.id);
    admin.wire(200, { cases: ['object'] });
    expect(cases).toEqual([]);

    // Merge a duplicate into it; the duplicate is archived and points at it.
    const duplicate = await scratchClient('Dana');
    const merged = await admin.api.mergeFirmClient(created.id, duplicate.id);
    admin.wire(200, { client: FIRM_CLIENT, merged: FIRM_CLIENT });
    expect(merged.merged.merged_into).toBe(created.id);
    expect(merged.merged.status).toBe('archived');
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
    expect(opened.status).toBe('intake');
    expect(opened.court).toBe('flnb');
    expect(opened.createdBy).toBe(seeded.firmAdmin.subject);

    const read = await admin.api.getCase(opened.id);
    admin.wire(200, CASE);
    expect(read.id).toBe(opened.id);

    const page = await admin.api.listCases({ limit: 1 });
    admin.wire(200, { cases: [CASE], 'nextCursor?': 'string' });
    expect(page.cases).toHaveLength(1);
    expect(page.nextCursor).toBeTypeOf('string');
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
    const cleared = await admin.api.updateCase(opened.id, { filedAt: null });
    admin.wire(200, CASE);
    expect(cleared.filedAt).toBeUndefined();

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
    const { caseId } = await scratchCase();

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
    const archivedList = await admin.api.listCases({ archived: true, limit: 100 });
    expect(archivedList.cases.map((c) => c.id)).toContain(caseId);
    const restored = await admin.api.setCaseArchived(caseId, false);
    admin.wire(200, CASE);
    expect(restored.archivedAt).toBeUndefined();

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

    await admin.api.deleteCase(copy.id);
    admin.wire(204);
    await expectApiError(admin.api.getCase(copy.id), 404, 'NotFoundError');
    await expectApiError(admin.api.deleteCase(copy.id), 404, 'NotFoundError');
  });

  it('assigns colleagues, and an unassigned colleague cannot see the case', async () => {
    const admin = firmAdmin();
    const { caseId } = await scratchCase();
    const colleague = await admin.api.addFirmUser({
      email: `assignee-${tag()}@insolvia.test`,
      firstName: 'Assigned',
      lastName: 'Staff',
      role: 'staff',
      accessAllCases: false,
    });
    const asColleague = firmUser(colleague.subject);
    await expectApiError(asColleague.api.getCase(caseId), 404, 'NotFoundError');

    await admin.api.assignCase(caseId, colleague.subject);
    admin.wire(204);
    const assignees = await admin.api.listCaseAssignees(caseId);
    admin.wire(200, {
      assignees: [{ subject: 'string', assignedAt: 'string', assignedBy: 'string' }],
    });
    expect(assignees.map((a) => a.subject)).toContain(colleague.subject);
    await asColleague.api.getCase(caseId);
    asColleague.wire(200, CASE);

    await admin.api.unassignCase(caseId, colleague.subject);
    admin.wire(204);
    await expectApiError(asColleague.api.getCase(caseId), 404, 'NotFoundError');
    await expectApiError(admin.api.assignCase(caseId, 'no-such-subject'), 404, 'NotFoundError');

    await admin.api.removeFirmUser(colleague.subject);
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
    expect(done.completedAt).toBeTypeOf('string');
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
