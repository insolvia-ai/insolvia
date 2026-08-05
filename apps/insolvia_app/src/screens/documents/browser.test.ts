import { openDownload, pickFile } from '@/screens/documents/browser';
import { installFakeFileBrowser } from '@/screens/documents/testing';
import type { FakeFileBrowser } from '@/screens/documents/testing';

/**
 * The web seam for file picking and downloading.
 *
 * The interesting cases are the ones the screen cannot see: what happens with
 * no browser at all (jest-expo's native environment, and a native client if one
 * ever ships), and the difference between "the user cancelled" and "this
 * runtime cannot pick files" — which arrive at the same `await` and mean
 * opposite things.
 */
describe('the documents web seam', () => {
  describe('with no browser', () => {
    // No fake installed: this is jest-expo's native environment as it comes,
    // which is a faithful stand-in for a non-web runtime.
    it('reports that picking a file is unavailable rather than throwing', async () => {
      await expect(pickFile(['application/pdf'])).resolves.toEqual({ kind: 'unavailable' });
    });

    it('reports that a download could not be opened', () => {
      expect(openDownload('https://example.test/blob', 'statement.pdf')).toBe(false);
    });
  });

  describe('in a browser', () => {
    let browser: FakeFileBrowser;

    beforeEach(() => {
      browser = installFakeFileBrowser();
    });

    afterEach(() => {
      browser.restore();
    });

    it('resolves with the chosen file, flattened off the DOM object', async () => {
      browser.offer({ name: 'june-statement.pdf', type: 'application/pdf', size: 2048 });

      const outcome = await pickFile(['application/pdf']);

      expect(outcome.kind).toBe('picked');
      if (outcome.kind !== 'picked') throw new Error('expected a picked file');
      expect(outcome.file.name).toBe('june-statement.pdf');
      expect(outcome.file.contentType).toBe('application/pdf');
      expect(outcome.file.size).toBe(2048);
      // The bytes are carried through untouched — this is what the API client
      // is handed, and it is what `uploadDocument` reads `size` from.
      expect(outcome.file.bytes).toBeDefined();
    });

    it('distinguishes a dismissed picker from an unavailable one', async () => {
      browser.offer(null);

      await expect(pickFile(['application/pdf'])).resolves.toEqual({ kind: 'dismissed' });
    });

    it('opens a download in a new tab, under the name the record carries', () => {
      expect(openDownload('https://example.test/presigned', 'june-statement.pdf')).toBe(true);

      expect(browser.downloads).toEqual([
        { url: 'https://example.test/presigned', fileName: 'june-statement.pdf' },
      ]);
    });
  });
});
