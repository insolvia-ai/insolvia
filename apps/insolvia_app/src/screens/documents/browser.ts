/**
 * Every browser global the documents screen touches, each behind a guard.
 *
 * **The same shape, and for the same reasons, as
 * [`src/session/browser.ts`](../../session/browser.ts)** — read that file's
 * header first; this one only records what is different here.
 *
 * React Native has no file-picker primitive and no "open this URL as a
 * download" primitive. Web is the only shipping target (decision D9,
 * [ADR 0004]), so both are written directly against the DOM rather than against
 * a cross-platform abstraction — and both are therefore absent in jest-expo's
 * native test environment and in any future native client, where a bare
 * `document.createElement(...)` throws a `ReferenceError` and takes the screen
 * down with it. Reading `document` through this file turns "there is no browser
 * here" into an outcome the caller already has to render: `'unavailable'`.
 *
 * Every read is **lazy** — at call time, never at module load — so a fake
 * installed by a test, or a polyfill that lands after this module is evaluated,
 * is still seen.
 *
 * **Adding a native client is what would justify a picker dependency**
 * (`expo-document-picker` and `expo-file-system`, which own the photo-library
 * and share-sheet handoff and the platform download directory). On web they
 * would buy nothing over the twenty lines below, which is the same trade
 * `session/browser.ts` makes against `expo-auth-session`.
 *
 * **Nothing here logs.** {@link openDownload} is handed a presigned URL, which
 * is a bearer capability: anything holding it can read the document. It goes
 * into an element and nowhere else.
 *
 * [ADR 0004]: ../../../../../docs/adr/0004-react-native-replaces-flutter.md
 */

/**
 * A file the user chose, flattened to the four things the upload needs.
 *
 * `bytes` is the `File` itself — a `Blob`, which is what
 * `InsolviaApiClient.uploadDocument` takes and what it reads `size` from. The
 * other three are copied out rather than read off `bytes` at use time because
 * this is the boundary: past it, nothing in the screen touches a DOM object.
 */
export interface PickedFile {
  /** The file's own name, exactly as the picker reported it. */
  readonly name: string;
  /** The browser's guess at the media type. May be `''` — the caller checks. */
  readonly contentType: string;
  /** The size in bytes, from the bytes themselves. */
  readonly size: number;
  /** The bytes, ready to hand to the API client. */
  readonly bytes: Blob;
}

/**
 * What a trip to the file picker ended in.
 *
 * `'dismissed'` and `'unavailable'` are kept apart deliberately. They arrive at
 * the same `await` and mean opposite things: the first is the user changing
 * their mind, which needs no message at all, and the second is a runtime that
 * cannot pick files, which needs one. Collapsing them into `null` is how a
 * cancelled picker ends up showing an error.
 */
export type PickOutcome =
  | { readonly kind: 'picked'; readonly file: PickedFile }
  | { readonly kind: 'dismissed' }
  | { readonly kind: 'unavailable' };

/** A `File`: a `Blob` that also knows what it is called. */
type FileLike = Blob & { readonly name: string };

/** The slice of `FileList` used — indexed access is `item()`, not `[0]`. */
interface FileListLike {
  readonly length: number;
  item(index: number): FileLike | null;
}

/** What both created elements have in common. */
interface TransientElement {
  readonly style: { display: string };
  click(): void;
  remove(): void;
}

interface FileInputLike extends TransientElement {
  type: string;
  accept: string;
  multiple: boolean;
  readonly files: FileListLike | null;
  addEventListener(type: string, listener: () => void, options?: { once?: boolean }): void;
}

interface AnchorLike extends TransientElement {
  href: string;
  target: string;
  rel: string;
  download: string;
}

/**
 * The slice of `document` this file uses.
 *
 * Declared structurally rather than as the DOM's `Document` for the reason
 * `StorageLike` is: the guard has to be a runtime check, and typing the global
 * as always-present would make every one of them look redundant to a reader and
 * to any future lint rule.
 */
interface DocumentLike {
  createElement(tagName: string): unknown;
  readonly body?: { appendChild(node: unknown): void } | null | undefined;
}

interface BrowserGlobals {
  document?: DocumentLike;
}

/** `document`, or `null` when there is no browser. Never throws. */
function browserDocument(): DocumentLike | null {
  try {
    const found = (globalThis as BrowserGlobals).document;
    if (found === undefined || found === null || typeof found.createElement !== 'function') {
      return null;
    }
    return found;
  } catch {
    return null;
  }
}

