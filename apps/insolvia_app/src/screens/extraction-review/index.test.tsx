import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  caseBody,
  installFakeBrowser,
  jsonResponse,
  TEST_AUTH_CONFIG,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const CASE_ID = '00000000-0000-4000-8000-0000000000c1';
const CANDIDATE_ID = '00000000-0000-4000-8000-00000000ca01';
const DOC_ID = '00000000-0000-4000-8000-0000000000d1';
const ALICE = '00000000-0000-4000-8000-00000000a11c';

const ALL_PERMISSIONS = {
  cases: 'add_edit',
  intake: 'add_edit',
  documents: 'add_edit',
  extraction_review: 'add_edit',
  firm_administration: 'hidden',
};

function me(permissions: Record<string, string> = ALL_PERMISSIONS) {
  return {
    subject: ALICE,
    username: null,
    clientId: 'exampleappclientid000000',
    scopes: [],
    expiresAt: null,
    firm: {
      id: '00000000-0000-4000-8000-00000000f18a',
      name: 'Example & Partners',
      role: 'attorney',
      firstName: 'Alice',
      lastName: 'Attorney',
      displayName: 'Alice Attorney',
      isAdmin: false,
      accessAllCases: true,
      permissions,
    },
  };
}

/** One pending creditor, as `candidate_json` renders it. */
function candidate(overrides: Record<string, unknown> = {}) {
  return {
    id: CANDIDATE_ID,
    entityType: 'creditors',
    status: 'pending',
    payload: { name: 'First Example Bank', address: { city: 'Exampleville' } },
    origin: { channel: 'extraction', clientId: 'claude-opus-5', subject: ALICE },
    createdAt: '2026-09-04T10:00:00.000000Z',
    updatedAt: '2026-09-04T10:00:00.000000Z',
    documentId: DOC_ID,
    confidence: 0.95,
    locator: { document_id: DOC_ID, page: 2 },
    ...overrides,
  };
}

interface ApiStub {
  me?: () => Response;
  queue?: () => Response;
  documents?: () => Response;
  review?: (body: unknown) => Response;
}

function respond(stub: ApiStub, url: string, init?: RequestInit): Response {
  const method = init?.method ?? 'GET';
  if (url.endsWith('/v1/me')) {
    return (stub.me ?? (() => jsonResponse(200, me())))();
  }
  if (url.includes('/extraction/candidates') && method === 'POST') {
    const body: unknown = typeof init?.body === 'string' ? JSON.parse(init.body) : {};
    return (
      stub.review ??
      ((request: unknown) => {
        const action = (request as { action?: string }).action;
        return jsonResponse(200, {
          candidate: candidate({
            status: action === 'reject' ? 'rejected' : 'accepted',
            confirmedBy: ALICE,
            confirmedAt: '2026-09-04T11:00:00.000000Z',
          }),
          ...(action === 'reject' ? {} : { record: { id: 'rec-1' } }),
        });
      })
    )(body);
  }
  if (url.includes('/extraction/candidates')) {
    return (stub.queue ?? (() => jsonResponse(200, { candidates: [candidate()] })))();
  }
  if (url.endsWith('/documents')) {
    return (
      stub.documents ??
      (() =>
        jsonResponse(200, {
          documents: [
            {
              id: DOC_ID,
              caseId: CASE_ID,
              kind: 'credit_report',
              fileName: 'synthetic-report.pdf',
              contentType: 'application/pdf',
              byteSize: 2048,
              uploadedAt: '2026-09-04T09:00:00.000000Z',
              status: 'stored',
            },
          ],
        }))
    )();
  }
  // The case LAYOUT's two reads, which every screen under /cases/[caseId] now
  // mounts above it. They come before the throw and after the branches above,
  // so a test that wants a named debtor or a filed case still overrides them by
  // matching earlier. See `caseShellRoutes` in @/session/testing.
  if (url.endsWith('/debtors')) {
    return jsonResponse(200, { debtors: [] });
  }
  if (url.endsWith(CASE_ID)) {
    return jsonResponse(200, caseBody(CASE_ID));
  }
  throw new Error(`unexpected ${method} ${url}`);
}

/**
 * `/cases/<id>/extraction-review` — the app half of issue 8.9.
 *
 * What is asserted is what the issue itself demands: the queue shows enough
 * source context to verify a record, accept/correct/reject are per record,
 * a `view_only` reviewer confirms nothing, and a `hidden` one is told the
 * feature is off rather than shown a queue the API would refuse.
 */
