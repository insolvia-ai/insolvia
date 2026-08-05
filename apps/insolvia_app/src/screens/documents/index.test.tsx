import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';
import { Text } from 'react-native';

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
const DOC_ID = '00000000-0000-4000-8000-0000000000d1';
const OTHER_DOC_ID = '00000000-0000-4000-8000-0000000000d2';
const PRESIGNED_PUT = 'https://bucket.example.test/put-here';

/** A document as `document_json` renders it. `storageRef` is not among the keys. */
function document(overrides: Record<string, unknown> = {}) {
  return {
    id: DOC_ID,
    caseId: CASE_ID,
    kind: 'bank_statement',
    fileName: 'june-statement.pdf',
    contentType: 'application/pdf',
    byteSize: 2048,
    uploadedAt: '2026-08-04T10:00:00.000000Z',
    status: 'stored',
    ...overrides,
  };
}

/** What the API answers, dispatched on method AND path — several of these
 *  endpoints differ only by verb under the same `/documents` prefix. */
interface ApiStub {
  /** `GET /v1/cases/<id>/documents`, called again after every mutation. */
  list?: Answer;
  /** `POST .../documents` — mints the record and the presigned PUT. */
  create?: Answer;
  /** The presigned PUT itself, at whatever URL `create` handed out. */
  put?: Answer;
  /** `POST .../documents/<id>/complete`. */
  complete?: Answer;
  /** `GET .../documents/<id>/url`. */
  url?: Answer;
  /** `DELETE .../documents/<id>`. */
  remove?: Answer;
}

/** Async so a test can hold one endpoint open and look at the screen mid-flight. */
type Answer = () => Response | Promise<Response>;

function respond(stub: ApiStub, url: string, init?: RequestInit): Response | Promise<Response> {
  const method = init?.method ?? 'GET';
  if (url.startsWith(PRESIGNED_PUT)) {
    return (stub.put ?? (() => jsonResponse(200, {})))();
  }
  if (url.includes('/complete')) {
    return (stub.complete ?? (() => jsonResponse(200, { document: document() })))();
  }
  if (url.endsWith('/url')) {
    return (
      stub.url ??
      (() =>
        jsonResponse(200, {
          url: 'https://bucket.example.test/read-here',
          method: 'GET',
          expiresAt: '2026-08-04T10:05:00.000000Z',
        }))
    )();
  }
  if (url.endsWith('/documents')) {
    if (method === 'POST') {
      return (
        stub.create ??
        (() =>
          jsonResponse(201, {
            document: document({ status: 'pending' }),
            upload: { url: PRESIGNED_PUT, method: 'PUT', headers: {}, expiresAt: 'later' },
          }))
      )();
    }
    return (stub.list ?? (() => jsonResponse(200, { documents: [] })))();
  }
  if (method === 'DELETE') {
    return (stub.remove ?? (() => jsonResponse(204, {})))();
  }
  throw new Error(`unexpected ${method} ${url}`);
}

/**
 * `/cases/<id>/documents` — the app half of issue 8.6.
 *
 * Rendered through the **real router**, so a route file that moved or stopped
 * compiling fails here. The route is protected, so every test signs in first,
 * exactly as the cases screen's suite does.
 *
 * What is asserted is what a user would lose if it broke: that a failed upload
 * leaves the pending record visible instead of swallowing it, that the pending
 * state says in words what will happen to the file, that deleting asks first,
 * and that every control in a list of near-identical rows has an accessible
 * name that says which file it acts on.
 */
