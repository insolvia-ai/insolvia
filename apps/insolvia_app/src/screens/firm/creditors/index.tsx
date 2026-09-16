import { ApiValidationException, permits } from '@insolvia-ai/api-client';
import type {
  Address,
  FirmMembership,
  LibraryCreditor,
  LibraryCreditorDraft,
} from '@insolvia-ai/api-client';
import { Button, Checkbox, Field, Input, Textarea } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly creditors: readonly LibraryCreditor[] }
  | { readonly kind: 'error'; readonly message: string };

type Mode =
  | { readonly kind: 'list' }
  | { readonly kind: 'form'; readonly id: string | null; readonly draft: DraftState };

interface DraftState {
  readonly name: string;
  readonly line1: string;
  readonly line2: string;
  readonly city: string;
  readonly state: string;
  readonly postalCode: string;
  readonly county: string;
  readonly preferred: boolean;
  readonly notes: string;
}

const EMPTY_DRAFT: DraftState = {
  name: '',
  line1: '',
  line2: '',
  city: '',
  state: '',
  postalCode: '',
  county: '',
  preferred: false,
  notes: '',
};

function draftFrom(creditor: LibraryCreditor): DraftState {
  return {
    name: creditor.name,
    line1: creditor.address.line1 ?? '',
    line2: creditor.address.line2 ?? '',
    city: creditor.address.city ?? '',
    state: creditor.address.state ?? '',
    postalCode: creditor.address.postal_code ?? '',
    county: creditor.address.county ?? '',
    preferred: creditor.preferred,
    notes: creditor.notes ?? '',
  };
}

function undefinedIfBlank(value: string): string | undefined {
  const trimmed = value.trim();
  return trimmed === '' ? undefined : trimmed;
}

function draftToRequest(draft: DraftState): LibraryCreditorDraft {
  const address: Address = {
    line1: undefinedIfBlank(draft.line1),
    line2: undefinedIfBlank(draft.line2),
    city: undefinedIfBlank(draft.city),
    state: undefinedIfBlank(draft.state),
    postal_code: undefinedIfBlank(draft.postalCode),
    county: undefinedIfBlank(draft.county),
  };
  return {
    name: draft.name,
    address,
    preferred: draft.preferred,
    notes: undefinedIfBlank(draft.notes),
  };
}

function summarize(creditor: LibraryCreditor): string {
  const parts = [creditor.name, creditor.address.city, creditor.address.state].filter(
    (part): part is string => Boolean(part),
  );
  return parts.join(' — ');
}

/**
 * The firm's reusable creditor library manager (issue 13.9 / #350), at
 * `/firm/creditors`. List, add, edit and remove — the same list/form shape
 * `screens/intake/collection-editor.tsx` uses for a case collection, sized
 * down for one firm-scoped resource with no provenance of its own (see
 * `insolvia_core.library_creditors`' module docstring for why it carries
 * none).
 *
 * Gated on the `creditor_library` feature, independently of
 * `firm_administration` — `screens/firm/index.tsx`'s `LibraryLink` is the
 * other half of that gate.
 */
