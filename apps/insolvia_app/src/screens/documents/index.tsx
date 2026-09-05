import {
  ApiValidationException,
  DOCUMENT_CONTENT_TYPES,
  DOCUMENT_KINDS,
  MAX_DOCUMENT_BYTE_SIZE,
  isDocumentContentType,
  isDocumentKind,
  isUploadIncomplete,
} from '@insolvia-ai/api-client';
import type { Document, DocumentContentType, DocumentKind } from '@insolvia-ai/api-client';
import { AlertDialog, Button, Field, Progress, Select } from '@insolvia-ai/design-system';
import type { SelectOption, SelectValue } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { openDownload, pickFile } from '@/screens/documents/browser';
import type { PickedFile } from '@/screens/documents/browser';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * Human labels for the API's `kind` claim. Keyed off `DOCUMENT_KINDS` so the
 * picker's options and the type cannot drift — a kind added to the API is a
 * compile error here until it is named.
 */
const KIND_LABELS: Record<DocumentKind, string> = {
  credit_report: 'Credit report',
  pay_stub: 'Pay stub',
  bank_statement: 'Bank statement',
  tax_return: 'Tax return',
  identification: 'Identification',
  court_notice: 'Court notice',
  other: 'Something else',
};

const KIND_OPTIONS: readonly SelectOption[] = DOCUMENT_KINDS.map((kind) => ({
  value: kind,
  label: KIND_LABELS[kind],
}));

/** The allowlist, in the words a person uses for it. */
const ACCEPTED_TYPES = 'PDF, JPEG, PNG, HEIC and TIFF';

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly documents: readonly Document[] }
  | { readonly kind: 'error'; readonly message: string };

/**
 * A case's documents: upload, list, download, delete (issue 8.6).
 *
 * **The pending state is the reason this screen is shaped the way it is.** An
 * upload is a three-step transaction — record, PUT, confirm — and only the
 * confirm keeps the bytes: until it runs, the object still carries the
 * `upload=unconfirmed` tag the bucket's lifecycle rule reaps 24 hours later.
 * `uploadDocument` runs all three and, when one fails, deliberately leaves the
 * `'pending'` record in place rather than tidying it away. So a failed upload
 * here must *reload the list*, not swallow the error: the row the user sees
 * afterwards is the truthful record of a file they tried to add, and the screen
 * says in plain language what will happen to it. Hiding pending rows, or
 * treating a failed upload as "nothing happened", would turn a recoverable
 * state into a file that silently disappears overnight.
 *
 * Three smaller rules the API's doc comments impose, each easy to get wrong:
 *
 * - **A download URL is minted on press, never on render.** It is short-lived
 *   and it is a bearer capability; one per row per render would both waste
 *   calls and hand out access nobody asked for.
 * - **`storageRef` is never rendered** — the API does not even send it, and the
 *   object layout is not something a client may come to depend on.
 * - **A `'pending'` document has no bytes**, so it gets no download control at
 *   all; its URL would mint fine and 404 when followed.
 *
 * Client-side validation mirrors `core/documents.py` for the two checks worth a
 * saved round trip — the media type and the size — and stops there. The server
 * stays the authority: its per-field messages are rendered verbatim and win
 * over ours wherever the two disagree.
 */
