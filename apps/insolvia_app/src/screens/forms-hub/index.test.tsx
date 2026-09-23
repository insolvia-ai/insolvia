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

function respond(stub: ApiStub, url: string, init?: RequestInit): Response | Promise<Response> {
  const method = init?.method ?? 'GET';
  if (url.includes('/forms/') && url.endsWith('/preview')) {
    return (
      stub.preview ??
      (() =>
        jsonResponse(200, {
          url: 'https://bucket.example.test/read-here',
          method: 'GET',
          expiresAt: '2026-09-03T10:05:00.123Z',
          problems: [],
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

  it('says why a form could not render when the preview itself is blocked', async () => {
    signedIn({
      list: () => jsonResponse(200, { forms: [formRow()] }),
      preview: () =>
        jsonResponse(200, {
          problems: [{ source: 'debtors', message: 'The case has no Debtor 1 record.' }],
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
});
