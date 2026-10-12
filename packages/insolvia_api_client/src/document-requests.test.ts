// The contract pin for document request checklists (ADR 0023 PR 5 / #364),
// in its own file so the section reads as one unit. Keep every literal in
// sync with:
//   packages/insolvia_core/src/insolvia_core/document_requests.py
//     (checklist_json, request_json, requests_json, portal_request_json)
//   packages/insolvia_core/src/insolvia_core/documents.py (portal_document_json)
//   services/api/src/insolvia_api/api/routes/{document_requests,portal_documents}.py
//
// The conventions are index.test.ts's: a stubbed fetch, an obviously fake
// token, the client imported by package name. This repo is public.
import { describe, expect, test } from 'vitest';

import { ApiValidationException, InsolviaApiClient } from '@insolvia-ai/api-client';
import type { FetchLike } from '@insolvia-ai/api-client';

const ACCESS_TOKEN = 'test-access-token';
const BASE_URL = 'https://staging-api.insolvia.ai';
const CASE_ID = 'a3f1e9d0-4b2c-4d1e-9a7f-6c8e0d1f2a3b';
const REQUEST_ID = 'e1e10000-0000-4000-8000-0000000000e1';
const DOCUMENT_ID = 'c5d3a1b2-6e4f-4a3b-9c8d-2e1f0a9b8c7d';

interface Seen {
  readonly method: string;
  readonly url: string;
  readonly headers: Headers;
  readonly body: string;
}

function stubFetch(respond: (seen: Seen) => Response): {
  fetch: FetchLike;
  requests: () => readonly Seen[];
} {
  const requests: Seen[] = [];
  return {
    fetch: (input, init) => {
      const seen: Seen = {
        method: init?.method ?? 'GET',
        url: input,
        headers: new Headers(init?.headers),
        body: typeof init?.body === 'string' ? init.body : '',
      };
      requests.push(seen);
      return Promise.resolve(respond(seen));
    },
    requests: () => requests,
  };
}

function jsonResponse(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function clientFor(stub: { fetch: FetchLike }): InsolviaApiClient {
  return new InsolviaApiClient(BASE_URL, { fetch: stub.fetch, accessToken: () => ACCESS_TOKEN });
}

function only(stub: { requests: () => readonly Seen[] }): Seen {
  const [seen] = stub.requests();
  if (seen === undefined) {
    throw new Error('the client made no request');
  }
  return seen;
}

const ITEM = {
  title: 'Bank statements',
  kind: 'bank_statement',
  description: 'The last six months.',
};

const CHECKLIST = {
  isDefault: true,
  updatedAt: null,
  updatedBy: null,
  items: [ITEM],
  defaultItems: [ITEM],
};

const REQUEST = {
  id: REQUEST_ID,
  caseId: CASE_ID,
  title: 'Bank statements',
  kind: 'bank_statement',
  description: null,
  status: 'received',
  documentIds: [DOCUMENT_ID],
  createdAt: '2099-01-02T03:04:05.000006Z',
  updatedAt: '2099-01-02T03:04:06.000006Z',
  receivedAt: '2099-01-02T03:04:06.000006Z',
};

const PROGRESS = { total: 6, received: 1, outstanding: 5, waived: 0 };

const PORTAL_UPLOAD = {
  id: DOCUMENT_ID,
  requestId: REQUEST_ID,
  fileName: 'statement.pdf',
  contentType: 'application/pdf',
  byteSize: 2048,
  uploadedAt: '2099-01-02T03:04:05.006Z',
  status: 'stored',
};

const UPLOAD_BLOCK = {
  url: 'https://documents.example.invalid/cases/x/y?X-Amz-Signature=not-a-real-signature',
  method: 'PUT',
  headers: { 'Content-Type': 'application/pdf', 'x-amz-tagging': 'upload=unconfirmed' },
  expiresAt: '2099-01-02T03:19:05.006Z',
};

describe('the firm document checklist', () => {
  test('GETs /v1/firm/document-checklist and decodes the default beside the list', async () => {
    const stub = stubFetch(() => jsonResponse(CHECKLIST, 200));

    const checklist = await clientFor(stub).getFirmDocumentChecklist();

    const seen = only(stub);
    expect([seen.method, seen.url]).toEqual(['GET', `${BASE_URL}/v1/firm/document-checklist`]);
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(checklist).toEqual(CHECKLIST);
  });

  test('PUTs the whole list, omitting an absent description', async () => {
    const stub = stubFetch(() => jsonResponse({ ...CHECKLIST, isDefault: false }, 200));

    await clientFor(stub).saveFirmDocumentChecklist({
      items: [
        { title: 'Lease', kind: 'other' },
        { ...ITEM, kind: 'bank_statement' },
      ],
    });

    const seen = only(stub);
    expect(seen.method).toBe('PUT');
    expect(JSON.parse(seen.body)).toEqual({
      items: [{ title: 'Lease', kind: 'other' }, ITEM],
    });
  });

  test('a 400 on a save is keyed items.<index>.<field>', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        { error: 'validation failed', fields: { 'items.0.title': 'A title is required.' } },
        400,
      ),
    );

    const error = await clientFor(stub)
      .saveFirmDocumentChecklist({ items: [{ title: '', kind: 'other' }] })
      .catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(ApiValidationException);
  });

  test('DELETE resets to the default', async () => {
    const stub = stubFetch(() => jsonResponse(CHECKLIST, 200));

    const checklist = await clientFor(stub).resetFirmDocumentChecklist();

    expect(only(stub).method).toBe('DELETE');
    expect(checklist.isDefault).toBe(true);
  });
});