export function Documents({ caseId }: { readonly caseId: string }) {
  const theme = useTheme();
  const { call } = useApi();

  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [kind, setKind] = useState<SelectValue>(null);
  const [picked, setPicked] = useState<PickedFile | null>(null);
  const [uploading, setUploading] = useState(false);
  const [serverErrors, setServerErrors] = useState<Readonly<Record<string, string>>>({});
  const [actionError, setActionError] = useState<string | null>(null);
  const [activity, setActivity] = useState('');
  const [confirming, setConfirming] = useState<Document | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listDocuments(caseId));
      if (result.ok) {
        setList({ kind: 'ready', documents: result.value });
      }
      // !ok means the session ended and useApi already navigated; leaving the
      // screen in `loading` is correct — it is about to unmount.
    } catch {
      setList({ kind: 'error', message: 'Could not load this case’s documents.' });
    }
  }, [call, caseId]);

  useEffect(() => {
    void load();
  }, [load]);

  /**
   * What the polite region says. **Derived, not stored**, so the list arriving
   * is announced without a second effect racing the state that produced it: the
   * last thing the user did wins, and the case's own summary is what is left
   * when they have not done anything yet.
   */
  const statusText =
    activity !== ''
      ? activity
      : list.kind === 'loading'
        ? 'Loading this case’s documents…'
        : list.kind === 'ready'
          ? summarise(list.documents)
          : '';

  /**
   * The client-side checks, and the media type narrowed for the upload call.
   * Derived at render rather than stored: a second copy of "what is wrong with
   * the chosen file" is a copy that can disagree with the file itself.
   */
  const check = checkFile(picked);
  // The server's message wins wherever both have something to say about a
  // field — it is the authority, and it saw the request we actually sent.
  const fieldErrors: Readonly<Record<string, string>> = { ...check.problems, ...serverErrors };
  const fileRejected = Object.keys(check.problems).length > 0;

  const choose = async () => {
    setServerErrors({});
    setActionError(null);
    // No busy state around this await, deliberately: a browser that never fires
    // `cancel` leaves it pending forever, and a spinner here would be a screen
    // the user cannot get out of. See `pickFile`.
    const outcome = await pickFile(DOCUMENT_CONTENT_TYPES);
    if (outcome.kind === 'unavailable') {
      setActionError('Choosing a file needs a web browser. Open Insolvia in one to upload.');
      return;
    }
    if (outcome.kind === 'dismissed') {
      return;
    }
    setPicked(outcome.file);
    setActivity(`Chose ${outcome.file.name}.`);
  };

  const upload = async () => {
    const file = picked;
    if (file === null || uploading) {
      return;
    }
    if (kind === null || !isDocumentKind(kind)) {
      setServerErrors({ kind: 'Choose what this document is.' });
      return;
    }
    const contentType = check.contentType;
    if (contentType === null || fileRejected) {
      return;
    }

    setServerErrors({});
    setActionError(null);
    setUploading(true);
    setActivity(`Uploading ${file.name}…`);
    try {
      const result = await call((client) =>
        client.uploadDocument(caseId, {
          file: file.bytes,
          fileName: file.name,
          kind,
          contentType,
        }),
      );
      if (result.ok) {
        setPicked(null);
        setActivity(`Uploaded ${file.name}.`);
      }
    } catch (cause) {
      setActivity('');
      if (cause instanceof ApiValidationException) {
        // ADR 0001: the server is the source of truth for validation, so its
        // per-field messages are rendered as-is rather than restated here.
        setServerErrors(cause.fields);
      } else if (isUploadIncomplete(cause)) {
        // A 409 from the confirm step: the record exists, the bytes never
        // arrived. Retrying the confirm would fail identically forever.
        setActionError(
          `${file.name} did not finish uploading — none of its contents reached us. ` +
            'It is listed below as unfinished; choose the file and upload it again.',
        );
      } else {
        setActionError(
          `Could not upload ${file.name}. ` +
            'If it is listed below as unfinished, choose the file and upload it again.',
        );
      }
    } finally {
      setUploading(false);
      // ALWAYS, including after a failure. `uploadDocument` leaves the pending
      // record behind on purpose; this is what puts it on screen instead of
      // letting the attempt vanish.
      await load();
    }
  };

  const download = async (entry: Document) => {
    setActionError(null);
    setBusyId(entry.id);
    try {
      // Minted HERE, at the moment of use — never on render. The URL lives for
      // minutes and is a bearer capability; one per row per render would spend
      // a request on every document nobody asked to open.
      const result = await call((client) => client.getDocumentUrl(caseId, entry.id));
      if (result.ok) {
        if (openDownload(result.value.url, entry.fileName)) {
          setActivity(`Opened ${entry.fileName}.`);
        } else {
          setActionError(`Could not open ${entry.fileName} — this needs a web browser.`);
        }
      }
    } catch {
      setActionError(`Could not prepare ${entry.fileName} for download. Please try again.`);
    } finally {
      // Compared, not cleared: a slow response for one row must not re-enable
      // the controls of whichever row the user started next.
      setBusyId((current) => (current === entry.id ? null : current));
    }
  };

  const confirmDelete = async () => {
    const target = confirming;
    if (target === null) {
      return;
    }
    setConfirming(null);
    setActionError(null);
    setBusyId(target.id);
    setActivity(`Deleting ${target.fileName}…`);
    try {
      const result = await call((client) => client.deleteDocument(caseId, target.id));
      if (result.ok) {
        setActivity(`Deleted ${target.fileName}.`);
        await load();
      }
    } catch {
      setActivity('');
      setActionError(`Could not delete ${target.fileName}. Please try again.`);
    } finally {
      setBusyId((current) => (current === target.id ? null : current));
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const danger = { color: theme.colors.danger, fontFamily: theme.typography.body };

  return (
    <>
      <Heading level={1}>Case documents</Heading>

      {/*
        ONE always-present live region per urgency, whose TEXT changes. A region
        mounted at the same moment as its message announces nothing at all —
        the assistive technology has to be observing the node before the text
        lands in it. Both therefore render unconditionally, empty when there is
        nothing to say.

        The assertive one is derived rather than stored so a failed load and a
        failed action share it without either having to remember to clear the
        other; whatever the user just did wins.
      */}
      <Text aria-live="polite" style={[styles.status, muted]}>
        {statusText}
      </Text>
      <Text aria-live="assertive" style={[styles.status, danger]}>
        {actionError ?? (list.kind === 'error' ? list.message : '')}
      </Text>

      <Heading level={2}>Add a document</Heading>

      <View style={styles.form}>
        <Field.Root name="kind" invalid={Boolean(fieldErrors.kind)}>
          <Field.Label>Document type</Field.Label>
          <Select
            options={KIND_OPTIONS}
            value={kind}
            onValueChange={setKind}
            placeholder="Choose a type"
          />
          <Field.Description>
            What you are telling us this is. Nobody opens the file to check, so say “Something else”
            if you are unsure.
          </Field.Description>
          {fieldErrors.kind ? <Field.Error match>{fieldErrors.kind}</Field.Error> : null}
        </Field.Root>

        <View style={styles.chooser}>
          {/*
            The visible control is a real Button — a Pressable, so
            react-native-web emits a <button> that the keyboard can reach — and
            the file input itself is created off-screen by `pickFile`. A bare
            <input type="file"> would take its accessible name from the browser
            ("Choose file"), which says nothing about which file or what for.
            size="lg" (48dp) clears the 44dp WCAG 2.5.5 target-size floor.
          */}
          <Button
            size="lg"
            intent="secondary"
            onPress={choose}
            aria-label="Choose a file to upload to this case"
          >
            Choose a file
          </Button>
          <Text style={[styles.body, picked === null ? muted : { color: theme.colors.ink }]}>
            {picked === null
              ? 'No file chosen yet.'
              : `${picked.name} · ${formatSize(picked.size)}`}
          </Text>
        </View>

        {/* The client's own checks and the server's, in one place and one
            order, so a disagreement shows the server's wording. */}
        {(['contentType', 'byteSize', 'fileName'] as const).map((field) => {
          const message = fieldErrors[field];
          return message === undefined ? null : (
            <Text key={field} style={[styles.error, danger]}>
              {message}
            </Text>
          );
        })}

        <Text style={[styles.caption, muted]}>
          {ACCEPTED_TYPES}, up to {MAX_DOCUMENT_BYTE_SIZE / (1024 * 1024)} MB.
        </Text>

        <View style={styles.actions}>
          <Button
            size="lg"
            onPress={upload}
            disabled={picked === null || uploading || fileRejected}
            aria-label={picked === null ? 'Upload the chosen file' : `Upload ${picked.name}`}
          >
            {uploading ? 'Uploading…' : 'Upload'}
          </Button>
        </View>

        {uploading && picked !== null ? (
          // Indeterminate on purpose: the upload is one `fetch` PUT and the
          // platform reports no byte progress for a request body, so a
          // percentage here would be invented. `Progress.Root` carries
          // role="progressbar", which is what makes the aria-label legitimate.
          <Progress.Root value={null} aria-label={`Uploading ${picked.name}`}>
            <Progress.Track>
              <Progress.Indicator />
            </Progress.Track>
          </Progress.Root>
        ) : null}
      </View>

      <Heading level={2}>Documents in this case</Heading>
      {list.kind === 'ready' ? (
        <DocumentList
          documents={list.documents}
          busyId={busyId}
          onDownload={download}
          onDelete={setConfirming}
        />
      ) : (
        // Deliberately NOT the same sentence as the live region above. The
        // region is where the message is announced; repeating it verbatim in
        // place would put the identical string on screen twice, and the
        // in-place copy is the one with room for the recovery step.
        <Text style={[styles.body, muted]}>
          {list.kind === 'loading' ? 'Loading…' : `${list.message} Reload the page to try again.`}
        </Text>
      )}

      {/*
        ONE dialog for the whole list, driven by which document is being
        confirmed — not one per row. Deleting is not reachable by a single
        press, and AlertDialog is the part of the design system that guarantees
        it: role="alertdialog", no tap-outside dismissal, an explicit choice.
      */}
      <AlertDialog.Root
        open={confirming !== null}
        onOpenChange={(next) => {
          if (!next) setConfirming(null);
        }}
      >
        <AlertDialog.Popup>
          <AlertDialog.Title>
            Delete {confirming === null ? 'this document' : confirming.fileName}?
          </AlertDialog.Title>
          <AlertDialog.Description>
            It stops being part of this case straight away and nobody can open it again. Insolvia
            keeps the file recoverable internally for 30 days for legal-retention reasons, and it
            cannot be restored from here.
          </AlertDialog.Description>
          <View style={styles.dialogActions}>
            <Button size="lg" onPress={confirmDelete}>
              Delete document
            </Button>
            <AlertDialog.Close>Keep it</AlertDialog.Close>
          </View>
        </AlertDialog.Popup>
      </AlertDialog.Root>
    </>
  );
}

function DocumentList({
  documents,
  busyId,
  onDownload,
  onDelete,
}: {
  readonly documents: readonly Document[];
  readonly busyId: string | null;
  readonly onDownload: (entry: Document) => void;
  readonly onDelete: (entry: Document) => void;
}) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  if (documents.length === 0) {
    return (
      <Text style={[styles.body, muted]}>
        No documents yet. Choose a file above to add the first one.
      </Text>
    );
  }

  return (
    // `role`, not `accessibilityRole`: RN's AccessibilityRole union has no
    // list/listitem, but the ARIA `role` prop passes straight through
    // react-native-web to a real <ul>/<li> pair.
    <View role="list" style={styles.list}>
      {documents.map((item) => (
        <DocumentRow
          key={item.id}
          entry={item}
          busy={busyId === item.id}
          onDownload={onDownload}
          onDelete={onDelete}
        />
      ))}
    </View>
  );
}

