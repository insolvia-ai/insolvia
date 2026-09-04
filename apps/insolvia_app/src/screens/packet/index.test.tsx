import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { installFakeFileBrowser } from '@/screens/documents/testing';
import type { FakeFileBrowser } from '@/screens/documents/testing';
import { writeRefreshToken } from '@/session';
import {
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
const PACKET_ID = '00000000-0000-4000-8000-0000000000p1';
const JOB_ID = '00000000-0000-4000-8000-0000000000j1';

/** A packet as `packet_json` renders it. `storageRef` is not among the keys. */
function packet(overrides: Record<string, unknown> = {}) {
  return {
    id: PACKET_ID,
    caseId: CASE_ID,
    jobId: JOB_ID,
    fileName: 'chapter7-packet.zip',
    contentType: 'application/zip',
    byteSize: 1843200,
    sha256: 'f2ca1bb6c7e907d06dafe4687e579fce76b37e4e93b7605022da52e6ccc26fd2',
    formRevisions: { 'form/b101': '2024-06-22' },
    creditorCount: 6,
    createdBy: '00000000-0000-4000-8000-0000000000a1',
    createdAt: '2026-09-03T10:00:00.123Z',
    ...overrides,
  };
}

/** A job as `job_json` renders it — the accept's 202 and the status polls. */
function job(overrides: Record<string, unknown> = {}) {
  return {
    id: JOB_ID,
    kind: 'packet_assembly',
    status: 'queued',
    createdBy: '00000000-0000-4000-8000-0000000000a1',
    attempts: 0,
    createdAt: '2026-09-03T10:00:00.123Z',
    updatedAt: '2026-09-03T10:00:00.123Z',
    ...overrides,
  };
}

interface ApiStub {
  /** `GET /v1/cases/<id>/packets`, called again after a successful assembly. */
  list?: Answer;
  /** `POST .../jobs` — the trigger's 202. */
  accept?: Answer;
  /** `GET .../jobs/<id>` — the poll. Called until the job settles. */
  status?: Answer;
  /** `GET .../packets/<id>/url`. */
  url?: Answer;
}

type Answer = () => Response | Promise<Response>;

function respond(stub: ApiStub, url: string, init?: RequestInit): Response | Promise<Response> {
  const method = init?.method ?? 'GET';
  if (url.endsWith('/jobs') && method === 'POST') {
    return (stub.accept ?? (() => jsonResponse(202, job())))();
  }
  if (url.includes('/jobs/')) {
    return (stub.status ?? (() => jsonResponse(200, job())))();
  }
  if (url.endsWith('/url')) {
    return (
      stub.url ??
      (() =>
        jsonResponse(200, {
          url: 'https://bucket.example.test/read-here',
          method: 'GET',
          expiresAt: '2026-09-03T10:05:00.123Z',
        }))
    )();
  }
  if (url.endsWith('/packets')) {
    return (stub.list ?? (() => jsonResponse(200, { packets: [] })))();
  }
  throw new Error(`unexpected ${method} ${url}`);
}

/**
 * `/cases/<id>/packet` — the app half of issue #96.
 *
 * Rendered through the real router, signed in first, exactly as the documents
 * suite does. What is asserted is what a preparer would lose if it broke: that
 * a blocked assembly shows the whole fix list instead of a toast, that a
 * failed one shows the job record's own words, and that the download mints on
 * press and opens under the packet's file name.
 */
describe('the filing packet screen', () => {
  let browser: FakeBrowser;
  let files: FakeFileBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(stub: ApiStub) {
    const fetchMock = jest.fn(async (url: string, init?: RequestInit) =>
      url.includes('/oauth2/token') ? tokenEndpointResponse() : respond(stub, url, init),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/packet` });
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

  it('says so plainly when nothing has been assembled yet', async () => {
    signedIn({});

    expect(await screen.findByText(/No packet has been assembled yet/)).toBeTruthy();
  });

  it('lists an assembled packet with its date, size and creditor count', async () => {
    signedIn({ list: () => jsonResponse(200, { packets: [packet()] }) });

    expect(await screen.findByText('chapter7-packet.zip')).toBeTruthy();
    expect(
      screen.getByText(/Assembled 2026-09-03 · 1.8 MB · 6 creditors on the matrix/),
    ).toBeTruthy();
  });

  it('downloads through a URL minted on press, under the packet file name', async () => {
    signedIn({ list: () => jsonResponse(200, { packets: [packet()] }) });
    await screen.findByText('chapter7-packet.zip');

    await userEvent.press(
      screen.getByRole('button', { name: 'Download the packet assembled 2026-09-03' }),
    );

    expect(await screen.findByText('Opened chapter7-packet.zip.')).toBeTruthy();
    expect(files.downloads).toEqual([
      { url: 'https://bucket.example.test/read-here', fileName: 'chapter7-packet.zip' },
    ]);
  });

  it('assembles: accepts the job, polls it, and reloads the list on success', async () => {
    const assembled = packet();
    let settled = false;
    const fetchMock = signedIn({
      // Empty before the run, one packet after — the reload is what puts the
      // new packet on screen.
      list: () => jsonResponse(200, { packets: settled ? [assembled] : [] }),
      accept: () => jsonResponse(202, job()),
      status: () => {
        settled = true;
        return jsonResponse(
          200,
          job({
            status: 'succeeded',
            attempts: 1,
            result: { outcome: 'assembled', packet: assembled },
          }),
        );
      },
    });
    await screen.findByText(/No packet has been assembled yet/);

    await userEvent.press(
      screen.getByRole('button', { name: 'Assemble the Chapter 7 filing packet for this case' }),
    );

    expect(
      await screen.findByText('Packet assembled. It is ready to download below.'),
    ).toBeTruthy();
    expect(await screen.findByText('chapter7-packet.zip')).toBeTruthy();
    const accepts = fetchMock.mock.calls.filter(
      ([url, init]) => init?.method === 'POST' && String(url).endsWith('/jobs'),
    );
    expect(accepts).toHaveLength(1);
    expect(JSON.parse(String(accepts[0]?.[1]?.body))).toEqual({ kind: 'packet_assembly' });
  });

  it('renders the whole fix list when the completeness gate refuses', async () => {
    signedIn({
      status: () =>
        jsonResponse(
          200,
          job({
            status: 'succeeded',
            attempts: 1,
            result: {
              outcome: 'blocked',
              problems: [
                {
                  source: 'creditors',
                  itemId: 'cred-1',
                  field: 'address.postal_code',
                  message: 'A ZIP code is required.',
                },
                {
                  source: 'form/b101',
                  message: 'line 9: 3 prior cases but the form prints 2 rows',
                },
              ],
            },
          }),
        ),
    });
    await screen.findByText(/No packet has been assembled yet/);

    await userEvent.press(
      screen.getByRole('button', { name: 'Assemble the Chapter 7 filing packet for this case' }),
    );

    expect(await screen.findByText('The case is not ready to file')).toBeTruthy();
    expect(screen.getByText('Creditors')).toBeTruthy();
    expect(screen.getByText('A ZIP code is required.')).toBeTruthy();
    expect(screen.getByText('Form B101')).toBeTruthy();
    expect(screen.getByText(/prior cases but the form prints/)).toBeTruthy();
    // Nothing was produced, and the screen keeps saying so.
    expect(screen.getByText(/No packet has been assembled yet/)).toBeTruthy();
  });

  it('shows the job record’s own words when the pipeline fails', async () => {
    signedIn({
      status: () =>
        jsonResponse(
          200,
          job({
            status: 'failed',
            attempts: 1,
            failure: {
              category: 'case_changed',
              message:
                'The case changed while its packet was being assembled — run assembly again.',
            },
          }),
        ),
    });
    await screen.findByText(/No packet has been assembled yet/);

    await userEvent.press(
      screen.getByRole('button', { name: 'Assemble the Chapter 7 filing packet for this case' }),
    );

    expect(
      await screen.findByText(/The case changed while its packet was being assembled/),
    ).toBeTruthy();
    // The button re-enables for the retry the message asks for.
    expect(
      screen.getByRole('button', { name: 'Assemble the Chapter 7 filing packet for this case' }),
    ).toBeEnabled();
  });
});