export function CreditorLibrary({ membership }: { membership: FirmMembership }) {
  const theme = useTheme();
  const { call } = useApi();

  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [mode, setMode] = useState<Mode>({ kind: 'list' });
  const [status, setStatus] = useState('');
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [saving, setSaving] = useState(false);

  const mayView = permits(membership.permissions.creditor_library, 'view_only');
  const mayChange = permits(membership.permissions.creditor_library, 'add_edit');

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listLibraryCreditors());
      if (result.ok) {
        setList({ kind: 'ready', creditors: result.value });
      }
    } catch {
      setList({ kind: 'error', message: 'Could not load your firm’s creditor library.' });
    }
  }, [call]);

  useEffect(() => {
    if (mayView) {
      void load();
    }
  }, [load, mayView]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  if (!mayView) {
    return (
      <AppShell>
        <Heading level={1}>Creditor library</Heading>
        <Text style={[styles.body, muted]}>
          Your firm has not given you access to the creditor library. Ask one of your firm’s
          administrators if you need it.
        </Text>
      </AppShell>
    );
  }

  const persist = async (form: Mode & { readonly kind: 'form' }) => {
    setSaving(true);
    setStatus('Saving…');
    setErrors({});
    try {
      const request = draftToRequest(form.draft);
      const result = await call((client) =>
        form.id === null
          ? client.addLibraryCreditor(request)
          : client.updateLibraryCreditor(form.id, request),
      );
      if (!result.ok) {
        setStatus('');
        return;
      }
      // Optimistic local update from the server's own response, rather than
      // a second GET — the same rule `collection-editor.tsx`'s `persist`
      // follows: the write response already IS the current row.
      setList((current) =>
        current.kind === 'ready'
          ? {
              kind: 'ready',
              creditors:
                form.id === null
                  ? [...current.creditors, result.value]
                  : current.creditors.map((c) => (c.id === result.value.id ? result.value : c)),
            }
          : current,
      );
      setStatus('Saved');
      setMode({ kind: 'list' });
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(cause.fields);
        setStatus('Some answers need attention.');
      } else {
        setStatus('Could not save. Your entries are still here — try again.');
      }
    } finally {
      setSaving(false);
    }
  };

  const remove = async (id: string) => {
    setStatus('Removing…');
    try {
      const result = await call((client) => client.removeLibraryCreditor(id));
      if (!result.ok) {
        setStatus('');
        return;
      }
      setList((current) =>
        current.kind === 'ready'
          ? { kind: 'ready', creditors: current.creditors.filter((c) => c.id !== id) }
          : current,
      );
      setStatus('Removed');
    } catch {
      setStatus('Could not remove it. Try again.');
    }
  };

  return (
    <AppShell>
      <Heading level={1}>Your firm’s creditor library</Heading>
      <Text style={[styles.body, muted]}>
        Creditors saved here can be picked straight onto a case’s creditor list, so an address typed
        once does not need retyping on every matter.
      </Text>

      <Text
        aria-live={status === 'Some answers need attention.' ? 'assertive' : 'polite'}
        style={[styles.help, muted]}
      >
        {status}
      </Text>

      {mode.kind === 'list' ? (
        <View style={styles.list}>
          {list.kind !== 'ready' ? (
            <Text
              aria-live={list.kind === 'error' ? 'assertive' : 'polite'}
              style={[styles.body, muted]}
            >
              {list.kind === 'loading' ? 'Loading your firm’s creditor library…' : list.message}
            </Text>
          ) : list.creditors.length === 0 ? (
            <Text style={[styles.body, muted]}>Nothing in your library yet.</Text>
          ) : (
            <View role="list" style={styles.list}>
              {list.creditors.map((creditor) => (
                <View
                  key={creditor.id}
                  role="listitem"
                  style={[styles.row, { borderColor: theme.colors.line }]}
                >
                  <Text
                    style={[
                      styles.rowSummary,
                      { color: theme.colors.ink, fontFamily: theme.typography.body },
                    ]}
                  >
                    {summarize(creditor)}
                    {creditor.preferred ? ' · preferred' : ''}
                  </Text>
                  {mayChange ? (
                    <View style={styles.rowActions}>
                      <Button
                        size="lg"
                        intent="secondary"
                        aria-label={`Edit ${creditor.name}`}
                        onPress={() => {
                          setErrors({});
                          setStatus('');
                          setMode({ kind: 'form', id: creditor.id, draft: draftFrom(creditor) });
                        }}
                      >
                        Edit
                      </Button>
                      <Button
                        size="lg"
                        intent="secondary"
                        aria-label={`Remove ${creditor.name}`}
                        onPress={() => void remove(creditor.id)}
                      >
                        Remove
                      </Button>
                    </View>
                  ) : null}
                </View>
              ))}
            </View>
          )}
          {mayChange ? (
            <Button
              size="lg"
              onPress={() => {
                setErrors({});
                setStatus('');
                setMode({ kind: 'form', id: null, draft: EMPTY_DRAFT });
              }}
            >
              Add creditor
            </Button>
          ) : null}
        </View>
      ) : (
        <CreditorForm
          mode={mode}
          saving={saving}
          errors={errors}
          onChange={(draft) => setMode({ ...mode, draft })}
          onSave={() => void persist(mode)}
          onCancel={() => {
            setErrors({});
            setStatus('');
            setMode({ kind: 'list' });
          }}
        />
      )}
    </AppShell>
  );
}