function DocumentRow({
  entry,
  busy,
  onDownload,
  onDelete,
}: {
  readonly entry: Document;
  readonly busy: boolean;
  readonly onDownload: (entry: Document) => void;
  readonly onDelete: (entry: Document) => void;
}) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const pending = entry.status === 'pending';

  return (
    <View role="listitem" style={styles.listItem}>
      <Text style={[styles.fileName, { color: theme.colors.ink }]}>{entry.fileName}</Text>
      {/*
        Never `storageRef` — the API does not send it, and the object layout is
        not a thing a client may come to depend on. `byteSize` means different
        things either side of the confirm step, so it is worded differently:
        what S3 counted, versus what this browser said it would send.
      */}
      <Text style={[styles.meta, muted]}>
        {kindLabel(entry.kind)} · {formatSize(entry.byteSize)}
        {pending ? ' expected' : ''} · added {entry.uploadedAt.slice(0, 10)}
      </Text>

      {pending ? (
        <Text style={[styles.meta, { color: theme.colors.danger }]}>
          Upload didn’t finish. None of this file’s contents reached us, so there is nothing to
          open. Insolvia deletes an unfinished upload 24 hours after it starts — choose the file
          above and upload it again to keep it.
        </Text>
      ) : (
        <Text style={[styles.meta, muted]}>Uploaded and stored.</Text>
      )}

      <View style={styles.rowActions}>
        {/*
          Each control's accessible name carries the file name, because a list
          of rows each offering "Download" tells a screen-reader user which
          verb but never which file (WCAG 2.4.4). The visible word is still the
          first word of the name, which is what WCAG 2.5.3 asks for.

          A pending document gets no download control at all: its URL would
          mint happily and 404 the moment it was followed.
        */}
        {pending ? null : (
          <Button
            size="lg"
            intent="secondary"
            disabled={busy}
            onPress={() => onDownload(entry)}
            aria-label={`Download ${entry.fileName}`}
          >
            Download
          </Button>
        )}
        <Button
          size="lg"
          intent="ghost"
          disabled={busy}
          onPress={() => onDelete(entry)}
          aria-label={`Delete ${entry.fileName}`}
        >
          Delete
        </Button>
      </View>
    </View>
  );
}

