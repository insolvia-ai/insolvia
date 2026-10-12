import { ApiValidationException, DOCUMENT_KINDS, isDocumentKind } from '@insolvia-ai/api-client';
import type { ChecklistItem, DocumentKind, FirmDocumentChecklist } from '@insolvia-ai/api-client';
import { AlertDialog, Button, Field, Input, Select, Textarea } from '@insolvia-ai/design-system';
import type { SelectOption } from '@insolvia-ai/design-system';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { KIND_LABELS, kindLabel } from '@/screens/documents';
import { fontSizes, spacing, useTheme } from '@/theme';

type Notice = { readonly tone: 'error' | 'saved'; readonly message: string };

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly checklist: FirmDocumentChecklist }
  | { readonly kind: 'error' };

interface Entry {
  /** A key for the row while it is edited; never sent. */
  readonly key: string;
  readonly title: string;
  readonly kind: DocumentKind;
  readonly description: string;
}

const KIND_OPTIONS: readonly SelectOption[] = DOCUMENT_KINDS.map((kind) => ({
  value: kind,
  label: KIND_LABELS[kind],
}));

let nextKey = 0;
function entryFrom(item: ChecklistItem): Entry {
  nextKey += 1;
  return {
    key: `entry-${String(nextKey)}`,
    title: item.title,
    kind: isDocumentKind(item.kind) ? item.kind : 'other',
    description: item.description ?? '',
  };
}

/**
 * The documents the firm asks every client for (ADR 0023 PR 5 / #364) — on
 * the firm screen, for administrators. The shipped default is in force
 * until the firm saves its own list; "Reset to the default" deletes the
 * firm's list, so a later improvement to the default reaches it.
 *
 * Editing the list changes what a case gets when staff press "Request the
 * checklist" on it from then on. It never rewrites what a case has already
 * asked for — a request is a copy, not a reference.
 */
