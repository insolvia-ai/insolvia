import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writePortalPendingAuthorization } from '@/session';
import {
  fakeJwt,
  installFakeBrowser,
  jsonResponse,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

/**
 * The portal questionnaire (ADR 0023 PR 4), through the real router: the
 * client opens it from the landing screen, sees one section at a time,
 * answers a question, and the answer is POSTed and shown as waiting for
 * review — and what they gave before is read back, so it resumes.
 */

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolvePortalAuthConfig: (): AuthConfig => ({
    domain: 'https://insolvia-test.auth.example.test',
    clientId: 'test-portal-client-id-000',
  }),
}));

const PORTAL_ME = {
  subject: '00000000-0000-4000-8000-0000000000a1',
  displayName: 'Pat Example',
  roles: ['debtor_1'],
  firm: { name: 'Example & Partners' },
  case: { chapter: 7, stage: 'intake' },
};

// Shaped as core/questions.py::portal_sections_json answers, trimmed.
const QUESTIONNAIRE = {
  sections: [
    {
      id: 'personal_information',
      title: 'Personal information',
      instructions: 'Tell us who you are and where you live.',
      questions: [
        {
          id: 'personal_information.legal_name',
          text: 'What is your full legal name?',
          repeats: false,
          perDebtor: true,
          inputs: [
            { key: 'given', label: 'First name', type: 'text', required: true },
            { key: 'surname', label: 'Last name', type: 'text', required: true },
          ],
        },
        {
          id: 'personal_information.phone',
          text: 'What is the best phone number to reach you?',
          repeats: false,
          perDebtor: true,
          inputs: [{ key: 'phone', label: 'Phone number', type: 'text', required: true }],
        },
      ],
    },
    {
      id: 'debts',
      title: 'Debts',
      instructions: 'Bring your statements.',
      questions: [
        {
          id: 'debts.creditor',
          text: 'Who do you owe money to?',
          repeats: true,
          perDebtor: false,
          inputs: [{ key: 'name', label: 'Name', type: 'text', required: true }],
        },
      ],
    },
  ],
};

const ANSWERED_PHONE = {
  id: '00000000-0000-4000-8000-0000000a45e0',
  questionId: 'personal_information.phone',
  sectionId: 'personal_information',
  filingRole: 'debtor_1',
  value: { phone: '555-0100' },
  status: 'accepted',
  createdAt: '2099-01-01T00:00:00.000000Z',
  updatedAt: '2099-01-01T00:00:00.000000Z',
};

type Seen = { url: string; method: string; body: unknown };

function portalFetch(seen: Seen[]) {
  let answers: unknown[] = [ANSWERED_PHONE];
  return jest.fn((url: string, init?: { method?: string; body?: string }) => {
    const method = init?.method ?? 'GET';
    if (url.includes('/oauth2/token')) {
      // A form-encoded body, not JSON — and not what this suite is about.
      return Promise.resolve(
        tokenEndpointResponse({ idToken: fakeJwt({ email: 'client@example.test' }) }),
      );
    }
    const body = init?.body === undefined ? undefined : (JSON.parse(init.body) as unknown);
    seen.push({ url, method, body });
    if (url.endsWith('/v1/portal/me')) return Promise.resolve(jsonResponse(200, PORTAL_ME));
    if (url.endsWith('/v1/portal/questionnaire')) {
      return Promise.resolve(jsonResponse(200, QUESTIONNAIRE));
    }
    if (url.endsWith('/v1/portal/answers') && method === 'POST') {
      const created = {
        ...ANSWERED_PHONE,
        id: '00000000-0000-4000-8000-0000000a45e1',
        questionId: 'personal_information.legal_name',
        value: (body as { value: unknown }).value,
        status: 'pending',
      };
      answers = [...answers, created];
      return Promise.resolve(jsonResponse(201, created));
    }
    if (url.endsWith('/v1/portal/answers')) {
      return Promise.resolve(jsonResponse(200, { answers }));
    }
    return Promise.reject(new Error(`unexpected request to ${url}`));
  });
}

describe('the portal questionnaire', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  beforeEach(() => {
    browser = installFakeBrowser();
    writePortalPendingAuthorization({
      state: 'test-state',
      codeVerifier: 'test-code-verifier',
      returnTo: null,
    });
  });

  afterEach(() => {
    browser.restore();
    globalThis.fetch = realFetch;
  });

  it('opens one section at a time and resumes from the answers already given', async () => {
    const seen: Seen[] = [];
    globalThis.fetch = portalFetch(seen) as unknown as typeof fetch;
    const router = renderRouter('src/app', {
      initialUrl: '/portal/auth/callback?code=test-code&state=test-state',
    });

    await userEvent.press(await screen.findByRole('button', { name: 'Answer your questionnaire' }));

    expect(await screen.findByRole('heading', { name: 'Personal information' })).toBeTruthy();
    expect(router.getPathname()).toBe('/portal/questionnaire');
    expect(screen.getByText('Section 1 of 2')).toBeTruthy();
    // The other section's questions wait for their own page.
    expect(screen.queryByRole('heading', { name: 'Who do you owe money to?' })).toBeNull();
    // What the client said before, and where it stands with the firm.
    expect(screen.getByText('555-0100')).toBeTruthy();
    expect(screen.getByText('Accepted')).toBeTruthy();
  });

  it('saves an answer as waiting for review', async () => {
    const seen: Seen[] = [];
    globalThis.fetch = portalFetch(seen) as unknown as typeof fetch;
    renderRouter('src/app', {
      initialUrl: '/portal/auth/callback?code=test-code&state=test-state',
    });
    await userEvent.press(await screen.findByRole('button', { name: 'Answer your questionnaire' }));
    await screen.findByRole('heading', { name: 'What is your full legal name?' });

    await userEvent.press(screen.getAllByRole('button', { name: 'Answer' })[0]!);
    await userEvent.type(screen.getByLabelText('First name'), 'Patricia');
    await userEvent.type(screen.getByLabelText('Last name'), 'Example');
    await userEvent.press(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Waiting for review')).toBeTruthy();
    const post = seen.find((r) => r.method === 'POST' && r.url.endsWith('/v1/portal/answers'));
    expect(post?.body).toEqual({
      questionId: 'personal_information.legal_name',
      value: { given: 'Patricia', surname: 'Example' },
    });
  });

  it('moves to the next section', async () => {
    const seen: Seen[] = [];
    globalThis.fetch = portalFetch(seen) as unknown as typeof fetch;
    renderRouter('src/app', {
      initialUrl: '/portal/auth/callback?code=test-code&state=test-state',
    });
    await userEvent.press(await screen.findByRole('button', { name: 'Answer your questionnaire' }));
    await screen.findByRole('heading', { name: 'Personal information' });

    await userEvent.press(screen.getByRole('button', { name: 'Next section' }));

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Who do you owe money to?' })).toBeTruthy();
    });
    expect(screen.getByText('Section 2 of 2')).toBeTruthy();
  });
});
