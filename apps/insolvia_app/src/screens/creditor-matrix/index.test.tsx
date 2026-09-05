import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { installFakeFileBrowser } from '@/screens/documents/testing';
import type { FakeFileBrowser } from '@/screens/documents/testing';
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
const CREDITOR_ID = '00000000-0000-4000-8000-0000000000e1';
const UNNAMED_CREDITOR_ID = '00000000-0000-4000-8000-0000000000e2';

/** The exact CRLF text `matrix_json` carries when the list is clean. */
const MATRIX_CONTENT = 'Example Bank\r\nPO Box 15168\r\nWilmington DE 19850\r\n';

interface ApiStub {
  /** `GET /v1/cases/<id>/creditors` — the names load on mount. */
  creditors?: Answer;
  /** `GET /v1/cases/<id>/creditor-matrix` — the generation itself. */
  matrix?: Answer;
}

type Answer = () => Response;

function respond(stub: ApiStub, url: string, init?: RequestInit): Response {
  const method = init?.method ?? 'GET';
  if (url.endsWith('/creditor-matrix')) {
    return (
      stub.matrix ??
      (() =>
        jsonResponse(200, {
          fileName: 'creditor-matrix.txt',
          creditorCount: 1,
          duplicatesOmitted: 0,
          problems: [],
          content: MATRIX_CONTENT,
        }))
    )();
  }
  if (url.endsWith('/creditors')) {
    return (stub.creditors ?? (() => jsonResponse(200, { creditors: [] })))();
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
 * `/cases/<id>/creditor-matrix` — the app half of issue #94 (issue #282).
 *
 * Rendered through the real router, signed in first, exactly as the packet
 * suite does. What is asserted is what a preparer would lose if it broke: that
 * a clean list saves the file byte-for-byte under the server's name, and that
 * a refused one shows every problem under the creditor it belongs to — named,
 * so the preparer knows which record to open.
 */
describe('the creditor matrix screen', () => {
  let browser: FakeBrowser;
  let files: FakeFileBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(stub: ApiStub) {
    const fetchMock = jest.fn(async (url: string, init?: RequestInit) =>
      url.includes('/oauth2/token') ? tokenEndpointResponse() : respond(stub, url, init),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/creditor-matrix` });
    return fetchMock;
  }

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    files = installFakeFileBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    files.restore();
    browser.restore();
    jest.clearAllMocks();
  });

  it('saves the file byte-for-byte under the server’s name when the list is clean', async () => {
    signedIn({});
    await screen.findByRole('button', { name: 'Generate the creditor matrix for this case' });

    await userEvent.press(
      screen.getByRole('button', { name: 'Generate the creditor matrix for this case' }),
    );

    expect(await screen.findByText('Saved creditor-matrix.txt.')).toBeTruthy();
    expect(screen.getByText(/creditor-matrix\.txt — 1 creditor\./)).toBeTruthy();
    expect(files.downloads).toHaveLength(1);
    const saved = files.downloads[0];
    if (saved === undefined) throw new Error('expected a recorded download');
    expect(saved.fileName).toBe('creditor-matrix.txt');
    // The data URL round trip must not touch the CRLF bytes the clerks expect.
    expect(decodeURIComponent(saved.url.slice('data:text/plain;charset=utf-8,'.length))).toBe(
      MATRIX_CONTENT,
    );
  });

  it('says how many duplicate blocks the court-required dedupe dropped', async () => {
    signedIn({
      matrix: () =>
        jsonResponse(200, {
          fileName: 'creditor-matrix.txt',
          creditorCount: 6,
          duplicatesOmitted: 2,
          problems: [],
          content: MATRIX_CONTENT,
        }),
    });
    await screen.findByRole('button', { name: 'Generate the creditor matrix for this case' });

    await userEvent.press(
      screen.getByRole('button', { name: 'Generate the creditor matrix for this case' }),
    );

    expect(
      await screen.findByText(/creditor-matrix\.txt — 6 creditors, 2 duplicate blocks omitted\./),
    ).toBeTruthy();
  });

  it('renders every problem under the creditor it belongs to, by name', async () => {
    signedIn({
      creditors: () =>
        jsonResponse(200, {
          creditors: [
            {
              id: CREDITOR_ID,
              case_id: CASE_ID,
              created_at: '2026-09-01T10:00:00.000000Z',
              updated_at: '2026-09-01T10:00:00.000000Z',
              provenance: { name: { source: 'staff_typed' } },
              name: 'Example Bank',
            },
          ],
        }),
      matrix: () =>
        jsonResponse(200, {
          fileName: 'creditor-matrix.txt',
          creditorCount: 0,
          duplicatesOmitted: 0,
          problems: [
            {
              creditorId: CREDITOR_ID,
              field: 'address.state',
              message: 'A state is required.',
            },
            {
              creditorId: CREDITOR_ID,
              field: 'address.postal_code',
              message: 'A ZIP code is required.',
            },
            {
              // A creditor the names load does not know — the heading
              // degrades to a numbered creditor, never to a bare uuid.
              creditorId: UNNAMED_CREDITOR_ID,
              field: 'name',
              message: 'A creditor needs a name to appear on the matrix.',
            },
          ],
        }),
    });
    await screen.findByRole('button', { name: 'Generate the creditor matrix for this case' });

    await userEvent.press(
      screen.getByRole('button', { name: 'Generate the creditor matrix for this case' }),
    );

    expect(await screen.findByText('The creditor list is not ready')).toBeTruthy();
    // Grouped: both address problems under the one named creditor.
    expect(screen.getByText('Example Bank')).toBeTruthy();
    expect(screen.getByText('Address — state')).toBeTruthy();
    expect(screen.getByText('A state is required.')).toBeTruthy();
    expect(screen.getByText('Address — ZIP code')).toBeTruthy();
    expect(screen.getByText('A ZIP code is required.')).toBeTruthy();
    expect(screen.getByText('Creditor 2')).toBeTruthy();
    expect(screen.getByText('A creditor needs a name to appear on the matrix.')).toBeTruthy();
    // Nothing was produced, and nothing was downloaded.
    expect(files.downloads).toHaveLength(0);
  });

  it('puts the case-level problem in a group of its own', async () => {
    signedIn({
      matrix: () =>
        jsonResponse(200, {
          fileName: 'creditor-matrix.txt',
          creditorCount: 0,
          duplicatesOmitted: 0,
          problems: [
            {
              field: 'creditors',
              message:
                'The case has no creditors — a matrix must list every creditor before it can be filed.',
            },
          ],
        }),
    });
    await screen.findByRole('button', { name: 'Generate the creditor matrix for this case' });

    await userEvent.press(
      screen.getByRole('button', { name: 'Generate the creditor matrix for this case' }),
    );

    expect(await screen.findByText('This case')).toBeTruthy();
    expect(screen.getByText(/The case has no creditors/)).toBeTruthy();
  });
});