function CreditorForm({
  mode,
  saving,
  errors,
  onChange,
  onSave,
  onCancel,
}: {
  mode: Mode & { readonly kind: 'form' };
  saving: boolean;
  errors: Readonly<Record<string, string>>;
  onChange: (draft: DraftState) => void;
  onSave: () => void;
  onCancel: () => void;
}) {
  const theme = useTheme();
  const { draft } = mode;
  const set = <K extends keyof DraftState>(key: K, value: DraftState[K]) =>
    onChange({ ...draft, [key]: value });

  return (
    <View style={styles.form}>
      <Field.Root name="name" invalid={Boolean(errors.name)}>
        <Field.Label>Creditor name</Field.Label>
        <Input value={draft.name} onValueChange={(next) => set('name', next)} autoCorrect={false} />
        {errors.name ? <Field.Error match>{errors.name}</Field.Error> : null}
      </Field.Root>

      <Field.Root name="address.line1" invalid={Boolean(errors['address.line1'])}>
        <Field.Label>Address line 1</Field.Label>
        <Input
          value={draft.line1}
          onValueChange={(next) => set('line1', next)}
          autoCorrect={false}
        />
        {errors['address.line1'] ? (
          <Field.Error match>{errors['address.line1']}</Field.Error>
        ) : null}
      </Field.Root>

      <Field.Root name="address.line2" invalid={Boolean(errors['address.line2'])}>
        <Field.Label>Address line 2</Field.Label>
        <Input
          value={draft.line2}
          onValueChange={(next) => set('line2', next)}
          autoCorrect={false}
        />
      </Field.Root>

      <Field.Root name="address.city" invalid={Boolean(errors['address.city'])}>
        <Field.Label>City</Field.Label>
        <Input value={draft.city} onValueChange={(next) => set('city', next)} autoCorrect={false} />
      </Field.Root>

      <Field.Root name="address.state" invalid={Boolean(errors['address.state'])}>
        <Field.Label>State</Field.Label>
        <Input
          value={draft.state}
          onValueChange={(next) => set('state', next)}
          autoCorrect={false}
        />
      </Field.Root>

      <Field.Root name="address.postal_code" invalid={Boolean(errors['address.postal_code'])}>
        <Field.Label>ZIP code</Field.Label>
        <Input
          value={draft.postalCode}
          onValueChange={(next) => set('postalCode', next)}
          autoCorrect={false}
        />
      </Field.Root>

      <View style={styles.checkboxRow}>
        <Checkbox.Root
          aria-label="Preferred"
          checked={draft.preferred}
          onCheckedChange={(next) => set('preferred', next)}
        >
          <Checkbox.Indicator>✓</Checkbox.Indicator>
        </Checkbox.Root>
        <Text
          style={[
            styles.checkboxLabel,
            { color: theme.colors.ink, fontFamily: theme.typography.body },
          ]}
        >
          Preferred — surface this one first in the picker
        </Text>
      </View>

      <Field.Root name="notes" invalid={Boolean(errors.notes)}>
        <Field.Label>Notes</Field.Label>
        <Textarea value={draft.notes} onValueChange={(next) => set('notes', next)} />
        {errors.notes ? <Field.Error match>{errors.notes}</Field.Error> : null}
      </Field.Root>

      <View style={styles.rowActions}>
        <Button size="lg" disabled={saving} onPress={onSave}>
          {mode.id === null ? 'Save creditor' : 'Save changes'}
        </Button>
        <Button size="lg" intent="secondary" onPress={onCancel}>
          Cancel
        </Button>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  checkboxLabel: { fontSize: fontSizes.body },
  checkboxRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
  form: { gap: spacing.md, marginTop: spacing.sm },
  help: { fontSize: fontSizes.label },
  list: { gap: spacing.md },
  row: { borderBottomWidth: 1, gap: spacing.sm, paddingBottom: spacing.sm },
  rowActions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  rowSummary: { fontSize: fontSizes.body },
});
