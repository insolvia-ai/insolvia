import { existsSync } from 'node:fs';
import path from 'node:path';

import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

/** The port of `app_router_test.dart`'s Cognito-callback group. */
describe('the /auth/callback route', () => {
  it('is declared at exactly the path infra registers', () => {
    // A drift guard. `web_callback_urls` in infra/modules/auth/main.tf is
    // `"${o}/auth/callback"`, and under file-based routing the path IS the file's
    // location — so the assertion has to be about the file. Changing one side
    // without the other silently breaks the return leg of sign-in.
    const routeFile = path.join(__dirname, '..', 'app', 'auth', 'callback.tsx');
    expect(existsSync(routeFile)).toBe(true);
  });

  it('resolves to a screen, not the unmatched-route page', async () => {
    renderRouter('src/app', { initialUrl: '/auth/callback' });

    expect(await screen.findByRole('heading', { name: 'Sign-in is not enabled yet' })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Page not found' })).toBeNull();
    // Branded chrome, not a bare error page.
    expect(screen.getByText('Insolvia.')).toBeTruthy();
  });

  it('leads back to home', async () => {
    const router = renderRouter('src/app', { initialUrl: '/auth/callback' });

    await userEvent.press(screen.getByRole('button', { name: 'Continue to Insolvia' }));

    // `getPathname()` rather than the `toHavePathname` matcher: the matcher is
    // registered at runtime but expo-router ships no type declaration for it,
    // so it would not typecheck.
    expect(router.getPathname()).toBe('/');
    expect(await screen.findByRole('heading', { name: 'Your case workspace' })).toBeTruthy();
  });
});
