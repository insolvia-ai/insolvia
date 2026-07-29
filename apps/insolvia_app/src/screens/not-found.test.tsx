import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

/**
 * The port of `app_router_test.dart`'s unknown-path group.
 *
 * This route is what answers a mistyped URL in production: CloudFront rewrites
 * 403/404 to `/index.html` with HTTP 200, so the edge never 404s and the router
 * is the only thing that can say "not found".
 */
describe('an unknown path', () => {
  it('renders branded not-found chrome that echoes the path', async () => {
    renderRouter('src/app', { initialUrl: '/nope' });

    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeTruthy();
    expect(screen.getByText('Nothing lives at /nope.')).toBeTruthy();
    expect(screen.getByText('Insolvia.')).toBeTruthy();
  });

  it('leads back to home', async () => {
    const router = renderRouter('src/app', { initialUrl: '/nope' });

    await userEvent.press(screen.getByRole('button', { name: 'Back to home' }));

    expect(router.getPathname()).toBe('/');
    expect(await screen.findByRole('heading', { name: 'Your case workspace' })).toBeTruthy();
  });

  it('does not swallow the routes that do exist', async () => {
    renderRouter('src/app', { initialUrl: '/auth/callback' });

    expect(await screen.findByRole('heading', { name: 'Sign-in is not enabled yet' })).toBeTruthy();
  });
});
