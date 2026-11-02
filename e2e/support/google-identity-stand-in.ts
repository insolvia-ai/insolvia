import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import type { Page } from '@playwright/test';

import { loadSigningKey } from '../../contract/src/support/jwt';

/**
 * Google's sign-in button, stood in for — inside THIS BROWSER only.
 *
 * The admin portal signs staff in with Google Identity Services: it loads
 * `https://accounts.google.com/gsi/client`, renders Google's button, and hands
 * the ID token the button returns to the admin service, which verifies it
 * against Google's keys. An offline run has no Google account to click
 * through, so this module answers that one script URL with a stand-in
 * `google.accounts.id` whose button asks the test process for a token.
 *
 * WHERE THE STAND-IN LIVES, AND WHERE IT MUST NOT. The token is signed with
 * the contract stack's per-run key (`CONTRACT_SIGNING_KEY`, minted by
 * `contract/scripts/dev-test.sh` and deleted when it exits), and the offline
 * admin host trusts that key only because the same script starts it with
 * `GOOGLE_JWKS_URL` pointing at the aux server. That is configuration of a
 * throwaway local composition — no host's code knows a stand-in exists, the
 * portal's code is untouched (it simply receives a different script), and no
 * deployed config gains a second issuer. Against staging or production this
 * token would be refused, because the key that signs it exists nowhere else.
 *
 * The claims are the ones Google puts in a Workspace ID token, built the way
 * the contract suite builds them (`contract/src/support/tokens.ts`): `iss`
 * accounts.google.com, `aud` the stack's Google client id, `hd` its Workspace
 * domain, a verified `.test` address (RFC 2606 — nobody's mailbox).
 */

const STACK_FILE = fileURLToPath(new URL('../../contract/stack/stack.json', import.meta.url));

export const IDENTITY_SCRIPT_URL = 'https://accounts.google.com/gsi/client';

/** The stand-in button's accessible name — the spec's one selector into it. */
export const STAND_IN_BUTTON = 'Sign in with Google (offline stand-in)';

export const STAFF_EMAIL = 'e2e-staff@insolvia.test';

interface Stack {
  readonly clientIds: { readonly google: string };
  readonly workspaceDomain: string;
}

function mintGoogleIdToken(): string {
  const keyPath = process.env.CONTRACT_SIGNING_KEY;
  if (!keyPath) {
    throw new Error(
      'CONTRACT_SIGNING_KEY is not set — run this suite through e2e/scripts/admin-offline-test.sh',
    );
  }
  const stack = JSON.parse(readFileSync(STACK_FILE, 'utf8')) as Stack;
  const issued = Math.floor(Date.now() / 1000);
  return loadSigningKey(keyPath).sign({
    iss: 'https://accounts.google.com',
    aud: stack.clientIds.google,
    azp: stack.clientIds.google,
    sub: '100000000000000000584',
    hd: stack.workspaceDomain,
    email: STAFF_EMAIL,
    email_verified: true,
    iat: issued,
    exp: issued + 3600,
  });
}

/** The script the portal receives instead of Google's. */
const STAND_IN_SCRIPT = `
(() => {
  let callback = null;
  window.google = { accounts: { id: {
    initialize(config) { callback = config.callback; },
    renderButton(parent) {
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = ${JSON.stringify(STAND_IN_BUTTON)};
      button.addEventListener('click', async () => {
        callback({ credential: await window.__insolviaMintGoogleIdToken() });
      });
      parent.appendChild(button);
    },
  } } };
})();
`;

/**
 * Installs the stand-in on this page, and points the portal's local API base
 * (`http://127.0.0.1:8090`, `apps/insolvia_admin/src/config/environment.ts`)
 * at the offline stack's admin host instead — the developer's own dev stack
 * may be on :8090, and this run must not touch it.
 */
export async function useOfflineGoogleIdentity(page: Page, adminUrl: string): Promise<void> {
  await page.exposeFunction('__insolviaMintGoogleIdToken', mintGoogleIdToken);
  await page.route(IDENTITY_SCRIPT_URL, (route) =>
    route.fulfill({ contentType: 'text/javascript', body: STAND_IN_SCRIPT }),
  );
  await page.route('http://127.0.0.1:8090/**', async (route) => {
    const url = route.request().url().replace('http://127.0.0.1:8090', adminUrl);
    await route.fulfill({ response: await route.fetch({ url }) });
  });
}
