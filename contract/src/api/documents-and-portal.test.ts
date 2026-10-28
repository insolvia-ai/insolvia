// Documents and the client portal (ADR 0023): the firm asks for documents,
// invites the client, the client answers and uploads through presigned PUTs,
// and the firm reviews what arrived.
import { describe, expect, it } from 'vitest';

import { firmAdmin, portalClient, scratchCase, tag } from '../support/actors.ts';
import { seeded } from '../support/env.ts';
import { fixtureCase } from '../support/fixtures.ts';
import { DOCUMENT } from '../support/shapes.ts';
import { contractClient } from '../support/client.ts';
import { staffToken } from '../support/tokens.ts';
import { expectApiError, expectShape, type Spec } from '../support/wire.ts';

const PDF = new TextEncoder().encode('%PDF-1.4\n% contract suite upload\n%%EOF\n');

const UPLOAD: Spec = { url: 'string', method: 'string', headers: 'object', expiresAt: 'string' };
const DOWNLOAD: Spec = { url: 'string', method: 'string', expiresAt: 'string' };
const PROGRESS: Spec = {
  total: 'integer',
  received: 'integer',
  outstanding: 'integer',
  waived: 'integer',
};
const DOCUMENT_REQUEST: Spec = {
  id: 'string',
  caseId: 'string',
  title: 'string',
  kind: 'string',
  description: 'string|null',
  status: 'string',
  documentIds: ['string'],
  createdAt: 'string',
  updatedAt: 'string',
  receivedAt: 'string|null',
};
const PORTAL_UPLOAD: Spec = {
  id: 'string',
  requestId: 'string|null',
  fileName: 'string',
  contentType: 'string',
  byteSize: 'integer',
  uploadedAt: 'string',
  status: 'string',
};
const PORTAL_CLIENT: Spec = {
  subject: 'string',
  email: 'string',
  displayName: 'string',
  roles: ['string'],
  status: 'string',
  invitedBy: 'string',
  createdAt: 'string',
  updatedAt: 'string',
};
const PORTAL_ME: Spec = {
  subject: 'string',
  displayName: 'string',
  roles: ['string'],
  firm: { name: 'string' },
  case: { chapter: 'integer', stage: 'string' },
};
const PORTAL_ANSWER: Spec = {
  id: 'string',
  questionId: 'string',
  sectionId: 'string',
  filingRole: 'string',
  value: 'object',
  status: 'string',
  createdAt: 'string',
  updatedAt: 'string',
};
const CANDIDATE: Spec = {
  id: 'string',
  entityType: 'string',
  status: 'string',
  payload: 'object',
  origin: { channel: 'string', clientId: 'string', subject: 'string' },
  createdAt: 'string',
  updatedAt: 'string',
  'question?': {
    id: 'string',
    sectionId: 'string',
    sectionTitle: 'string',
    text: 'string',
    filingRole: 'string',
  },
  'client?': { displayName: 'string' },
};

describe('staff documents', () => {
  it('upload through the presigned PUT, list, download, and delete', async () => {
    const admin = firmAdmin();
    const { caseId } = await scratchCase();

    // The client's whole upload: create → PUT the bytes → complete.
    const uploaded = await admin.api.uploadDocument(caseId, {
      file: new Blob([PDF], { type: 'application/pdf' }),
      fileName: 'stub.pdf',
      kind: 'pay_stub',
      contentType: 'application/pdf',
    });
    expect(uploaded.status).toBe('stored');
    expect(uploaded.byteSize).toBe(PDF.byteLength);
    const create = admin.exchanges.find(
      (e) => e.method === 'POST' && e.path.endsWith('/documents'),
    );
    expect(create?.status).toBe(201);
    expectShape(create?.body, { document: DOCUMENT, upload: UPLOAD });
    admin.wire(200, { document: DOCUMENT });

    // The same three steps, one call each.
    const created = await admin.api.createDocument(caseId, {
      kind: 'bank_statement',
      fileName: 'statement.pdf',
      contentType: 'application/pdf',
      byteSize: PDF.byteLength,
    });
    admin.wire(201, { document: DOCUMENT, upload: UPLOAD });
    expect(created.document.status).toBe('pending');
    // Completing before the bytes arrive is a conflict.
    await expectApiError(admin.api.completeDocument(caseId, created.document.id), 409);
    const put = await fetch(created.upload.url, {
      method: created.upload.method,
      headers: created.upload.headers,
      body: PDF,
    });
    expect(put.status).toBe(200);
    const completed = await admin.api.completeDocument(caseId, created.document.id);
    admin.wire(200, { document: DOCUMENT });
    expect(completed.status).toBe('stored');

    const listed = await admin.api.listDocuments(caseId);
    admin.wire(200, { documents: [DOCUMENT] });
    expect(listed.map((d) => d.id)).toEqual(
      expect.arrayContaining([uploaded.id, created.document.id]),
    );

    const download = await admin.api.getDocumentUrl(caseId, uploaded.id);
    admin.wire(200, DOWNLOAD);
    const bytes = new Uint8Array(await (await fetch(download.url)).arrayBuffer());
    expect(bytes).toEqual(PDF);

    await expectApiError(
      admin.api.createDocument(caseId, {
        kind: 'pay_stub',
        fileName: 'x.exe',
        contentType: 'application/x-msdownload' as never,
        byteSize: 10,
      }),
      400,
      'ValidationError',
    );

    await admin.api.deleteDocument(caseId, uploaded.id);
    admin.wire(204);
    await expectApiError(admin.api.getDocumentUrl(caseId, uploaded.id), 404, 'NotFoundError');
  });
});

