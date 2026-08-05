/**
 * A fake `document` for the two suites that need one — the seam's own tests and
 * the documents screen's route test.
 *
 * The same role, and the same reason for existing, as
 * [`src/session/testing.ts`](../../session/testing.ts): jest-expo runs the
 * **native** environment, where `document` is absent entirely, which is a
 * faithful stand-in for a non-web runtime and exactly why
 * [`browser.ts`](browser.ts) reads it lazily and behind a guard. A test that
 * wants the web path has to ask for it, here.
 *
 * **Not a `*.test.ts` file**, so Jest does not collect it as a suite, and
 * nothing shipping imports it.
 *
 * It implements only what `browser.ts` touches. In particular the input's
 * `click()` dispatches its listener **synchronously**, which is what real
 * browsers do for a programmatic click and what lets a test press a button and
 * `await` the outcome without a timer.
 */

/** What a test offers the picker: a file, or `null` for "the user cancelled". */
export interface OfferedFile {
  readonly name: string;
  readonly type: string;
  readonly size: number;
}

/** One `openDownload` call, recorded rather than performed. */
export interface RecordedDownload {
  readonly url: string;
  readonly fileName: string;
}

/** What {@link installFakeFileBrowser} hands back. */
export interface FakeFileBrowser {
  /**
   * What the **next** pick resolves with. `null` dismisses it, which is what a
   * browser reports when the user closes the picker without choosing.
   */
  offer(file: OfferedFile | null): void;
  /** Every download the screen opened, in order. */
  readonly downloads: readonly RecordedDownload[];
  /** Restores whatever was on `globalThis` before. */
  restore(): void;
}

interface MutableGlobals {
  document?: unknown;
}

interface FakeState {
  offered: OfferedFile | null;
  downloads: RecordedDownload[];
}

function fakeInput(state: FakeState): unknown {
  const listeners = new Map<string, () => void>();
  let files: unknown = null;

  return {
    type: '',
    accept: '',
    multiple: true,
    style: { display: '' },
    get files() {
      return files;
    },
    addEventListener(type: string, listener: () => void) {
      listeners.set(type, listener);
    },
    click() {
      const offered = state.offered;
      if (offered === null) {
        listeners.get('cancel')?.();
        return;
      }
      // A stand-in for the `File` the API client would be handed. Nothing under
      // test reads its bytes — `uploadDocument` reads `size` and passes the
      // object straight to `fetch`, which every one of these tests stubs.
      const file = { name: offered.name, type: offered.type, size: offered.size };
      files = { length: 1, item: (index: number) => (index === 0 ? file : null) };
      listeners.get('change')?.();
    },
    remove() {
      listeners.clear();
    },
  };
}

function fakeAnchor(state: FakeState): unknown {
  const anchor = {
    href: '',
    target: '',
    rel: '',
    download: '',
    style: { display: '' },
    click() {
      state.downloads.push({ url: anchor.href, fileName: anchor.download });
    },
    remove() {},
  };
  return anchor;
}

/**
 * Installs a fake `document` on `globalThis` for the life of one test.
 *
 * Deliberately narrow: `createElement` answers for exactly the two tags
 * `browser.ts` creates, and anything else throws rather than returning a
 * plausible object — a silent default is how a test ends up asserting against
 * an element nothing under test asked for.
 */
export function installFakeFileBrowser(): FakeFileBrowser {
  const globals = globalThis as MutableGlobals;
  const previous = globals.document;
  const state: FakeState = { offered: null, downloads: [] };

  globals.document = {
    createElement(tagName: string): unknown {
      if (tagName === 'input') return fakeInput(state);
      if (tagName === 'a') return fakeAnchor(state);
      throw new Error(`the documents screen created an unexpected <${tagName}>`);
    },
    body: {
      appendChild() {},
    },
  };

  return {
    offer: (file) => {
      state.offered = file;
    },
    downloads: state.downloads,
    restore: () => {
      globals.document = previous;
    },
  };
}
