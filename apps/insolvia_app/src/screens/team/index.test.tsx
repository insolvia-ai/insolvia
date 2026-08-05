import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  installFakeBrowser,
  jsonResponse,
  routeFetch,
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
const ALICE = '00000000-0000-4000-8000-00000000a11c';
const BOB = '00000000-0000-4000-8000-00000000b0b0';
const GONE = '00000000-0000-4000-8000-00000000dead';

const ALL_ADD_EDIT = {
  cases: 'add_edit',
  intake: 'add_edit',
  documents: 'add_edit',
  extraction_review: 'add_edit',
  firm_administration: 'hidden',
};

function me(permissions: Record<string, string> = ALL_ADD_EDIT) {
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
      displayName: 'Alice Attorney',
      isAdmin: false,
      accessAllCases: true,
      permissions,
    },
  };
}

const DIRECTORY = {
  people: [
    { subject: ALICE, displayName: 'Alice Attorney', role: 'attorney' },
    { subject: BOB, displayName: 'Bob Paralegal', role: 'paralegal' },
  ],
};

function assignees(subjects: readonly string[]) {
  return {
    assignees: subjects.map((subject) => ({
      subject,
      assignedAt: '2026-08-04T10:00:00.000Z',
      assignedBy: ALICE,
    })),
  };
}

/**
 * `/cases/<id>/team` — who is linked to a matter.
 *
 * The screen makes TWO requests and joins them: subjects come from the case,
 * names come from the firm directory. These tests pin that join, including the
 * case where it cannot be made.
 */
describe('the case team screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(handlers: Readonly<Record<string, () => Response>>) {
    const route = routeFetch({ '/oauth2/token': tokenEndpointResponse, ...handlers });
    const fetchMock = jest.fn((url: string, _init?: RequestInit) => route(url));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/team` });
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

  it('shows assignees as people, not as subjects', async () => {
    signedIn({
      '/v1/me': () => jsonResponse(200, me()),
      '/v1/firm/directory': () => jsonResponse(200, DIRECTORY),
      [`/v1/cases/${CASE_ID}/assignees`]: () => jsonResponse(200, assignees([ALICE])),
    });

    expect(await screen.findByText('Alice Attorney')).toBeTruthy();
    expect(screen.queryByText(ALICE)).toBeNull();
  });

  it('still shows a subject the directory cannot resolve', async () => {
    // A case opened by somebody since removed from the firm. Dropping the row
    // would silently understate who has had access to the file — this is
    // history, not a picker.
    signedIn({
      '/v1/me': () => jsonResponse(200, me()),
      '/v1/firm/directory': () => jsonResponse(200, DIRECTORY),
      [`/v1/cases/${CASE_ID}/assignees`]: () => jsonResponse(200, assignees([GONE])),
    });

    expect(await screen.findByText(GONE)).toBeTruthy();
  });

  it('offers only colleagues who are not already on it', async () => {
    signedIn({
      '/v1/me': () => jsonResponse(200, me()),
      '/v1/firm/directory': () => jsonResponse(200, DIRECTORY),
      [`/v1/cases/${CASE_ID}/assignees`]: () => jsonResponse(200, assignees([ALICE])),
    });
    await screen.findByText('Alice Attorney');

    expect(screen.getByRole('button', { name: 'Add Bob Paralegal to this case' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Add Alice Attorney to this case' })).toBeNull();
  });

  it('PUTs the subject into the path when adding somebody', async () => {
    const fetchMock = signedIn({
      '/v1/me': () => jsonResponse(200, me()),
      '/v1/firm/directory': () => jsonResponse(200, DIRECTORY),
      [`/v1/cases/${CASE_ID}/assignees`]: () => jsonResponse(200, assignees([ALICE])),
    });
    await screen.findByText('Alice Attorney');

    await userEvent
      .setup()
      .press(screen.getByRole('button', { name: 'Add Bob Paralegal to this case' }));

    const put = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT');
    expect(put?.[0]).toContain(`/v1/cases/${CASE_ID}/assignees/${BOB}`);
  });

  it('names every remove button after the person it removes', async () => {
    // A column of identical "Remove" buttons is the WCAG 2.4.4 failure a list
    // of identical links is, moved to buttons — and here the mistake removes
    // the wrong colleague from a case.
    signedIn({
      '/v1/me': () => jsonResponse(200, me()),
      '/v1/firm/directory': () => jsonResponse(200, DIRECTORY),
      [`/v1/cases/${CASE_ID}/assignees`]: () => jsonResponse(200, assignees([ALICE, BOB])),
    });
    await screen.findByText('Bob Paralegal');

    expect(
      screen.getByRole('button', { name: 'Remove Alice Attorney from this case' }),
    ).toBeTruthy();
    expect(
      screen.getByRole('button', { name: 'Remove Bob Paralegal from this case' }),
    ).toBeTruthy();
  });

  it('shows no controls to a view-only caller', async () => {
    // Hiding them is a courtesy, never a control — the server refuses either
    // way. What it buys is a screen that does not offer something that 403s.
    signedIn({
      '/v1/me': () => jsonResponse(200, me({ ...ALL_ADD_EDIT, cases: 'view_only' })),
      '/v1/firm/directory': () => jsonResponse(200, DIRECTORY),
      [`/v1/cases/${CASE_ID}/assignees`]: () => jsonResponse(200, assignees([ALICE])),
    });
    await screen.findByText('Alice Attorney');

    expect(screen.queryByRole('button', { name: /Remove/ })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Add somebody' })).toBeNull();
  });

  it('says plainly when nobody is linked', async () => {
    // A real state, and not an error: unlinking the last person is allowed
    // because the firm's administrators still reach the case.
    signedIn({
      '/v1/me': () => jsonResponse(200, me()),
      '/v1/firm/directory': () => jsonResponse(200, DIRECTORY),
      [`/v1/cases/${CASE_ID}/assignees`]: () => jsonResponse(200, assignees([])),
    });

    expect(await screen.findByText(/Nobody is linked to this case/)).toBeTruthy();
  });
});
