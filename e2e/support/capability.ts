import type { Page } from '@playwright/test';

/**
 * Follows a presigned URL the app opens in a new tab, and returns what it
 * serves — without the URL ever leaving this function.
 *
 * The app hands a download or a PDF preview to the browser as a link to
 * another origin (S3 deployed; the compose stack's blob twin locally), opened
 * with `target=_blank`. Chromium then renders it or turns it into a download,
 * and either way the new tab's own URL is `about:blank` when the popup event
 * fires — so the URL is taken from the tab's NAVIGATION REQUEST, the first
 * one that leaves the app's origin, and read once more through the test's own
 * request context.
 *
 * A presigned URL is a bearer capability: it is not returned, logged or put
 * in a message. The caller gets a status and the bytes.
 */
export async function followCapability(
  page: Page,
  open: () => Promise<void>,
): Promise<{ status: number; body: Buffer }> {
  const appOrigin = new URL(page.url()).origin;
  const navigation = page.context().waitForEvent('request', {
    predicate: (request) =>
      request.isNavigationRequest() && new URL(request.url()).origin !== appOrigin,
    timeout: 60_000,
  });
  await open();
  const request = await navigation;
  const response = await page.request.get(request.url());
  const result = { status: response.status(), body: await response.body() };
  for (const other of page.context().pages()) {
    if (other !== page) await other.close();
  }
  return result;
}