/** What the client can rule out before spending a request. */
interface FileCheck {
  /** The media type, narrowed — `null` when it is not one the API accepts. */
  readonly contentType: DocumentContentType | null;
  /** Per-field messages, keyed exactly as the API keys its own. */
  readonly problems: Readonly<Record<string, string>>;
}

/**
 * Mirrors `core/documents.py` for the two checks a client can make honestly:
 * the media type and the size. Both are also bound into the presigned
 * signature, so getting them wrong costs a request *and* returns an S3 error
 * with nothing in it to explain why.
 *
 * The file **name** is deliberately not checked here. It comes from the
 * picker rather than from a text field, so there is no keystroke to correct,
 * and the API's rule (no path separators, no invisible or direction-changing
 * characters) is a security check whose exact wording belongs to the server.
 */
function checkFile(file: PickedFile | null): FileCheck {
  if (file === null) {
    return { contentType: null, problems: {} };
  }
  const problems: Record<string, string> = {};

  const contentType = isDocumentContentType(file.contentType) ? file.contentType : null;
  if (contentType === null) {
    problems.contentType =
      file.contentType === ''
        ? `Your browser could not tell what kind of file that is. Insolvia accepts ${ACCEPTED_TYPES}.`
        : `Insolvia accepts ${ACCEPTED_TYPES}. That file is a ${file.contentType}.`;
  }
  if (file.size === 0) {
    problems.byteSize = 'That file is empty.';
  } else if (file.size > MAX_DOCUMENT_BYTE_SIZE) {
    problems.byteSize = `A document must be ${MAX_DOCUMENT_BYTE_SIZE / (1024 * 1024)} MB or smaller. That one is ${formatSize(file.size)}.`;
  }

  return { contentType, problems };
}

