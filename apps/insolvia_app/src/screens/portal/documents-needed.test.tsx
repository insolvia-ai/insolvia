import { readFileSync } from 'node:fs';
import path from 'node:path';

import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { installFakeFileBrowser } from '@/screens/documents/testing';
import type { FakeFileBrowser } from '@/screens/documents/testing';
import { writePortalPendingAuthorization } from '@/session';
import {
  installFakeBrowser,
  jsonResponse,
  routeFetch,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

/**
 * The portal's "Documents we need" card (ADR 0023 PR 5 / #364), mounted on
 * the real `/portal` landing screen through the sign-in callback, as
 * portal-routes.test.tsx does.
 */

const PORTAL_CONFIG: AuthConfig = {
  domain: 'https://insolvia-test.auth.example.test',
  clientId: 'test-portal-client-id-000',
};

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolvePortalAuthConfig: () => PORTAL_CONFIG,
}));

const REQUEST_ID = '00000000-0000-4000-8000-0000000000e1';
const DOCUMENT_ID = '00000000-0000-4000-8000-0000000000d1';

const PORTAL_ME = {
  subject: '00000000-0000-4000-8000-0000000000a1',
  displayName: 'Pat Example',
  roles: ['debtor_1'],
  firm: { name: 'Example & Partners' },
  case: { chapter: 7, stage: 'intake' },
};

// Shaped as routes/portal_documents.py answers.
function requests(status: 'requested' | 'received') {
  return {
    requests: [
      {
        id: REQUEST_ID,
        title: 'Bank statements',
        kind: 'bank_statement',
        description: 'The last six months of statements for every account.',
        status,
        uploads:
          status === 'received'
            ? [
                {
                  id: DOCUMENT_ID,
                  requestId: REQUEST_ID,
                  fileName: 'june.pdf',
                  contentType: 'application/pdf',
                  byteSize: 2048,
                  uploadedAt: '2099-01-02T03:04:05.006Z',
                  status: 'stored',
                },
              ]
            : [],
      },
      {
        id: '00000000-0000-4000-8000-0000000000e2',
        title: 'Vehicle titles',
        kind: 'other',
        description: null,
        status: 'waived',
        uploads: [],
      },
    ],
    progress: {
      total: 1,
      received: status === 'received' ? 1 : 0,
      outstanding: status === 'received' ? 0 : 1,
      waived: 1,
    },
  };
}

describe('documents we need', () => {
  let browser: FakeBrowser;
  let files: FakeFileBrowser;
  const realFetch = globalThis.fetch;

  beforeEach(() => {
    browser = installFakeBrowser();
    files = installFakeFileBrowser();
    writePortalPendingAuthorization({
      state: 'test-state',
      codeVerifier: 'test-code-verifier',
      returnTo: null,
    });
  });

  afterEach(() => {
    files.restore();
    browser.restore();
    globalThis.fetch = realFetch;
  });

  it('lists what the firm asked for, and uploads against a request', async () => {
    let received = false;
    const fetchMock = jest.fn(
      routeFetch({
        '/oauth2/token': () => tokenEndpointResponse(),
        '/v1/portal/me': () => jsonResponse(200, PORTAL_ME),
        '/v1/portal/questionnaire': () => jsonResponse(200, { sections: [] }),
        '/v1/portal/document-requests': () =>
          jsonResponse(200, requests(received ? 'received' : 'requested')),
        // First: routeFetch matches by substring, in order, and this URL
        // contains the create route's.
        [`/v1/portal/documents/${DOCUMENT_ID}/complete`]: () => {
          received = true;
          return jsonResponse(200, {
            document: {
              id: DOCUMENT_ID,
              requestId: REQUEST_ID,
              fileName: 'june.pdf',
              contentType: 'application/pdf',
              byteSize: 2048,
              uploadedAt: '2099-01-02T03:04:05.006Z',
              status: 'stored',
            },
          });
        },
        '/v1/portal/documents': () =>
          jsonResponse(201, {
            document: {
              id: DOCUMENT_ID,
              requestId: REQUEST_ID,
              fileName: 'june.pdf',
              contentType: 'application/pdf',
              byteSize: 2048,
              uploadedAt: '2099-01-02T03:04:05.006Z',
              status: 'pending',
            },
            upload: {
              url: 'https://documents.example.invalid/put?X-Amz-Signature=not-real',
              method: 'PUT',
              headers: { 'Content-Type': 'application/pdf' },
              expiresAt: '2099-01-02T03:19:05.006Z',
            },
          }),
        '/put': () => jsonResponse(200, {}),
      }),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', {
      initialUrl: '/portal/auth/callback?code=test-code&state=test-state',
    });

    expect(await screen.findByRole('heading', { name: 'Documents we need' })).toBeTruthy();
    expect(await screen.findByText('0 of 1 documents received')).toBeTruthy();
    expect(screen.getByText('Needed')).toBeTruthy();
    expect(screen.getByText('No longer needed')).toBeTruthy();
    // A waived request takes no upload.
    expect(screen.queryByRole('button', { name: 'Upload Vehicle titles' })).toBeNull();

    files.offer({ name: 'june.pdf', type: 'application/pdf', size: 2048 });
    await userEvent.press(screen.getByRole('button', { name: 'Upload Bank statements' }));

    expect(await screen.findByText('1 of 1 documents received')).toBeTruthy();
    expect(screen.getByText(/You sent: june\.pdf/)).toBeTruthy();
    // routeFetch's type names only the URL; fetch is called with the init too.
    const calls = fetchMock.mock.calls as unknown as [string, RequestInit | undefined][];
    const create = calls.find(([url]) => url.endsWith('/v1/portal/documents'));
    // No kind: the server files it under the request's.
    expect(JSON.parse(String(create?.[1]?.body))).toEqual({
      requestId: REQUEST_ID,
      fileName: 'june.pdf',
      contentType: 'application/pdf',
      byteSize: 2048,
    });
    await waitFor(() => {
      expect(
        fetchMock.mock.calls.some(([url]) => String(url).endsWith(`${DOCUMENT_ID}/complete`)),
      ).toBe(true);
    });
  });

  it('never reaches for the staff session or API hook', () => {
    const source = readFileSync(path.join(__dirname, 'documents-needed.tsx'), 'utf8');
    expect(source).not.toMatch(/\buseSession\(/);
    expect(source).not.toMatch(/\buseApi\(/);
    expect(source).not.toMatch(/from '@\/api\/me'/);
  });
});
