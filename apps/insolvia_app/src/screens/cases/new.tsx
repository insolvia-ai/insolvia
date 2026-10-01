import { ApiValidationException, permits } from '@insolvia-ai/api-client';
import type { CaseChapter, CourtRegistry, FirmClient } from '@insolvia-ai/api-client';
import { Button, Checkbox, Field, Input, RadioGroup, Select } from '@insolvia-ai/design-system';
import { Link, useRouter } from 'expo-router';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { sortName } from '@/components/client-names';
import { CourtPicker } from '@/components/court-picker';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

const CHAPTERS: readonly { readonly value: CaseChapter; readonly label: string }[] = [
  { value: 7, label: 'Chapter 7' },
  { value: 13, label: 'Chapter 13' },
  { value: 11, label: 'Chapter 11' },
  { value: 12, label: 'Chapter 12' },
];

type RegistryState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly registry: CourtRegistry }
  | { readonly kind: 'error' };

type ClientsState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly clients: readonly FirmClient[] }
  | { readonly kind: 'error' };

/** The client `Select`'s value for "a new client, named below". */
const NEW_CLIENT = '__new_client__';

/**
 * One debtor's client: who was chosen, and — when that is a new client — the
 * two name boxes. Kept per slot so Debtor 2's half-typed name survives
 * Debtor 1's picker changing.
 */
interface Slot {
  readonly choice: string | null;
  readonly given: string;
  readonly surname: string;
}

const EMPTY_SLOT: Slot = { choice: null, given: '', surname: '' };

/**
 * The labels each slot's controls carry. Debtor 1's are a SELECTOR CONTRACT
 * with `e2e/support/scratch-case.ts` — the combobox "Client" and the
 * textboxes "Client’s first name" / "Client’s last name" — so they stay the
 * words #411's minimal picker used.
 */
const SLOT_LABELS = [
  {
    field: 'client_ids',
    nameField: 'clientName',
    picker: 'Client',
    given: 'Client’s first name',
    surname: 'Client’s last name',
    description:
      'The person this case is for. Their name and contact details are copied into the case as ' +
      'Debtor 1, and stay the case’s own.',
  },
  {
    field: 'client_ids.1',
    nameField: 'client2Name',
    picker: 'Second client (Debtor 2)',
    given: 'Second client’s first name',
    surname: 'Second client’s last name',
    description: 'The spouse filing with them, copied into the case as Debtor 2.',
  },
] as const;

/**
 * `/cases/new` — open a case, FOR ONE OR TWO CLIENTS (ADR 0022).
 *
 * This is where #411's minimal client picker grew up, and where every way of
 * starting a case arrives: "New case" on `/cases` and the dashboard, "Start a
 * case for this client" on a client's record, and "Save and start a case" on
 * `/clients/new`. The last two pass the client as `?client=<id>`, which
 * preselects Debtor 1, so the preparer's next choice is the court rather
 * than the person they just chose.
 *
 * A client is picked or added here, never skipped: `POST /v1/cases` requires
 * `client_ids`. A new client is added to the directory FIRST (its own
 * `POST /v1/firm/clients`) and then chosen, so a case-level refusal after it
 * — no court, say — does not add the same person twice on the retry.
 *
 * JOINT FILING PICKS TWO. The checkbox reveals a second picker whose options
 * leave out Debtor 1's client: one client, one role per case, and the server
 * refuses the pair otherwise. A non-filing spouse is not picked here — they
 * need not be the firm's client, and the intake links one if they are.
 *
 * THE COURT IS PICKED, NOT TYPED (issue #360), and the firm's defaults
 * (`/v1/me`'s firm block) preselect court, division and chapter — once, when
 * they arrive, so a membership refresh cannot snap a half-filled form back.
 *
 * On success the preparer lands ON the new case, not back on a list: the
 * next thing anyone does with a case they just opened is work in it.
 */