describe("a case's document requests", () => {
  test('lists them with progress', async () => {
    const stub = stubFetch(() => jsonResponse({ requests: [REQUEST], progress: PROGRESS }, 200));

    const listed = await clientFor(stub).listDocumentRequests(CASE_ID);

    expect(only(stub).url).toBe(`${BASE_URL}/v1/cases/${CASE_ID}/document-requests`);
    expect(listed).toEqual({ requests: [REQUEST], progress: PROGRESS });
  });

  test('applies the checklist and reports how many it added', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ requests: [REQUEST], progress: PROGRESS, added: 1 }, 200),
    );

    const applied = await clientFor(stub).applyDocumentChecklist(CASE_ID);

    const seen = only(stub);
    expect([seen.method, seen.url]).toEqual([
      'POST',
      `${BASE_URL}/v1/cases/${CASE_ID}/document-requests/from-checklist`,
    ]);
    expect(applied.added).toBe(1);
  });

  test('adds one request with a 201', async () => {
    const stub = stubFetch(() => jsonResponse({ ...REQUEST, status: 'requested' }, 201));

    const made = await clientFor(stub).addDocumentRequest(CASE_ID, {
      title: 'Divorce decree',
      kind: 'court_notice',
    });

    expect(JSON.parse(only(stub).body)).toEqual({ title: 'Divorce decree', kind: 'court_notice' });
    expect(made.status).toBe('requested');
  });

  test('PATCHes a status and DELETEs with a 204', async () => {
    const stub = stubFetch((seen) =>
      seen.method === 'DELETE'
        ? new Response(null, { status: 204 })
        : jsonResponse({ ...REQUEST, status: 'waived' }, 200),
    );
    const client = clientFor(stub);

    const waived = await client.setDocumentRequestStatus(CASE_ID, REQUEST_ID, 'waived');
    await client.deleteDocumentRequest(CASE_ID, REQUEST_ID);

    const [patch, del] = stub.requests();
    expect(patch?.method).toBe('PATCH');
    expect(patch?.url).toBe(`${BASE_URL}/v1/cases/${CASE_ID}/document-requests/${REQUEST_ID}`);
    expect(JSON.parse(patch?.body ?? '')).toEqual({ status: 'waived' });
    expect(waived.status).toBe('waived');
    expect(del?.method).toBe('DELETE');
  });

  test('a staff upload names its request', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        {
          document: {
            id: DOCUMENT_ID,
            caseId: CASE_ID,
            kind: 'bank_statement',
            fileName: 'statement.pdf',
            contentType: 'application/pdf',
            byteSize: 2048,
            uploadedAt: '2099-01-02T03:04:05.006Z',
            status: 'pending',
            channel: 'staff',
            requestId: REQUEST_ID,
          },
          upload: UPLOAD_BLOCK,
        },
        201,
      ),
    );

    const created = await clientFor(stub).createDocument(CASE_ID, {
      kind: 'bank_statement',
      fileName: 'statement.pdf',
      contentType: 'application/pdf',
      byteSize: 2048,
      requestId: REQUEST_ID,
    });

    expect(JSON.parse(only(stub).body)).toMatchObject({ requestId: REQUEST_ID });
    expect(created.document.requestId).toBe(REQUEST_ID);
    expect(created.document.channel).toBe('staff');
  });
});

describe('the portal', () => {
  test('GETs /v1/portal/document-requests with the client’s own uploads', async () => {
    const body = {
      requests: [
        {
          id: REQUEST_ID,
          title: 'Bank statements',
          kind: 'bank_statement',
          description: null,
          status: 'received',
          uploads: [PORTAL_UPLOAD],
        },
      ],
      progress: PROGRESS,
    };
    const stub = stubFetch(() => jsonResponse(body, 200));

    const requests = await clientFor(stub).getPortalDocumentRequests();

    expect(only(stub).url).toBe(`${BASE_URL}/v1/portal/document-requests`);
    expect(requests).toEqual(body);
  });

  test('uploads end to end: create with no kind, PUT verbatim, complete', async () => {
    const stub = stubFetch((seen) => {
      if (seen.url === UPLOAD_BLOCK.url) {
        return new Response(null, { status: 200 });
      }
      if (seen.url.endsWith('/complete')) {
        return jsonResponse({ document: PORTAL_UPLOAD }, 200);
      }
      return jsonResponse(
        { document: { ...PORTAL_UPLOAD, status: 'pending' }, upload: UPLOAD_BLOCK },
        201,
      );
    });

    const done = await clientFor(stub).uploadPortalDocument({
      requestId: REQUEST_ID,
      file: new Blob(['%PDF-not-really']),
      fileName: 'statement.pdf',
      contentType: 'application/pdf',
    });

    const [create, put, complete] = stub.requests();
    expect([create?.method, create?.url]).toEqual(['POST', `${BASE_URL}/v1/portal/documents`]);
    expect(JSON.parse(create?.body ?? '')).toEqual({
      requestId: REQUEST_ID,
      fileName: 'statement.pdf',
      contentType: 'application/pdf',
      byteSize: 15,
    });
    expect(put?.method).toBe('PUT');
    expect(put?.headers.get('authorization')).toBeNull();
    expect(put?.headers.get('x-amz-tagging')).toBe('upload=unconfirmed');
    expect([complete?.method, complete?.url]).toEqual([
      'POST',
      `${BASE_URL}/v1/portal/documents/${DOCUMENT_ID}/complete`,
    ]);
    expect(done).toEqual(PORTAL_UPLOAD);
  });
});
