import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import { appEnvironment, environmentInfo } from '@/config/environment';

type RenderedNode = ReturnType<typeof screen.toJSON>;

/** Every `role` prop in a rendered tree, in document order. */
function rolesIn(node: RenderedNode): string[] {
  if (node === null || typeof node !== 'object') return [];
  const nodes = Array.isArray(node) ? node : [node];
  return nodes.flatMap((child) => {
    const role: unknown = child.props?.role;
    const here = typeof role === 'string' ? [role] : [];
    return [...here, ...(child.children ?? []).flatMap((c) => rolesIn(c as RenderedNode))];
  });
}

/**
 * The port of the Flutter `home_screen_test.dart`, plus the half of
 * `app_router_test.dart` that asserted `/` lands on the home shell.
 *
 * These render through the **real router** (`renderRouter` mounts `src/app`), the
 * way the Flutter tests pumped the real `InsolviaApp` with a real `GoRouter`, so
 * a route file that moved or stopped compiling fails here.
 */
describe('the home route', () => {
  it('renders the branded chrome and the shell content at /', async () => {
    renderRouter('src/app', { initialUrl: '/' });

    // The wordmark is the shell's identity — it comes from AppShell, so this
    // also asserts the screen is inside the frame.
    expect(await screen.findByText('Insolvia.')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Your case workspace' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Start a case' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open a case' })).toBeTruthy();
  });

  it('gives the page exactly one level-1 heading', () => {
    renderRouter('src/app', { initialUrl: '/' });

    const levelOne = screen
      .getAllByRole('heading')
      .filter((node) => node.props['aria-level'] === 1);

    expect(levelOne).toHaveLength(1);
  });

  it('reflects the resolved environment in the badge and the body', () => {
    renderRouter('src/app', { initialUrl: '/' });

    // Tests run without EXPO_PUBLIC_INSOLVIA_ENV, so this is the `local`
    // fallback arm — the same one an unconfigured build takes.
    const env = environmentInfo(appEnvironment);
    expect(env.name).toBe('local');

    expect(screen.getByText(env.label.toUpperCase())).toBeTruthy();
    expect(screen.getByLabelText(`${env.label} environment, ${env.host}`)).toBeTruthy();
    expect(screen.getByText(`Serving ${env.label.toLowerCase()} · ${env.host}`)).toBeTruthy();
  });

  it('keeps the decorative arrow glyph out of the primary CTA accessible name', () => {
    // Ported from the deleted components/button.test.tsx when Button moved to
    // @insolvia-ai/design-system: the package button has no `icon` prop, so the
    // glyph is now an `aria-hidden` child at the call site and `aria-label`
    // pins the name. If either half were dropped, a screen reader would
    // announce "Start a case right arrow".
    renderRouter('src/app', { initialUrl: '/' });

    // RN aliases `aria-label` onto `accessibilityLabel` on the host element,
    // which is the prop react-native-web emits as `aria-label` in the DOM.
    const cta = screen.getByRole('button', { name: 'Start a case' });
    expect(cta.props.accessibilityLabel).toBe('Start a case');

    // The glyph renders (visible when hidden elements are included) but is
    // excluded from the accessibility tree.
    expect(screen.queryByText('→')).toBeNull();
    expect(screen.getByText('→', { includeHiddenElements: true })).toBeTruthy();
  });

  it('answers the primary CTA with an announced notice', async () => {
    renderRouter('src/app', { initialUrl: '/' });

    expect(screen.queryByText('Case tools arrive in a later release.')).toBeNull();

    await userEvent.press(screen.getByRole('button', { name: 'Start a case' }));

    // The Flutter screen raised a SnackBar; this is the live region that
    // replaced it, so the message reaches a screen reader too.
    const notice = await screen.findByText('Case tools arrive in a later release.');
    expect(notice.props['aria-live']).toBe('polite');
  });

  it('frames every screen with the header, nav, main and footer landmarks', () => {
    renderRouter('src/app', { initialUrl: '/' });

    // react-native-web maps these four roles to <header>, <nav>, <main> and
    // <footer>. The built DOM is re-checked in the deploy verification; this is
    // the guard that stops the roles being dropped from AppShell in the first
    // place.
    //
    // Walked from the rendered tree rather than queried with `getByRole`:
    // Testing Library only matches *accessibility elements*, and a landmark
    // `View` is not one — making it one (`accessible`) would collapse the whole
    // header into a single element for a screen reader on native, which is a
    // worse app in exchange for a prettier test.
    expect(rolesIn(screen.toJSON())).toEqual(
      expect.arrayContaining(['banner', 'navigation', 'main', 'contentinfo']),
    );
  });
});