export function NewCase({ initialClientId }: { initialClientId?: string | undefined }) {
  const theme = useTheme();
  const router = useRouter();
  const { call } = useApi();
  const membership = useMembership();

  const [courts, setCourts] = useState<RegistryState>({ kind: 'loading' });
  const [clients, setClients] = useState<ClientsState>({ kind: 'loading' });
  const [chapter, setChapter] = useState<CaseChapter>(7);
  const [court, setCourt] = useState<string | null>(null);
  const [division, setDivision] = useState<string | null>(null);
  const [defaultsApplied, setDefaultsApplied] = useState(false);
  const [slots, setSlots] = useState<readonly [Slot, Slot]>([
    { ...EMPTY_SLOT, choice: initialClientId ?? null },
    EMPTY_SLOT,
  ]);
  const [joint, setJoint] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const setSlot = (index: 0 | 1, next: Partial<Slot>) =>
    setSlots((current) => {
      const updated: [Slot, Slot] = [current[0], current[1]];
      updated[index] = { ...current[index], ...next };
      return updated;
    });

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const result = await call((client) => client.listFirmClients());
        if (!result.ok || cancelled) return;
        const active = result.value.filter((client) => client.status === 'active');
        setClients({ kind: 'ready', clients: active });
        setSlots((current) => {
          const first = current[0];
          // A preselected client that is not an active one here (archived,
          // or another firm's id in a pasted URL) is dropped rather than
          // kept as a value the picker cannot show.
          if (first.choice !== null && first.choice !== NEW_CLIENT) {
            if (!active.some((client) => client.id === first.choice)) {
              return [{ ...first, choice: active.length === 0 ? NEW_CLIENT : null }, current[1]];
            }
            return current;
          }
          // With nobody in the directory the only possible answer is a new
          // client, so the form starts there rather than on an empty list.
          return active.length === 0 ? [{ ...first, choice: NEW_CLIENT }, current[1]] : current;
        });
      } catch {
        // A 403 is the firm not having granted `clients`, which opening a
        // case needs — the message below says so rather than an empty picker.
        if (!cancelled) setClients({ kind: 'error' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call]);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const result = await call((client) => client.listCourts());
        if (result.ok && !cancelled) setCourts({ kind: 'ready', registry: result.value });
      } catch {
        if (!cancelled) setCourts({ kind: 'error' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call]);

  useEffect(() => {
    if (defaultsApplied || membership === undefined || membership === null) return;
    setDefaultsApplied(true);
    if (membership.defaultChapter !== null) setChapter(membership.defaultChapter);
    if (membership.defaultCourt !== null) setCourt(membership.defaultCourt);
    if (membership.defaultDivision !== null) setDivision(membership.defaultDivision);
  }, [defaultsApplied, membership]);

  /**
   * One slot's client id — adding the named new client first when that is
   * the choice. `null` when the add failed and the session ended (useApi has
   * navigated); a refused name throws, keyed to the slot's name field.
   */
  const resolveSlot = async (index: 0 | 1): Promise<string | null | undefined> => {
    const slot = slots[index];
    if (slot.choice !== NEW_CLIENT) return slot.choice ?? undefined;
    const given = slot.given.trim();
    const surname = slot.surname.trim();
    try {
      const added = await call((client) =>
        client.createFirmClient({
          name: {
            ...(given === '' ? {} : { given }),
            ...(surname === '' ? {} : { surname }),
          },
        }),
      );
      if (!added.ok) return null;
      // Chosen from now on, so a retry after a case-level error does not add
      // the same person twice.
      setClients((current) =>
        current.kind === 'ready'
          ? { kind: 'ready', clients: [...current.clients, added.value] }
          : current,
      );
      setSlot(index, { choice: added.value.id });
      return added.value.id;
    } catch (cause) {
      if (cause instanceof ApiValidationException && cause.fields.name !== undefined) {
        throw new SlotNameError(SLOT_LABELS[index].nameField, cause.fields.name);
      }
      throw cause;
    }
  };

  const submit = async () => {
    setSubmitting(true);
    setFieldErrors({});
    setFormError(null);
    try {
      const first = await resolveSlot(0);
      if (first === null) return;
      const second = joint ? await resolveSlot(1) : undefined;
      if (second === null) return;
      if (joint && second === undefined) {
        setFieldErrors({ 'client_ids.1': 'Choose the second client, or untick joint filing.' });
        return;
      }
      // The ids go as chosen, empty when not — the server's per-field message
      // ("Choose the client this case is for.") is the validation, not a
      // second copy of the rule here (ADR 0001).
      const clientIds = [first, second].filter((id): id is string => id !== undefined);
      const result = await call((client) =>
        client.createCase({
          chapter,
          court: court ?? '',
          division: division ?? '',
          clientIds,
        }),
      );
      if (result.ok) router.replace(`/cases/${result.value.id}`);
    } catch (cause) {
      if (cause instanceof SlotNameError) {
        setFieldErrors({ [cause.field]: cause.message });
      } else if (cause instanceof ApiValidationException) {
        setFieldErrors(cause.fields);
      } else {
        setFormError('Could not open the case. Please try again.');
      }
    } finally {
      setSubmitting(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const danger = { color: theme.colors.danger, fontFamily: theme.typography.body };

  // A courtesy: the API refuses regardless. `undefined` is "not known yet",
  // which renders the form — the safe reading, since the server still answers.
  if (membership != null && !permits(membership.permissions.cases, 'add_edit')) {
    return (
      <AppShell>
        <Heading level={1}>Open a case</Heading>
        <Text style={[styles.body, muted]}>
          Your firm has not given you permission to open cases. Ask one of your firm’s
          administrators if you need it.
        </Text>
      </AppShell>
    );
  }

  return (
    <AppShell>
      <Heading level={1}>Open a case</Heading>
      <Text style={[styles.body, muted]}>
        A case is opened for one of your firm’s clients — or two, for a joint filing. Choose them,
        or add someone new.
      </Text>

      <View style={styles.form}>
        {clients.kind === 'ready' ? (
          <>
            <ClientSlot
              index={0}
              clients={clients.clients}
              slot={slots[0]}
              onChange={(next) => setSlot(0, next)}
              errors={fieldErrors}
            />
            <View style={styles.checkboxRow}>
              <Checkbox.Root
                aria-label="Joint filing — two clients"
                checked={joint}
                onCheckedChange={setJoint}
              >
                <Checkbox.Indicator>✓</Checkbox.Indicator>
              </Checkbox.Root>
              <Text
                aria-hidden
                style={[
                  styles.checkboxLabel,
                  { color: theme.colors.ink, fontFamily: theme.typography.body },
                ]}
              >
                Joint filing — two clients
              </Text>
            </View>
            {joint ? (
              <ClientSlot
                index={1}
                clients={clients.clients.filter((client) => client.id !== slots[0].choice)}
                slot={slots[1]}
                onChange={(next) => setSlot(1, next)}
                errors={fieldErrors}
              />
            ) : null}
          </>
        ) : (
          <Text
            aria-live={clients.kind === 'error' ? 'assertive' : 'polite'}
            style={[styles.body, clients.kind === 'error' ? danger : muted]}
          >
            {clients.kind === 'loading'
              ? 'Loading your clients…'
              : 'Could not load your client directory — a case is opened for a client, so ' +
                'opening one needs access to it. Ask a firm admin to grant “Clients”.'}
          </Text>
        )}

        {/*
          RadioGroup.Item IS the 20dp circle, so the label is a SIBLING, the
          Item carries `aria-label`, and `hitSlop` takes the target to 44dp
          (WCAG 2.5.5) without changing the visual. The case list's suite used
          to pin this; this screen's does now.
        */}
        <RadioGroup.Root
          aria-label="Chapter"
          value={String(chapter)}
          onValueChange={(next) => setChapter(Number(next) as CaseChapter)}
          style={styles.chapters}
        >
          {CHAPTERS.map((option) => (
            <View key={option.value} style={styles.chapterOption}>
              <RadioGroup.Item value={String(option.value)} aria-label={option.label} hitSlop={12}>
                <RadioGroup.Indicator />
              </RadioGroup.Item>
              <Text
                style={[
                  styles.chapterLabel,
                  { color: theme.colors.ink, fontFamily: theme.typography.body },
                ]}
              >
                {option.label}
              </Text>
            </View>
          ))}
        </RadioGroup.Root>
        {fieldErrors.chapter ? (
          <Text aria-live="assertive" style={[styles.error, danger]}>
            {fieldErrors.chapter}
          </Text>
        ) : null}

        {courts.kind === 'ready' ? (
          <CourtPicker
            registry={courts.registry}
            court={court}
            division={division}
            onCourtChange={(next) => {
              setCourt(next);
              // A division belongs to its court; a new court starts with none.
              setDivision(null);
            }}
            onDivisionChange={setDivision}
            errors={fieldErrors}
          />
        ) : (
          <Text
            aria-live={courts.kind === 'error' ? 'assertive' : 'polite'}
            style={[styles.body, courts.kind === 'error' ? danger : muted]}
          >
            {courts.kind === 'loading'
              ? 'Loading the court registry…'
              : 'Could not load the court registry — a case cannot be opened until it loads.'}
          </Text>
        )}

        <View style={styles.actions}>
          {/* size="lg" (48dp): the package's md is 40dp, under the 44dp
              WCAG 2.5.5 target-size floor this app enforces. */}
          <Button
            size="lg"
            onPress={() => void submit()}
            disabled={submitting || courts.kind !== 'ready' || clients.kind !== 'ready'}
          >
            {submitting ? 'Opening…' : 'Open case'}
          </Button>
          <Link
            href="/cases"
            style={[
              styles.cancel,
              { color: theme.colors.primary, fontFamily: theme.typography.body },
            ]}
          >
            Cancel
          </Link>
        </View>

        {formError === null ? null : (
          <Text aria-live="assertive" style={[styles.error, danger]}>
            {formError}
          </Text>
        )}
      </View>
    </AppShell>
  );
}

/** A refused new client's name, carried to the name box of the slot it belongs to. */
class SlotNameError extends Error {
  // A declared field, not a parameter property: `erasableSyntaxOnly` is on.
  readonly field: string;

  constructor(field: string, message: string) {
    super(message);
    this.field = field;
  }
}

/**
 * One debtor's client: an active client from the firm's directory, or a new
 * one named in two boxes. The directory is ordered by surname, so options
 * read "Surname, Given" — the order they are sorted in.
 */
function ClientSlot({
  index,
  clients,
  slot,
  onChange,
  errors,
}: {
  index: 0 | 1;
  clients: readonly FirmClient[];
  slot: Slot;
  onChange: (next: Partial<Slot>) => void;
  errors: Readonly<Record<string, string>>;
}) {
  const labels = SLOT_LABELS[index];
  const options = [
    ...clients.map((client) => ({ value: client.id, label: sortName(client) })),
    { value: NEW_CLIENT, label: 'New client…' },
  ];
  const pickError = errors[labels.field];
  const nameError = errors[labels.nameField];
  return (
    <>
      <Field.Root name={labels.field} invalid={Boolean(pickError)}>
        <Field.Label>{labels.picker}</Field.Label>
        <Select
          options={options}
          value={slot.choice}
          onValueChange={(choice) => onChange({ choice })}
          placeholder="Choose a client"
        />
        <Field.Description>{labels.description}</Field.Description>
        {pickError ? <Field.Error match>{pickError}</Field.Error> : null}
      </Field.Root>
      {slot.choice === NEW_CLIENT ? (
        <>
          <Field.Root name={`${labels.nameField}.given`} invalid={Boolean(nameError)}>
            <Field.Label>{labels.given}</Field.Label>
            <Input
              value={slot.given}
              onValueChange={(given) => onChange({ given })}
              autoCorrect={false}
            />
          </Field.Root>
          <Field.Root name={labels.nameField} invalid={Boolean(nameError)}>
            <Field.Label>{labels.surname}</Field.Label>
            <Input
              value={slot.surname}
              onValueChange={(surname) => onChange({ surname })}
              autoCorrect={false}
            />
            {nameError ? <Field.Error match>{nameError}</Field.Error> : null}
          </Field.Root>
        </>
      ) : null}
    </>
  );
}

const styles = StyleSheet.create({
  actions: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.lg,
    marginTop: spacing.xs,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  cancel: {
    fontSize: fontSizes.label,
    fontWeight: '600',
    // 44dp, the WCAG 2.5.5 target size — a text link is no exception.
    lineHeight: 44,
  },
  chapterLabel: {
    fontSize: fontSizes.label,
  },
  chapterOption: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.xs,
  },
  chapters: {
    // Four short options read better across than stacked, and wrap when the
    // column is narrow.
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
  },
  checkboxLabel: {
    fontSize: fontSizes.body,
  },
  checkboxRow: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.sm,
  },
  error: {
    fontSize: fontSizes.label,
  },
  form: {
    gap: spacing.md,
    marginTop: spacing.sm,
  },
});
