// Documents and the client portal (ADR 0023): the firm asks for documents,
// invites the client, the client answers and uploads through presigned PUTs,
// and the firm reviews what arrived.
import { describe, expect, it } from '@jest/globals';

import {
  firmAdmin,
  portalClient,
  scratchCase,
  seededPortalClient,
  tag,
  tokenOf,
} from '../support/actors.ts';
import { contractClient } from '../support/client.ts';
import { seeded } from '../support/env.ts';
import { restoreQuestionnaire } from '../support/firm-settings.ts';
import { fixtureCase } from '../support/fixtures.ts';
import { DOCUMENT } from '../support/shapes.ts';
import { needsBlobUrls, offlineOnly } from '../support/gates.ts';
import { person } from '../support/target.ts';
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

/**
 * The case the seed bound the portal client to — found through the case's
 * client listing, since no portal answer names a case (by design). A
 * long-lived stack may hold it on an older fixture version's case (a binding
 * the loader leaves alone, seeds/README.md), so it is looked up, not assumed.
 */
async function seededClientCaseId(): Promise<string> {
  const api = firmAdmin().api;
  let cursor: string | undefined;
  do {
    const page = await api.listCases({ limit: 100, cursor });
    for (const candidate of page.cases) {
      if (candidate.court !== 'flmb') continue;
      const clients = await api.listPortalClients(candidate.id);
      if (clients.some((c) => c.email === seeded.portalClient.email && c.status !== 'revoked')) {
        return candidate.id;
      }
    }
    cursor = page.nextCursor;
  } while (cursor !== undefined);
  throw new Error(`no case lists the seeded client ${seeded.portalClient.email} as live`);
}

