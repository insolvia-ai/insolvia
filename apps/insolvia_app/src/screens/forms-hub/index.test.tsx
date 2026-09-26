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

/** One `_form_summary_json` row, complete and metric-free by default. */
function formRow(overrides: Record<string, unknown> = {}) {
  return {
    series: 'form/b101',
    form: 'b101',
    title: 'Voluntary Petition for Individuals Filing for Bankruptcy',
    officialNumber: 'B 101',
    problems: [],
    openTaskCount: 0,
    ...overrides,
  };
}

interface ApiStub {
  /** `GET /v1/cases/<id>/forms`. */
  list?: Answer;
  /** `GET /v1/cases/<id>/forms/<form>/preview`. */
  preview?: Answer;
}

type Answer = () => Response | Promise<Response>;

/** The plain filing set — `output_options_json(OutputOptions())`'s shape. */
const DEFAULT_OPTIONS = {
  draftWatermark: false,
  printDate: false,
  signaturePages: 'all',
  signElectronically: false,
};

function respond(stub: ApiStub, url: string, init?: RequestInit): Response | Promise<Response> {
  const method = init?.method ?? 'GET';
  // A query string (issue 13.11's output options) may follow `/preview`.
  if (url.includes('/forms/') && url.includes('/preview')) {
    return (
      stub.preview ??
      (() =>
        jsonResponse(200, {
          url: 'https://bucket.example.test/read-here',
          method: 'GET',
          expiresAt: '2026-09-03T10:05:00.123Z',
          problems: [],
          options: DEFAULT_OPTIONS,
        }))
    )();
  }
  if (url.endsWith('/forms')) {
    return (stub.list ?? (() => jsonResponse(200, { forms: [] })))();
  }
  // The case layout's own two reads, exactly as the packet screen's test
  // stubs them (@/session/testing's `caseShellRoutes`).
  if (url.endsWith('/debtors')) {
    return jsonResponse(200, { debtors: [] });
  }
  if (url.endsWith(CASE_ID)) {
    return jsonResponse(200, caseBody(CASE_ID));
  }
  throw new Error(`unexpected ${method} ${url}`);
}

/**
 * `/cases/<id>/forms` — the forms hub (issue 13.2 / #343).
 *
 * Rendered through the real router, signed in first, the packet screen's own
 * shape. What is asserted is what a preparer would lose if it broke: that a
 * blocked row shows its own reasons, that "Preview" opens the PDF under the
 * form's own file name, and that "Open" sends a preparer to the screen that
 * actually collects the form's records.
 */