/**
 * Creates a detached element and puts it out of the way.
 *
 * Appended to `<body>` rather than left detached: Safari has historically
 * refused `click()` on an input that is not in the document, and an element
 * that is in the document has to be hidden or it reflows the page. It is
 * removed again the moment it has done its one job.
 */
function transientElement<T extends TransientElement>(
  doc: DocumentLike,
  tagName: string,
): T | null {
  try {
    const element = doc.createElement(tagName) as T;
    element.style.display = 'none';
    doc.body?.appendChild(element);
    return element;
  } catch {
    return null;
  }
}

/**
 * Opens the platform file picker and resolves with what came back.
 *
 * `accept` is the media-type allowlist, passed to the input's `accept`
 * attribute. It is a **hint to the picker, not a check** — a user can always
 * defeat it with "All files", and on some platforms it is ignored outright — so
 * the caller still validates what it gets, and the API validates again after
 * that.
 *
 * **The visible control must not be this input.** A bare `<input type="file">`
 * takes its accessible name from the browser ("Choose file"), which is a
 * WCAG 2.4.4 failure the moment a page has two of them and no way to tell them
 * apart. So the input is created here, off-screen, and driven by a real
 * `Button` whose name the screen writes — which is also why this is a function
 * rather than a component.
 *
 * **The promise is not guaranteed to settle.** The `cancel` event is what says
 * "the user closed the picker without choosing", and a browser old enough not
 * to fire it leaves this pending forever. That is survivable only because the
 * caller enters no busy state while waiting — pressing the button again simply
 * opens a new picker — and it is why this function must never be given a
 * spinner to own.
 */
export function pickFile(accept: readonly string[]): Promise<PickOutcome> {
  const doc = browserDocument();
  if (doc === null) {
    return Promise.resolve({ kind: 'unavailable' });
  }

  const input = transientElement<FileInputLike>(doc, 'input');
  if (input === null) {
    return Promise.resolve({ kind: 'unavailable' });
  }
  input.type = 'file';
  input.accept = accept.join(',');
  // One file per record. The API mints one capability per document, and a
  // multi-select would need a queue, per-file progress and per-file errors —
  // which is a feature, not a flag.
  input.multiple = false;

  return new Promise<PickOutcome>((resolve) => {
    const settle = (outcome: PickOutcome) => {
      input.remove();
      resolve(outcome);
    };
    input.addEventListener(
      'change',
      () => {
        const file = input.files?.item(0) ?? null;
        // A `change` with no file happens: some browsers fire it on cancel.
        settle(file === null ? { kind: 'dismissed' } : { kind: 'picked', file: describe(file) });
      },
      { once: true },
    );
    input.addEventListener('cancel', () => settle({ kind: 'dismissed' }), { once: true });
    input.click();
  });
}

function describe(file: FileLike): PickedFile {
  return { name: file.name, contentType: file.type, size: file.size, bytes: file };
}

/**
 * Hands a presigned document URL to the browser, in a new tab.
 *
 * Returns `false` when there is no browser to hand it to, so the caller can say
 * so rather than appearing to do nothing.
 *
 * **A new tab, not `location.assign`.** The API deliberately signs its download
 * URLs with no `Content-Disposition` — a file name in a query string is copied
 * into history, proxy logs and referrer chains, and the client already knows
 * the name (`document_blobs.py` owns that reasoning). Without it the browser may
 * render the file inline instead of saving it, and inline rendering in the
 * current tab would navigate the SPA away from the case. `download` is set
 * anyway: it is ignored cross-origin, which the bucket is, but it costs nothing
 * and is the right answer if these bytes are ever served same-origin.
 *
 * **This can be defeated by a pop-up blocker.** The click happens after an
 * `await` — the URL has to be minted first — so it is no longer inside the
 * user's gesture, and a strict blocker may swallow it silently. There is
 * nothing to detect: a blocked programmatic anchor click reports success. The
 * alternative, minting a URL for every row up front so the click is synchronous,
 * is worse in every way that matters — it spends a request per row on every
 * render and hands out a capability per document nobody asked for.
 */
export function openDownload(url: string, fileName: string): boolean {
  const doc = browserDocument();
  if (doc === null) {
    return false;
  }
  const anchor = transientElement<AnchorLike>(doc, 'a');
  if (anchor === null) {
    return false;
  }
  try {
    anchor.href = url;
    anchor.download = fileName;
    anchor.target = '_blank';
    // `noopener` is load-bearing, not boilerplate: the opened tab holds a
    // capability URL, and `window.opener` would let it reach back into the app.
    anchor.rel = 'noopener noreferrer';
    anchor.click();
    return true;
  } catch {
    return false;
  } finally {
    anchor.remove();
  }
}