/**
 * The one-line answer to "what is in this case?", for the polite region. It
 * names the unfinished uploads because that is the number a user has to act on.
 */
function summarise(documents: readonly Document[]): string {
  if (documents.length === 0) {
    return 'No documents in this case yet.';
  }
  const total = `${documents.length} document${documents.length === 1 ? '' : 's'}`;
  const unfinished = documents.filter((entry) => entry.status === 'pending').length;
  return unfinished === 0 ? `${total}.` : `${total}, ${unfinished} of them unfinished.`;
}

/**
 * The API sends `kind` as a plain string on purpose — a value it starts
 * accepting tomorrow must not break decoding of a whole page — so this narrows
 * before looking it up and falls back to the wire spelling rather than to
 * "Unknown".
 */
function kindLabel(kind: string): string {
  return isDocumentKind(kind) ? KIND_LABELS[kind] : kind.replace(/_/g, ' ');
}

/** A size a person can read. Approximate above a kilobyte, which is fine. */
function formatSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} bytes`;
  }
  const kilobytes = bytes / 1024;
  if (kilobytes < 1024) {
    return `${Math.round(kilobytes)} KB`;
  }
  return `${(kilobytes / 1024).toFixed(1)} MB`;
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    marginTop: spacing.xs,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  caption: {
    fontSize: fontSizes.caption,
  },
  chooser: {
    alignItems: 'center',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
  },
  dialogActions: {
    alignItems: 'center',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
  },
  error: {
    fontSize: fontSizes.label,
  },
  fileName: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  form: {
    gap: spacing.md,
    marginBottom: spacing.lg,
    marginTop: spacing.sm,
  },
  list: {
    gap: spacing.lg,
  },
  listItem: {
    gap: spacing.xs,
  },
  meta: {
    fontSize: fontSizes.caption,
    lineHeight: fontSizes.caption * 1.5,
  },
  rowActions: {
    flexDirection: 'row',
    gap: spacing.sm,
    marginTop: spacing.xs,
  },
  status: {
    fontSize: fontSizes.label,
  },
});