describe('the client portal', () => {
  it('a seeded portal client sees their firm and case', async () => {
    const client = portalClient(seeded.portalClient.subject);
    const me = await client.api.getPortalMe();
    client.wire(200, PORTAL_ME);
    expect(me.firm.name).toBe('Dev Firm');
    expect(me.case.chapter).toBe((await fixtureCase(7)).chapter);
    // A staff token is not a portal token, and the reverse holds too.
    await expectApiError(
      contractClient(staffToken(seeded.firmAdmin.subject)).api.getPortalMe(),
      401,
    );
    await expectApiError(client.api.listCases(), 401, 'Unauthorized');
  });

  it('invite → answer → request → upload → review → revoke', async () => {
    const admin = firmAdmin();
    const { caseId } = await scratchCase();

    // Invite the debtor to the portal.
    const email = `portal-${tag()}@insolvia.test`;
    const invited = await admin.api.invitePortalClient(caseId, {
      email,
      displayName: 'Portal Client',
      roles: ['debtor_1'],
    });
    admin.wire(201, PORTAL_CLIENT);
    expect(invited.status).toBe('invited');
    await expectApiError(
      admin.api.invitePortalClient(caseId, { email: 'nope', displayName: '' }),
      400,
      'ValidationError',
    );

    const clients = await admin.api.listPortalClients(caseId);
    admin.wire(200, { clients: [PORTAL_CLIENT] });
    expect(clients.map((c) => c.subject)).toContain(invited.subject);
    await admin.api.resendPortalInvitation(caseId, invited.subject);
    admin.wire(204);
    await expectApiError(admin.api.resendPortalInvitation(caseId, 'no-such-subject'), 404);

    // The client signs in.
    const client = portalClient(invited.subject);
    const me = await client.api.getPortalMe();
    client.wire(200, PORTAL_ME);
    expect(me.roles).toEqual(['debtor_1']);

    // …and answers a question.
    const questionnaire = await client.api.getPortalQuestionnaire();
    client.wire(200, {
      sections: [
        {
          id: 'string',
          title: 'string',
          instructions: 'string',
          questions: [
            {
              id: 'string',
              text: 'string',
              'help?': 'string',
              repeats: 'boolean',
              perDebtor: 'boolean',
              inputs: [{ key: 'string', label: 'string', type: 'string', required: 'boolean' }],
            },
          ],
        },
      ],
    });
    const [question, other] = questionnaire.sections
      .flatMap((s) => s.questions)
      .filter((q) => q.inputs.length > 0 && q.inputs.every((i) => i.type === 'text'));
    expect(question).toBeDefined();
    expect(other).toBeDefined();
    const value = Object.fromEntries(question!.inputs.map((i) => [i.key, 'Contract answer']));
    const answer = await client.api.createPortalAnswer({ questionId: question!.id, value });
    client.wire(201, PORTAL_ANSWER);
    expect(answer.status).toBe('pending');
    const revised = Object.fromEntries(question!.inputs.map((i) => [i.key, 'Revised answer']));
    const updated = await client.api.updatePortalAnswer(answer.id, revised);
    client.wire(200, PORTAL_ANSWER);
    expect(updated.value).toEqual(revised);
    const answers = await client.api.listPortalAnswers();
    client.wire(200, { answers: [PORTAL_ANSWER] });
    expect(answers.map((a) => a.id)).toContain(answer.id);
    await expectApiError(
      client.api.createPortalAnswer({ questionId: 'no-such-question', value }),
      400,
      'ValidationError',
    );

    // One pending answer per question…
    await expectApiError(
      client.api.createPortalAnswer({ questionId: question!.id, value }),
      409,
      'ConflictError',
    );
    // …and an answer can be withdrawn before review.
    const otherValue = Object.fromEntries(other!.inputs.map((i) => [i.key, 'Other answer']));
    const second = await client.api.createPortalAnswer({
      questionId: other!.id,
      value: otherValue,
    });
    const withdrawn = await client.api.withdrawPortalAnswer(second.id);
    client.wire(200, PORTAL_ANSWER);
    expect(withdrawn.status).toBe('withdrawn');

    // The firm sees the answer as a pending candidate and accepts it.
    const pending = await admin.api.listExtractionCandidates(caseId, 'pending');
    admin.wire(200, { candidates: [CANDIDATE] });
    const candidate = pending.find((c) => c.origin.channel === 'client');
    expect(candidate).toBeDefined();
    expect(candidate!.client?.displayName).toBe('Portal Client');
    const reviewed = await admin.api.reviewExtractionCandidate(caseId, candidate!.id, {
      action: 'reject',
    });
    admin.wire(200, { candidate: CANDIDATE, 'record?': 'object' });
    expect(reviewed.candidate.status).toBe('rejected');
    await expectApiError(
      admin.api.reviewExtractionCandidate(caseId, candidate!.id, { action: 'reject' }),
      409,
      'ConflictError',
    );
    // A reviewed answer is the firm's now: the client can no longer change it.
    await expectApiError(client.api.updatePortalAnswer(answer.id, value), 409);

    // The firm asks for documents: one by hand, the rest from the checklist.
    const asked = await admin.api.addDocumentRequest(caseId, {
      title: 'Last two pay stubs',
      kind: 'pay_stub',
      description: 'Both, please.',
    });
    admin.wire(201, DOCUMENT_REQUEST);
    const applied = await admin.api.applyDocumentChecklist(caseId);
    admin.wire(200, { requests: [DOCUMENT_REQUEST], progress: PROGRESS, added: 'integer' });
    expect(applied.added).toBeGreaterThan(0);
    const requests = await admin.api.listDocumentRequests(caseId);
    admin.wire(200, { requests: [DOCUMENT_REQUEST], progress: PROGRESS });
    const extra = requests.requests.find((r) => r.id !== asked.id)!;
    const waived = await admin.api.setDocumentRequestStatus(caseId, extra.id, 'waived');
    admin.wire(200, DOCUMENT_REQUEST);
    expect(waived.status).toBe('waived');
    await admin.api.setDocumentRequestStatus(caseId, extra.id, 'requested');
    admin.wire(200, DOCUMENT_REQUEST);
    await expectApiError(
      admin.api.addDocumentRequest(caseId, { title: '', kind: 'pay_stub' }),
      400,
      'ValidationError',
    );

    // The client sees what is asked and uploads against it.
    const portalRequests = await client.api.getPortalDocumentRequests();
    client.wire(200, {
      requests: [
        {
          id: 'string',
          title: 'string',
          kind: 'string',
          description: 'string|null',
          status: 'string',
          uploads: [PORTAL_UPLOAD],
        },
      ],
      progress: PROGRESS,
    });
    expect(portalRequests.requests.map((r) => r.id)).toContain(asked.id);

    const upload = await client.api.uploadPortalDocument({
      requestId: asked.id,
      file: new Blob([PDF], { type: 'application/pdf' }),
      fileName: 'stub-1.pdf',
      contentType: 'application/pdf',
    });
    client.wire(200, { document: PORTAL_UPLOAD });
    expect(upload.requestId).toBe(asked.id);

    const created = await client.api.createPortalDocument({
      requestId: asked.id,
      fileName: 'stub-2.pdf',
      contentType: 'application/pdf',
      byteSize: PDF.byteLength,
    });
    client.wire(201, { document: PORTAL_UPLOAD, upload: UPLOAD });
    await fetch(created.upload.url, {
      method: created.upload.method,
      headers: created.upload.headers,
      body: PDF,
    });
    const completed = await client.api.completePortalDocument(created.document.id);
    client.wire(200, { document: PORTAL_UPLOAD });
    expect(completed.status).toBe('stored');
    await expectApiError(
      client.api.createPortalDocument({
        requestId: 'no-such-request',
        fileName: 'x.pdf',
        contentType: 'application/pdf',
        byteSize: 10,
      }),
      400,
      'ValidationError',
    );

    // The firm sees the client's uploads, and the request as received.
    const docs = await admin.api.listDocuments(caseId);
    expect(docs.filter((d) => d.channel === 'client')).toHaveLength(2);
    const after = await admin.api.listDocumentRequests(caseId);
    expect(after.requests.find((r) => r.id === asked.id)?.status).toBe('received');

    await admin.api.deleteDocumentRequest(caseId, extra.id);
    admin.wire(204);
    await expectApiError(admin.api.deleteDocumentRequest(caseId, extra.id), 404, 'NotFoundError');

    // Revoked, the client is refused — a 403, the token is still valid.
    const revoked = await admin.api.revokePortalClient(caseId, invited.subject);
    admin.wire(200, PORTAL_CLIENT);
    expect(revoked.status).toBe('revoked');
    await expectApiError(client.api.getPortalMe(), 403, 'ForbiddenError');
  });
});