describe('staff documents', () => {
  needsBlobUrls().it('upload through the presigned PUT, list, download, and delete', async () => {
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
    const who = person('client');
    const client = seededPortalClient();
    const me = await client.api.getPortalMe();
    const body = client.wire(200, PORTAL_ME) as { case: object };
    expect(me.firm.name).toBe(who.firmName);
    expect(me.displayName).toBe(who.displayName);
    expect(me.roles).toEqual(who.roles);
    expect(me.case.chapter).toBe((await fixtureCase(7)).chapter);
    // Chapter and stage, and nothing else of the case — not even its id
    // (ADR 0023 decision 4).
    expect(client.last().body).not.toHaveProperty('caseId');
    expect(Object.keys(body.case).sort()).toEqual(['chapter', 'stage']);
    // A staff token is not a portal token, and the reverse holds too.
    await expectApiError(contractClient(tokenOf('admin')).api.getPortalMe(), 401);
    await expectApiError(client.api.me(), 401, 'Unauthorized');
    await expectApiError(client.api.listCases(), 401, 'Unauthorized');
  });

  it('a section the firm switches off is gone for its client and kept for staff', async () => {
    const admin = firmAdmin();
    const client = seededPortalClient();
    const before = await admin.api.getFirmQuestionnaire();
    const target = before.sections.find((s) => s.switchable && s.enabled);
    expect(target).toBeDefined();
    try {
      await admin.api.saveFirmQuestionnaire({
        sections: before.sections.map((s) => ({
          id: s.id,
          enabled: s.id === target!.id ? false : s.enabled,
          instructions: s.instructions,
        })),
      });
      const seen = (await client.api.getPortalQuestionnaire()).sections.map((s) => s.id);
      expect(seen).not.toContain(target!.id);
      expect(seen).toContain('personal_information');
      const staffView = (await admin.api.getFirmQuestionnaire()).sections;
      expect(staffView.map((s) => s.id)).toEqual(before.sections.map((s) => s.id));
      expect(staffView.find((s) => s.id === target!.id)?.enabled).toBe(false);
    } finally {
      await restoreQuestionnaire(before);
    }
    const after = await admin.api.getFirmQuestionnaire();
    expect(after.isDefault).toBe(before.isDefault);
    expect(after.sections).toEqual(before.sections);
  });

  // The one spec that writes on a fixture case, and why: a portal token
  // reaches a case only through its binding, and the seeded client's is the
  // only binding a deployed stack has — so this is the one way to prove a
  // CLIENT's presigned PUT against the real bucket (no encryption header: the
  // bucket default is the case key, #392) and the API role's check of it. It
  // adds ONE ad-hoc request (never the checklist) and removes the document and
  // the request however it ends; what stays is append-only by design (the
  // access log) or expired by the bucket's lifecycle rule (the object's
  // noncurrent version). Nothing it does touches what fixtures.ts matches on.
  needsBlobUrls().it(
    'the seeded client’s upload satisfies a request on their case, and the case counts it',
    async () => {
      const admin = firmAdmin();
      const client = seededPortalClient();
      const caseId = await seededClientCaseId();
      const title = `Contract probe ${tag()}`;
      const request = await admin.api.addDocumentRequest(caseId, { title, kind: 'bank_statement' });
      let documentId: string | undefined;
      try {
        const before = (await admin.api.listDocumentRequests(caseId)).progress;

        // The client sees the request — and no case id anywhere in the answer.
        const listed = await client.api.getPortalDocumentRequests();
        expect(JSON.stringify(client.last().body)).not.toContain(caseId);
        const seen = listed.requests.find((r) => r.id === request.id);
        expect(seen).toMatchObject({ title, status: 'requested', uploads: [] });

        const created = await client.api.createPortalDocument({
          requestId: request.id,
          fileName: 'contract-client-probe.pdf',
          contentType: 'application/pdf',
          byteSize: PDF.byteLength,
        });
        documentId = created.document.id;
        expect(Object.keys(created.upload.headers).map((h) => h.toLowerCase())).not.toContain(
          'x-amz-server-side-encryption',
        );
        const put = await fetch(created.upload.url, {
          method: created.upload.method,
          headers: created.upload.headers,
          body: PDF,
        });
        expect(put.status).toBe(200);
        const completed = await client.api.completePortalDocument(documentId);
        expect(completed.status).toBe('stored');
        expect(completed.byteSize).toBe(PDF.byteLength);

        // The request reads received for staff, and the case's progress counts it.
        const after = await admin.api.listDocumentRequests(caseId);
        const mine = after.requests.find((r) => r.id === request.id);
        expect(mine?.status).toBe('received');
        expect(mine?.documentIds).toEqual([documentId]);
        expect(after.progress.received).toBe(before.received + 1);
        expect(after.progress.outstanding).toBe(before.outstanding - 1);
        expect(after.progress.total).toBe(before.total);
        // …for the client too…
        const answered = (await client.api.getPortalDocumentRequests()).requests.find(
          (r) => r.id === request.id,
        );
        expect(answered?.status).toBe('received');
        expect(answered?.uploads.map((u) => u.id)).toEqual([documentId]);
        // …and staff see it on the case as the client's, filed under the request's kind.
        const stored = (await admin.api.listDocuments(caseId)).find((d) => d.id === documentId);
        expect(stored).toMatchObject({
          channel: 'client',
          kind: 'bank_statement',
          requestId: request.id,
        });
      } finally {
        if (documentId !== undefined) {
          await admin.api.deleteDocument(caseId, documentId).catch(() => undefined);
        }
        await admin.api.deleteDocumentRequest(caseId, request.id).catch(() => undefined);
      }
    },
  );

  offlineOnly(
    'inviting mints a pool account and mails it; only the run’s own key can sign in as it',
  ).it('invite → answer → request → upload → review → revoke → re-invite', async () => {
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

    // After review the question takes a new answer, and this one is accepted:
    // it reaches the case only through the queue, carrying the client's
    // provenance, and the client sees it settled.
    const again = await client.api.createPortalAnswer({ questionId: question!.id, value });
    const accepted = await admin.api.reviewExtractionCandidate(caseId, again.id, {
      action: 'accept',
    });
    admin.wire(200, { candidate: CANDIDATE, 'record?': 'object' });
    expect(accepted.candidate.status).toBe('accepted');
    const provenance = Object.values(
      ((accepted.record ?? {}) as { provenance?: Record<string, unknown> }).provenance ?? {},
    ) as { source?: string; extraction_id?: string }[];
    const fromAnswer = provenance.filter((entry) => entry.extraction_id === again.id);
    expect(fromAnswer.length).toBeGreaterThan(0);
    expect(new Set(fromAnswer.map((entry) => entry.source))).toEqual(new Set(['client_answered']));
    const settled = await client.api.listPortalAnswers();
    expect(settled.find((a) => a.id === again.id)?.status).toBe('accepted');

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
    // Once: applying it again adds nothing.
    expect((await admin.api.applyDocumentChecklist(caseId)).added).toBe(0);
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
    const progressBefore = (await admin.api.listDocumentRequests(caseId)).progress;
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
    expect(after.progress.received).toBe(progressBefore.received + 1);
    expect(after.progress.outstanding).toBe(progressBefore.outstanding - 1);

    await admin.api.deleteDocumentRequest(caseId, extra.id);
    admin.wire(204);
    await expectApiError(admin.api.deleteDocumentRequest(caseId, extra.id), 404, 'NotFoundError');

    // Revoked, the client is refused — a 403, the token is still valid.
    const revoked = await admin.api.revokePortalClient(caseId, invited.subject);
    admin.wire(200, PORTAL_CLIENT);
    expect(revoked.status).toBe('revoked');
    await expectApiError(client.api.getPortalMe(), 403, 'ForbiddenError');

    // Re-invited, the same address is re-bound as the SAME person, and the
    // same token works again.
    const rebound = await admin.api.invitePortalClient(caseId, {
      email,
      displayName: 'Portal Client',
      roles: ['debtor_1'],
    });
    expect(rebound.subject).toBe(invited.subject);
    await client.api.getPortalMe();
    client.wire(200, PORTAL_ME);
  });
});