export function DocumentChecklistSettings({
  editable,
  onNotice,
}: {
  editable: boolean;
  onNotice: (notice: Notice | null) => void;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [entries, setEntries] = useState<readonly Entry[]>([]);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [confirmReset, setConfirmReset] = useState(false);

  const settle = (checklist: FirmDocumentChecklist) => {
    setState({ kind: 'ready', checklist });
    setEntries(checklist.items.map(entryFrom));
  };

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const result = await call((client) => client.getFirmDocumentChecklist());
        if (!cancelled && result.ok) settle(result.value);
      } catch {
        if (!cancelled) setState({ kind: 'error' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call]);

  const save = async () => {
    setBusy(true);
    setFieldErrors({});
    onNotice(null);
    try {
      const result = await call((client) =>
        client.saveFirmDocumentChecklist({
          items: entries.map((entry) => {
            const description = entry.description.trim();
            return {
              title: entry.title,
              kind: entry.kind,
              ...(description === '' ? {} : { description }),
            };
          }),
        }),
      );
      if (result.ok) {
        settle(result.value);
        onNotice({ tone: 'saved', message: 'The document checklist is saved.' });
      }
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setFieldErrors(cause.fields);
      } else {
        onNotice({ tone: 'error', message: 'Could not save the document checklist. Try again.' });
      }
    } finally {
      setBusy(false);
    }
  };

  const reset = async () => {
    setBusy(true);
    setFieldErrors({});
    onNotice(null);
    try {
      const result = await call((client) => client.resetFirmDocumentChecklist());
      if (result.ok) {
        settle(result.value);
        onNotice({ tone: 'saved', message: 'The document checklist is back to the default.' });
      }
    } catch {
      onNotice({ tone: 'error', message: 'Could not reset the document checklist. Try again.' });
    } finally {
      setBusy(false);
    }
  };

  const update = (key: string, change: Partial<Entry>) =>
    setEntries((current) => current.map((e) => (e.key === key ? { ...e, ...change } : e)));

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  return (
    <View style={styles.form}>
      <Heading level={2}>Document checklist</Heading>
      <Text style={[styles.body, muted]}>
        The documents your firm asks every client for. On a case, “Request the checklist” asks the
        client for each of these through the client portal, and the case shows which have arrived.
      </Text>

      {state.kind !== 'ready' ? (
        <Text
          aria-live={state.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {state.kind === 'error'
            ? 'Could not load the document checklist.'
            : 'Loading the document checklist…'}
        </Text>
      ) : (
        <>
          <Text style={[styles.caption, muted]}>
            {state.checklist.isDefault
              ? 'Your firm uses the default checklist.'
              : `Last saved ${(state.checklist.updatedAt ?? '').slice(0, 10)}.`}
          </Text>
          {fieldErrors.items ? (
            <Text aria-live="assertive" style={[styles.caption, ink]}>
              {fieldErrors.items}
            </Text>
          ) : null}

          {entries.length === 0 ? (
            <Text style={[styles.body, muted]}>The checklist is empty.</Text>
          ) : (
            <View role="list" style={styles.entries}>
              {entries.map((entry, index) =>
                editable ? (
                  <View role="listitem" key={entry.key} style={styles.entry}>
                    <Field.Root
                      name={`title-${entry.key}`}
                      invalid={Boolean(fieldErrors[`items.${String(index)}.title`])}
                    >
                      <Field.Label>Document</Field.Label>
                      <Input
                        value={entry.title}
                        disabled={busy}
                        onValueChange={(title) => update(entry.key, { title })}
                      />
                      {fieldErrors[`items.${String(index)}.title`] ? (
                        <Field.Error match>
                          {fieldErrors[`items.${String(index)}.title`]}
                        </Field.Error>
                      ) : null}
                    </Field.Root>
                    <Field.Root name={`kind-${entry.key}`}>
                      <Field.Label>Filed as</Field.Label>
                      <Select
                        options={KIND_OPTIONS}
                        value={entry.kind}
                        disabled={busy}
                        onValueChange={(kind) => {
                          if (isDocumentKind(kind)) update(entry.key, { kind });
                        }}
                      />
                    </Field.Root>
                    <Field.Root
                      name={`description-${entry.key}`}
                      invalid={Boolean(fieldErrors[`items.${String(index)}.description`])}
                    >
                      <Field.Label>What to tell the client</Field.Label>
                      <Textarea
                        value={entry.description}
                        onValueChange={(description) => update(entry.key, { description })}
                      />
                      {fieldErrors[`items.${String(index)}.description`] ? (
                        <Field.Error match>
                          {fieldErrors[`items.${String(index)}.description`]}
                        </Field.Error>
                      ) : null}
                    </Field.Root>
                    <Button
                      size="lg"
                      intent="secondary"
                      disabled={busy}
                      aria-label={`Remove ${entry.title || 'this document'} from the checklist`}
                      onPress={() =>
                        setEntries((current) => current.filter((e) => e.key !== entry.key))
                      }
                      style={styles.remove}
                    >
                      Remove
                    </Button>
                  </View>
                ) : (
                  <View role="listitem" key={entry.key} style={styles.entry}>
                    <Heading level={3}>{entry.title}</Heading>
                    <Text style={[styles.caption, muted]}>Filed as {kindLabel(entry.kind)}</Text>
                    {entry.description !== '' ? (
                      <Text style={[styles.body, ink]}>{entry.description}</Text>
                    ) : null}
                  </View>
                ),
              )}
            </View>
          )}

          {editable ? (
            <View style={styles.actions}>
              <Button
                size="lg"
                intent="secondary"
                disabled={busy}
                onPress={() =>
                  setEntries((current) => [
                    ...current,
                    entryFrom({ title: '', kind: 'other', description: null }),
                  ])
                }
              >
                Add a document
              </Button>
              <Button size="lg" onPress={() => void save()} disabled={busy}>
                {busy ? 'Saving…' : 'Save checklist'}
              </Button>
              <Button
                size="lg"
                intent="secondary"
                disabled={busy || state.checklist.isDefault}
                onPress={() => setConfirmReset(true)}
              >
                Reset to the default
              </Button>
            </View>
          ) : (
            <Text style={[styles.body, muted]}>
              Changing the checklist is an administrator’s job.
            </Text>
          )}
        </>
      )}

      <AlertDialog.Root
        open={confirmReset}
        onOpenChange={(next) => {
          if (!next) setConfirmReset(false);
        }}
      >
        <AlertDialog.Popup>
          <AlertDialog.Title>Reset the checklist?</AlertDialog.Title>
          <AlertDialog.Description>
            Your firm’s list is replaced by the default. Documents already requested on a case are
            not affected.
          </AlertDialog.Description>
          <View style={styles.actions}>
            <Button
              size="lg"
              intent="danger"
              disabled={busy}
              onPress={() => {
                setConfirmReset(false);
                void reset();
              }}
            >
              Reset
            </Button>
            <AlertDialog.Close>Cancel</AlertDialog.Close>
          </View>
        </AlertDialog.Popup>
      </AlertDialog.Root>
    </View>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
    marginTop: spacing.xs,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  caption: {
    fontSize: fontSizes.caption,
    lineHeight: fontSizes.caption * 1.5,
  },
  entries: {
    gap: spacing.lg,
  },
  entry: {
    gap: spacing.sm,
  },
  form: {
    gap: spacing.md,
    marginBottom: spacing.lg,
    marginTop: spacing.sm,
  },
  remove: {
    alignSelf: 'flex-start',
  },
});