describe('the case documents screen', () => {
  let browser: FakeBrowser;
  let files: FakeFileBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(stub: ApiStub) {
    const fetchMock = jest.fn(async (url: string, init?: RequestInit) =>
      url.includes('/oauth2/token') ? tokenEndpointResponse() : respond(stub, url, init),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/documents` });
    return fetchMock;
  }

  /** Chooses `Bank statement` and a file, leaving the form ready to upload. */
  async function chooseAFile(name = 'june-statement.pdf', type = 'application/pdf', size = 2048) {
    files.offer({ name, type, size });
    await userEvent.press(screen.getByRole('combobox'));
    await userEvent.press(screen.getByRole('option', { name: 'Bank statement' }));
    await userEvent.press(screen.getByRole('button', { name: /^Choose a file/ }));
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

  it('lists a document by name, kind, size and date', async () => {
    signedIn({ list: () => jsonResponse(200, { documents: [document()] }) });

    expect(await screen.findByText('june-statement.pdf')).toBeTruthy();
    expect(screen.getByText(/Bank statement · 2 KB · added 2026-08-04/)).toBeTruthy();
  });

  it('says so plainly when there are none', async () => {
    signedIn({ list: () => jsonResponse(200, { documents: [] }) });

    expect(await screen.findByText(/No documents yet/)).toBeTruthy();
  });

  it('names the file chooser by what it is for, not "Choose file"', async () => {
    // A bare <input type="file"> takes its name from the browser, and "Choose
    // file" repeated on a page says nothing about which file or what for
    // (WCAG 2.4.4). The visible word stays the start of the name (WCAG 2.5.3).
    signedIn({ list: () => jsonResponse(200, { documents: [] }) });
    await screen.findByText(/No documents yet/);

    const chooser = screen.getByRole('button', { name: 'Choose a file to upload to this case' });
    expect(chooser).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Choose file' })).toBeNull();
  });

  it('marks an unfinished upload as unfinished, and says what becomes of it', async () => {
    // The whole reason the API lists pending rows. A record whose bytes never
    // landed is reaped 24 hours later, and a user who cannot tell it apart from
    // a stored one finds out when the file is gone.
    signedIn({
      list: () =>
        jsonResponse(200, { documents: [document({ status: 'pending', byteSize: 4096 })] }),
    });

    expect(await screen.findByText(/Upload didn’t finish/)).toBeTruthy();
    expect(screen.getByText(/24 hours/)).toBeTruthy();
    // Distinguishable from a stored row by more than colour: the size is
    // labelled as a claim, not a measurement.
    expect(screen.getByText(/4 KB expected/)).toBeTruthy();
    // No download control: the URL would mint happily and 404 when followed.
    expect(screen.queryByRole('button', { name: /^Download/ })).toBeNull();
  });

  it('leaves a visible pending row when the upload fails, instead of losing it', async () => {
    // `uploadDocument` deliberately does NOT clean up on a partial failure —
    // the record is the case's memory of a file the user tried to add. The
    // screen's job is to reload and show it rather than report "nothing
    // happened".
    let listed = 0;
    signedIn({
      list: () => {
        listed += 1;
        return jsonResponse(200, {
          documents: listed === 1 ? [] : [document({ status: 'pending' })],
        });
      },
      put: () => jsonResponse(403, { error: 'SignatureDoesNotMatch' }),
    });
    await screen.findByText(/No documents yet/);

    await chooseAFile();
    await userEvent.press(screen.getByRole('button', { name: 'Upload june-statement.pdf' }));

    expect(await screen.findByText(/Upload didn’t finish/)).toBeTruthy();
    expect(screen.getByText(/choose the file and upload it again/i)).toBeTruthy();
  });

  it('shows an indeterminate progress bar for as long as the upload is in flight', async () => {
    // The upload is one `fetch` PUT and the platform reports no byte progress
    // for a request body, so there is no percentage to show — but "something is
    // happening, and it is this file" still has to be on screen and announced.
    let release = () => {};
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    let listed = 0;
    signedIn({
      list: () => {
        listed += 1;
        return jsonResponse(200, { documents: listed === 1 ? [] : [document()] });
      },
      create: async () => {
        await held;
        return jsonResponse(201, {
          document: document({ status: 'pending' }),
          upload: { url: PRESIGNED_PUT, method: 'PUT', headers: {}, expiresAt: 'later' },
        });
      },
    });
    await screen.findByText(/No documents yet/);

    await chooseAFile();
    await userEvent.press(screen.getByRole('button', { name: 'Upload june-statement.pdf' }));

    // Reached by prop rather than by role+name, for two reasons that are about
    // the test environment and not about the markup: `*ByRole` only sees
    // elements React Native has marked `accessible`, which a plain View is not,
    // and RNTL's name matcher reads the `accessibilityLabel` that Pressable
    // normalises `aria-label` into — a View keeps the prop as written. What
    // turns both into a real `<div role="progressbar" aria-label="…">` is
    // react-native-web's own mapping, re-verified against the built export
    // rather than mocked here (see the app's `//jest` note).
    const bars = screen.UNSAFE_getAllByProps({ accessibilityRole: 'progressbar' });
    expect(bars.length).toBeGreaterThan(0);
    expect(bars[0]?.props['aria-label']).toBe('Uploading june-statement.pdf');
    // ...and the polite region says the same thing in words.
    expect(screen.getByText('Uploading june-statement.pdf…')).toBeTruthy();

    release();

    await waitFor(() =>
      expect(screen.UNSAFE_queryAllByProps({ accessibilityRole: 'progressbar' })).toHaveLength(0),
    );
    expect(screen.getByText('Uploaded june-statement.pdf.')).toBeTruthy();
  });

  it('confirms a stored upload and shows it as stored', async () => {
    let listed = 0;
    signedIn({
      list: () => {
        listed += 1;
        return jsonResponse(200, { documents: listed === 1 ? [] : [document()] });
      },
    });
    await screen.findByText(/No documents yet/);

    await chooseAFile();
    await userEvent.press(screen.getByRole('button', { name: 'Upload june-statement.pdf' }));

    expect(await screen.findByText('Uploaded and stored.')).toBeTruthy();
    expect(screen.queryByText(/Upload didn’t finish/)).toBeNull();
  });

  it('refuses a file the API would refuse, without spending a request', async () => {
    const fetchMock = signedIn({ list: () => jsonResponse(200, { documents: [] }) });
    await screen.findByText(/No documents yet/);

    await chooseAFile('holiday.mov', 'video/quicktime', 1024);

    expect(await screen.findByText(/Insolvia accepts PDF, JPEG, PNG, HEIC and TIFF/)).toBeTruthy();
    // The point of the check: no record was created, so no presigned capability
    // was minted and no round trip was spent on a file the API would refuse.
    expect(
      fetchMock.mock.calls.filter(
        ([url, init]) => url.endsWith('/documents') && init?.method === 'POST',
      ),
    ).toHaveLength(0);
  });

  it('refuses a file over the size limit', async () => {
    signedIn({ list: () => jsonResponse(200, { documents: [] }) });
    await screen.findByText(/No documents yet/);

    await chooseAFile('scan.pdf', 'application/pdf', 51 * 1024 * 1024);

    expect(await screen.findByText(/must be 50 MB or smaller/i)).toBeTruthy();
  });

  it("renders the server's per-field message rather than restating the rule", async () => {
    signedIn({
      list: () => jsonResponse(200, { documents: [] }),
      create: () =>
        jsonResponse(400, {
          error: 'ValidationError',
          fields: { fileName: 'Must be a file name.' },
        }),
    });
    await screen.findByText(/No documents yet/);

    await chooseAFile();
    await userEvent.press(screen.getByRole('button', { name: 'Upload june-statement.pdf' }));

    expect(await screen.findByText('Must be a file name.')).toBeTruthy();
  });

  it('gives every row control an accessible name that says which file it acts on', async () => {
    // Two rows offering "Download" and "Delete" are four controls with two
    // names between them. Asserting the NAME rather than the visible text is
    // the only thing that catches it.
    signedIn({
      list: () =>
        jsonResponse(200, {
          documents: [
            document(),
            document({ id: OTHER_DOC_ID, fileName: 'w2-2025.pdf', kind: 'tax_return' }),
          ],
        }),
    });
    await screen.findByText('w2-2025.pdf');

    expect(screen.getByRole('button', { name: 'Download june-statement.pdf' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Delete june-statement.pdf' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Download w2-2025.pdf' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Delete w2-2025.pdf' })).toBeTruthy();
  });

  it('mints a download URL on press and not on render', async () => {
    // The URL is short-lived and is itself the credential. One per row per
    // render would spend a request on every document nobody asked to open.
    const fetchMock = signedIn({ list: () => jsonResponse(200, { documents: [document()] }) });
    await screen.findByText('june-statement.pdf');

    const urlCalls = () => fetchMock.mock.calls.filter(([url]) => url.endsWith('/url'));
    expect(urlCalls()).toHaveLength(0);

    await userEvent.press(screen.getByRole('button', { name: 'Download june-statement.pdf' }));

    await waitFor(() => expect(urlCalls()).toHaveLength(1));
    expect(files.downloads).toEqual([
      { url: 'https://bucket.example.test/read-here', fileName: 'june-statement.pdf' },
    ]);
  });

  it('asks before deleting, and does not delete until the user says so', async () => {
    const fetchMock = signedIn({ list: () => jsonResponse(200, { documents: [document()] }) });
    await screen.findByText('june-statement.pdf');

    const deleteCalls = () => fetchMock.mock.calls.filter(([, init]) => init?.method === 'DELETE');

    await userEvent.press(screen.getByRole('button', { name: 'Delete june-statement.pdf' }));

    // The press opened a dialog and sent nothing.
    expect(await screen.findByText('Delete june-statement.pdf?')).toBeTruthy();
    expect(deleteCalls()).toHaveLength(0);

    await userEvent.press(screen.getByRole('button', { name: 'Delete document' }));

    await waitFor(() => expect(deleteCalls()).toHaveLength(1));
  });

  it('lets the user back out of a delete', async () => {
    const fetchMock = signedIn({ list: () => jsonResponse(200, { documents: [document()] }) });
    await screen.findByText('june-statement.pdf');

    await userEvent.press(screen.getByRole('button', { name: 'Delete june-statement.pdf' }));
    await screen.findByText('Delete june-statement.pdf?');
    await userEvent.press(screen.getByRole('button', { name: 'Keep it' }));

    await waitFor(() => expect(screen.queryByText('Delete june-statement.pdf?')).toBeNull());
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'DELETE')).toHaveLength(0);
  });

  it('reports a failed load without pretending the case has no documents', async () => {
    // "No documents yet" on a failed request would tell someone their
    // paperwork is gone.
    signedIn({ list: () => jsonResponse(500, { error: 'InternalError' }) });

    expect(await screen.findByText('Could not load this case’s documents.')).toBeTruthy();
    expect(screen.queryByText(/No documents yet/)).toBeNull();
  });

  it('keeps one always-present live region of each urgency', async () => {
    // A region that mounts at the same moment as its message announces
    // nothing: the assistive technology has to be watching the node before the
    // text lands in it. Both therefore exist from the first render.
    signedIn({ list: () => jsonResponse(200, { documents: [document()] }) });
    await screen.findByText('june-statement.pdf');

    const live = screen
      .UNSAFE_getAllByType(Text)
      .filter((node) => node.props['aria-live'] !== undefined);
    expect(live.map((node) => node.props['aria-live'])).toEqual(['polite', 'assertive']);
  });

  it('gives the page exactly one level-1 heading', async () => {
    signedIn({ list: () => jsonResponse(200, { documents: [] }) });
    await screen.findByText(/No documents yet/);

    const levelOnes = screen
      .getAllByRole('heading')
      .filter((node) => node.props['aria-level'] === 1);
    expect(levelOnes).toHaveLength(1);
  });
});