describe('the extraction review screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(stub: ApiStub = {}) {
    const fetchMock = jest.fn(async (url: string, init?: RequestInit) =>
      url.includes('/oauth2/token') ? tokenEndpointResponse() : respond(stub, url, init),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/extraction-review` });
    return fetchMock;
  }

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
    jest.clearAllMocks();
  });

  it('lists a pending record with its fields and its source context', async () => {
    signedIn();

    expect(await screen.findByText('Creditor')).toBeTruthy();
    expect(screen.getByText('First Example Bank')).toBeTruthy();
    expect(screen.getByText('Exampleville')).toBeTruthy();
    // The verification context: which model, which document, which page,
    // how confident.
    expect(screen.getByText(/Extracted by claude-opus-5/)).toBeTruthy();
    expect(await screen.findByText(/from synthetic-report\.pdf/)).toBeTruthy();
    expect(screen.getByText(/page 2/)).toBeTruthy();
    expect(screen.getByText('95% confident')).toBeTruthy();
  });

  it('says so plainly when nothing is waiting', async () => {
    signedIn({ queue: () => jsonResponse(200, { candidates: [] }) });
    expect(await screen.findByText(/Nothing is waiting/)).toBeTruthy();
  });

  it('tells a hidden user the feature is off, and never fetches the queue', async () => {
    const fetchMock = signedIn({
      me: () => jsonResponse(200, me({ ...ALL_PERMISSIONS, extraction_review: 'hidden' })),
    });

    expect(await screen.findByText(/not enabled for your account/)).toBeTruthy();
    const urls = fetchMock.mock.calls.map(([url]) => String(url));
    expect(urls.some((url) => url.includes('/extraction/candidates'))).toBe(false);
  });

  it('shows a view-only reviewer the queue with no way to confirm', async () => {
    signedIn({
      me: () => jsonResponse(200, me({ ...ALL_PERMISSIONS, extraction_review: 'view_only' })),
    });

    expect(await screen.findByText('First Example Bank')).toBeTruthy();
    expect(screen.getByText(/read-only/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Accept/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Reject/ })).toBeNull();
  });

  it('accepts a record and says it entered the case', async () => {
    const fetchMock = signedIn();
    await screen.findByText('First Example Bank');

    await userEvent.press(
      screen.getByRole('button', { name: 'Accept this creditor into the case' }),
    );

    expect(await screen.findByText(/Accepted the creditor into the case/)).toBeTruthy();
    expect(screen.queryByText('First Example Bank')).toBeNull();
    const review = fetchMock.mock.calls.find(
      ([url, init]) =>
        String(url).includes(`/extraction/candidates/${CANDIDATE_ID}/review`) &&
        (init as RequestInit | undefined)?.method === 'POST',
    );
    expect(review).toBeTruthy();
    expect(JSON.parse((review![1] as RequestInit).body as string)).toEqual({
      action: 'accept',
    });
  });

  it('corrects a field and accepts with the corrected payload', async () => {
    const fetchMock = signedIn();
    await screen.findByText('First Example Bank');

    await userEvent.press(
      screen.getByRole('button', { name: 'Correct this creditor before accepting' }),
    );
    const nameInput = screen.getByLabelText('Name');
    await userEvent.type(nameInput, ' NA');
    await userEvent.press(
      screen.getByRole('button', { name: 'Accept this creditor with your corrections' }),
    );

    expect(await screen.findByText(/Saved your corrections/)).toBeTruthy();
    const review = fetchMock.mock.calls.find(([url]) =>
      String(url).includes(`/extraction/candidates/${CANDIDATE_ID}/review`),
    );
    const body = JSON.parse((review![1] as RequestInit).body as string) as {
      action: string;
      correctedPayload: { name: string; address: { city: string } };
    };
    expect(body.action).toBe('accept');
    expect(body.correctedPayload.name).toBe('First Example Bank NA');
    // Untouched fields survive the correction whole.
    expect(body.correctedPayload.address.city).toBe('Exampleville');
  });

  it('rejects a record and keeps the wording honest', async () => {
    const fetchMock = signedIn();
    await screen.findByText('First Example Bank');

    await userEvent.press(screen.getByRole('button', { name: 'Reject this creditor' }));

    expect(await screen.findByText(/Rejected the creditor/)).toBeTruthy();
    const review = fetchMock.mock.calls.find(
      ([url, init]) =>
        String(url).includes('/review') && (init as RequestInit | undefined)?.method === 'POST',
    );
    expect(JSON.parse((review![1] as RequestInit).body as string)).toEqual({
      action: 'reject',
    });
  });

  it('surfaces the confirm-the-creditor-first refusal in the server’s words', async () => {
    signedIn({
      queue: () =>
        jsonResponse(200, {
          candidates: [
            candidate({
              entityType: 'claims',
              payload: { creditor_id: 'other-candidate', amount: '310.00' },
            }),
          ],
        }),
      review: () =>
        jsonResponse(400, {
          error: 'ValidationError',
          fields: {
            creditor_id:
              'This record references another extracted candidate (creditors) that has not been accepted yet — review that one first.',
          },
        }),
    });
    await screen.findByText('Debt');

    await userEvent.press(screen.getByRole('button', { name: 'Accept this debt into the case' }));

    expect(await screen.findByText(/has not been accepted yet/)).toBeTruthy();
    // The card stays: nothing was reviewed.
    expect(screen.getByText('Debt')).toBeTruthy();
  });
});