describe('the forms hub screen', () => {
  let browser: FakeBrowser;
  let files: FakeFileBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(stub: ApiStub) {
    const fetchMock = jest.fn(async (url: string, init?: RequestInit) =>
      url.includes('/oauth2/token') ? tokenEndpointResponse() : respond(stub, url, init),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/forms` });
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

  it('lists a clean form with its official number and a Complete badge', async () => {
    signedIn({ list: () => jsonResponse(200, { forms: [formRow()] }) });

    expect(
      await screen.findByText('B 101 — Voluntary Petition for Individuals Filing for Bankruptcy'),
    ).toBeTruthy();
    expect(screen.getByText('Complete')).toBeTruthy();
  });

  it('shows an item count metric', async () => {
    signedIn({
      list: () =>
        jsonResponse(200, {
          forms: [
            formRow({
              series: 'form/b106ab',
              form: 'b106ab',
              title: 'Schedule A/B: Property',
              officialNumber: 'B 106A/B',
              metric: { kind: 'count', value: '3' },
            }),
          ],
        }),
    });

    expect(await screen.findByText('3 items')).toBeTruthy();
  });

  it('shows a dollar total metric', async () => {
    signedIn({
      list: () =>
        jsonResponse(200, {
          forms: [
            formRow({
              series: 'form/b106i',
              form: 'b106i',
              title: 'Schedule I: Your Income',
              officialNumber: 'B 106I',
              metric: { kind: 'total', value: '2500.00' },
            }),
          ],
        }),
    });

    expect(await screen.findByText('$2,500.00')).toBeTruthy();
  });

  it('shows a blocked row’s own problems and an issue count', async () => {
    signedIn({
      list: () =>
        jsonResponse(200, {
          forms: [
            formRow({
              problems: [{ source: 'debtors', message: 'The case has no Debtor 1 record.' }],
            }),
          ],
        }),
    });

    expect(await screen.findByText('1 issue')).toBeTruthy();
    expect(screen.getByText('Debtors:')).toBeTruthy();
    expect(screen.getByText(/The case has no Debtor 1 record\./)).toBeTruthy();
  });

  it('previews a clean form: opens the PDF under the form’s own file name', async () => {
    signedIn({
      list: () =>
        jsonResponse(200, {
          forms: [
            formRow({
              series: 'form/b106g',
              form: 'b106g',
              title: 'Schedule G: Executory Contracts and Unexpired Leases',
              officialNumber: 'B 106G',
            }),
          ],
        }),
    });
    await screen.findByText(/Schedule G/);

    await userEvent.press(
      screen.getByRole('button', {
        name: 'Preview Schedule G: Executory Contracts and Unexpired Leases as a PDF',
      }),
    );

    expect(
      await screen.findByText('Opened Schedule G: Executory Contracts and Unexpired Leases.'),
    ).toBeTruthy();
    expect(files.downloads).toEqual([
      { url: 'https://bucket.example.test/read-here', fileName: 'b106g.pdf' },
    ]);
  });

  // ── Output options (issue 13.11) ──────────────────────────────

  it('sends the selected output options as the preview’s query string', async () => {
    const fetchMock = signedIn({
      list: () =>
        jsonResponse(200, {
          forms: [
            formRow({
              series: 'form/b106g',
              form: 'b106g',
              title: 'Schedule G: Executory Contracts and Unexpired Leases',
              officialNumber: 'B 106G',
            }),
          ],
        }),
    });
    await screen.findByText(/Schedule G/);

    await userEvent.press(screen.getByRole('checkbox', { name: '"Draft" watermark' }));
    await userEvent.press(
      screen.getByRole('button', {
        name: 'Preview Schedule G: Executory Contracts and Unexpired Leases as a PDF',
      }),
    );
    await screen.findByText('Opened Schedule G: Executory Contracts and Unexpired Leases.');

    const previewCall = fetchMock.mock.calls.find(([url]) => String(url).includes('/preview'));
    expect(String(previewCall?.[0])).toContain('draftWatermark=true');
  });

  it('says why a form could not render when the preview itself is blocked', async () => {
    signedIn({
      list: () => jsonResponse(200, { forms: [formRow()] }),
      preview: () =>
        jsonResponse(200, {
          problems: [{ source: 'debtors', message: 'The case has no Debtor 1 record.' }],
          options: DEFAULT_OPTIONS,
        }),
    });
    await screen.findByText(/Voluntary Petition/);

    await userEvent.press(
      screen.getByRole('button', {
        name: 'Preview Voluntary Petition for Individuals Filing for Bankruptcy as a PDF',
      }),
    );

    expect(
      await screen.findByText(/is not ready to preview: The case has no Debtor 1 record\./),
    ).toBeTruthy();
    expect(files.downloads).toEqual([]);
  });

  it('opens the intake screen for a schedule the intake editor collects', async () => {
    signedIn({
      list: () =>
        jsonResponse(200, {
          forms: [
            formRow({
              series: 'form/b106ab',
              form: 'b106ab',
              title: 'Schedule A/B: Property',
              officialNumber: 'B 106A/B',
            }),
          ],
        }),
    });
    await screen.findByText(/Schedule A\/B/);

    await userEvent.press(
      screen.getByRole('button', { name: 'Open the screen that collects Schedule A/B: Property' }),
    );

    expect(await screen.findByRole('heading', { name: 'Intake' })).toBeTruthy();
  });

  it('a summary form with no data-entry screen offers no Open button', async () => {
    signedIn({
      list: () =>
        jsonResponse(200, {
          forms: [
            formRow({
              series: 'form/b106sum',
              form: 'b106sum',
              title: 'Summary of Your Assets and Liabilities',
              officialNumber: 'B 106Sum',
            }),
          ],
        }),
    });
    await screen.findByText(/Summary of Your Assets/);

    expect(
      screen.queryByRole('button', {
        name: 'Open the screen that collects Summary of Your Assets and Liabilities',
      }),
    ).toBeNull();
  });

  it('says so plainly when the case has no forms yet', async () => {
    signedIn({});

    await screen.findByRole('heading', { name: 'Forms' });
    expect(screen.queryAllByRole('listitem')).toHaveLength(0);
  });

  // ── Task count (issue #356 / 14.4) ────────────────────────────

  it('shows a row’s open task count', async () => {
    signedIn({
      list: () =>
        jsonResponse(200, {
          forms: [
            formRow({
              series: 'form/b106d',
              form: 'b106d',
              title: 'Schedule D: Creditors Who Have Claims Secured by Property',
              officialNumber: 'B 106D',
              openTaskCount: 2,
            }),
          ],
        }),
    });

    expect(await screen.findByText('2 tasks')).toBeTruthy();
  });

  it('omits the task badge when a form has no open tasks', async () => {
    signedIn({ list: () => jsonResponse(200, { forms: [formRow()] }) });

    await screen.findByText(/Voluntary Petition/);
    expect(screen.queryByText(/task/)).toBeNull();
  });
});

// ── Notes on a form row (issue 14.5 / #357) ──────────────────────────
//
// A SEPARATE `signedIn`, method-aware, rather than an extension of `respond`
// above: every other test in this file reads `/forms` once and asserts on
// what came back, while this block needs `/v1/me` (for the `notes` feature
// gate) and a stateful `/notes` list a POST can grow — reusing `ApiStub`
// would widen every other test's setup for a concern only these touch.

const ALICE = '00000000-0000-4000-8000-00000000a11c';

function meBody(notesLevel: string = 'add_edit') {
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
      permissions: {
        cases: 'add_edit',
        intake: 'add_edit',
        documents: 'add_edit',
        extraction_review: 'hidden',
        creditor_library: 'hidden',
        notes: notesLevel,
        firm_administration: 'hidden',
      },
    },
  };
}

function noteRecord(overrides: Record<string, unknown> = {}) {
  return {
    id: 'note-1',
    case_id: CASE_ID,
    created_at: '2026-09-01T10:00:00.000000Z',
    updated_at: '2026-09-01T10:00:00.000000Z',
    author_subject: ALICE,
    author_name: 'Alice Attorney',
    text: 'A note on this form.',
    form_series: 'form/b106g',
    ...overrides,
  };
}

describe('notes on a form row (issue 14.5 / #357)', () => {
  let browser: FakeBrowser;
  let files: FakeFileBrowser;
  const realFetch = globalThis.fetch;

  const FORM = formRow({
    series: 'form/b106g',
    form: 'b106g',
    title: 'Schedule G: Executory Contracts and Unexpired Leases',
    officialNumber: 'B 106G',
  });

  /**
   * `notes` starts as the given seed and grows with every POST, so a test
   * can add one through the composer and then see the row's count and the
   * panel's own list both reflect it — the same round trip a preparer drives.
   */
  function signedInWithNotes(seed: readonly Record<string, unknown>[], notesLevel = 'add_edit') {
    const notes = [...seed];
    let nextId = notes.length + 1;
    const fetchMock = jest.fn(async (url: string, init?: RequestInit) => {
      if (url.includes('/oauth2/token')) return tokenEndpointResponse();
      if (url.includes('/v1/me')) return jsonResponse(200, meBody(notesLevel));
      if (url.endsWith('/forms')) return jsonResponse(200, { forms: [FORM] });
      if (url.endsWith('/debtors')) return jsonResponse(200, { debtors: [] });
      if (url.includes('/notes')) {
        const method = init?.method ?? 'GET';
        if (method === 'POST') {
          const body = JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>;
          const created = noteRecord({
            id: `note-${String(nextId)}`,
            text: body.text,
            form_series: body.form_series,
          });
          nextId += 1;
          notes.push(created);
          return jsonResponse(201, created);
        }
        return jsonResponse(200, { notes: [...notes].reverse() });
      }
      if (url.endsWith(CASE_ID)) return jsonResponse(200, caseBody(CASE_ID));
      throw new Error(`unexpected ${init?.method ?? 'GET'} ${url}`);
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/forms` });
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

  it('shows the row’s note count from the case’s whole note list', async () => {
    signedInWithNotes([noteRecord(), noteRecord({ id: 'note-2', text: 'A second note.' })]);

    expect(
      await screen.findByRole('button', {
        name: 'Show notes for Schedule G: Executory Contracts and Unexpired Leases',
      }),
    ).toBeTruthy();
    expect(screen.getByText('Notes (2)')).toBeTruthy();
  });

  it('expanding the row shows only that form’s notes, newest first', async () => {
    signedInWithNotes([
      noteRecord({ id: 'note-1', text: 'Older note.' }),
      noteRecord({ id: 'note-2', text: 'Newer note.' }),
    ]);
    await screen.findByText('Notes (2)');

    await userEvent.press(
      screen.getByRole('button', {
        name: 'Show notes for Schedule G: Executory Contracts and Unexpired Leases',
      }),
    );

    await screen.findByText('Newer note.');
    expect(screen.getByText('Older note.')).toBeTruthy();
    // `role="listitem"` has no React Native accessibilityRole counterpart, so
    // RNTL's role queries cannot see it here (only `queryAllByRole`, proving
    // absence, works elsewhere in this file) — the rendered tree order is
    // what's actually load-bearing for "newest first".
    const tree = JSON.stringify(screen.toJSON());
    expect(tree.indexOf('Newer note.')).toBeLessThan(tree.indexOf('Older note.'));
  });

  it('adding a note through the row’s composer anchors it to that form', async () => {
    const fetchMock = signedInWithNotes([]);
    await screen.findByText('Notes (0)');
    await userEvent.press(
      screen.getByRole('button', {
        name: 'Show notes for Schedule G: Executory Contracts and Unexpired Leases',
      }),
    );
    const field = await screen.findByLabelText('Add a note');
    await userEvent.type(field, 'Check the lease term.');
    await userEvent.press(screen.getByRole('button', { name: 'Add note' }));

    await screen.findByText('Check the lease term.');
    expect(screen.getByText('Notes (1)')).toBeTruthy();

    const posted = fetchMock.mock.calls.find(
      ([url, init]) =>
        String(url).endsWith('/notes') && (init as RequestInit | undefined)?.method === 'POST',
    );
    expect(posted).toBeDefined();
    const body = JSON.parse(String((posted?.[1] as RequestInit).body)) as Record<string, unknown>;
    expect(body).toEqual({ text: 'Check the lease term.', form_series: 'form/b106g' });
  });

  it('hides the composer without the notes feature', async () => {
    signedInWithNotes([noteRecord()], 'view_only');
    await screen.findByText('Notes (1)');

    await userEvent.press(
      screen.getByRole('button', {
        name: 'Show notes for Schedule G: Executory Contracts and Unexpired Leases',
      }),
    );

    await screen.findByText('A note on this form.');
    expect(screen.queryByLabelText('Add a note')).toBeNull();
  });
});
