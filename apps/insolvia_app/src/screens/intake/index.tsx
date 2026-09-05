import { ApiValidationException, staffTypedProvenance } from '@insolvia-ai/api-client';
import type { CaseCollection, Debtor, DebtorBody, FilingRole } from '@insolvia-ai/api-client';
import { Field, Select, Tabs } from '@insolvia-ai/design-system';
import { useLocalSearchParams } from 'expo-router';
import { useCallback, useEffect, useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

import { CollectionEditor } from './collection-editor';
import { COLLECTION_SPECS } from './collections';
import { DebtorFields } from './debtor-fields';

/**
 * `/cases/<id>/intake` — the structured intake (issue 8.5).
 *
 * THE ONE THING THIS MUST NEVER DO IS LOSE A HALF-FINISHED INTAKE, which is
 * what shapes everything below:
 *
 * - **Autosave, debounced**, rather than a Save button. A form this long
 *   collected behind one button is a form that loses an afternoon to a closed
 *   tab. The API accepts an entirely empty record for the same reason.
 * - **The WHOLE record goes on every save.** The endpoint is a PUT, because
 *   "every populated field carries provenance" can only be checked against a
 *   complete record (see the API's parse_debtor). So the client holds the whole
 *   thing and sends it; a field left out is cleared, which is also how removing
 *   an alias works.
 * - **Resume by loading first.** Every debtor of the case is fetched on mount,
 *   so returning to a half-done intake shows it rather than an empty form.
 *
 * A joint filing is two debtor RECORDS, not a second column, which is why the
 * role picker switches between whole records rather than revealing more fields
 * — see docs/reference/case-data-model.md.
 *
 * Beyond the debtor, the screen is SECTIONED — one section per case
 * collection (issue #249), chosen from a picker rather than a twelfth tab,
 * because eleven tabs wrap into an unreadable ribbon. The debtor section
 * keeps its autosave; each collection section is a `CollectionEditor`, whose
 * saves are explicit (its header comment says why the difference is
 * deliberate).
 */

type Section = 'debtor' | CaseCollection;

const SECTION_OPTIONS: readonly { readonly value: Section; readonly label: string }[] = [
  { value: 'debtor', label: 'About the debtor' },
  ...COLLECTION_SPECS.map((spec) => ({ value: spec.collection, label: spec.title })),
];

const ROLES: readonly { readonly value: FilingRole; readonly label: string }[] = [
  { value: 'debtor_1', label: 'Debtor 1' },
  { value: 'debtor_2', label: 'Debtor 2' },
  { value: 'non_filing_spouse', label: 'Non-filing spouse' },
];

/** Long enough that ordinary typing does not fire a request per word, short
 * enough that a closed tab loses a sentence rather than a session. */
const AUTOSAVE_DELAY_MS = 800;

type SaveState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'saving' }
  | { readonly kind: 'saved' }
  | { readonly kind: 'error'; readonly message: string };

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready' }
  | { readonly kind: 'error'; readonly message: string };

function bodyOf(debtor: Debtor): DebtorBody {
  const {
    id: _id,
    case_id: _caseId,
    filing_role: _role,
    created_at: _created,
    updated_at: _updated,
    provenance: _provenance,
    ...body
  } = debtor;
  return body;
}

export function Intake() {
  const theme = useTheme();
  const { call } = useApi();
  const { caseId } = useLocalSearchParams<{ caseId: string }>();

  const [section, setSection] = useState<Section>('debtor');
  const [role, setRole] = useState<FilingRole>('debtor_1');
  const [bodies, setBodies] = useState<Partial<Record<FilingRole, DebtorBody>>>({});
  const [load, setLoad] = useState<LoadState>({ kind: 'loading' });
  // KEYED BY ROLE, both of them. A flush fired by switching debtor resolves
  // AFTER the switch, so a single shared slot renders the outgoing record's
  // result under the incoming one: "Saved" announced for a record that was not
  // saved, and a field error pointing at an empty box belonging to someone
  // else. Someone correcting that types the fix into the wrong debtor.
  const [save, setSave] = useState<Partial<Record<FilingRole, SaveState>>>({});
  const [fieldErrors, setFieldErrors] = useState<
    Partial<Record<FilingRole, Record<string, string>>>
  >({});

  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  // What the timer is holding, so it can be flushed rather than dropped.
  const pending = useRef<{ role: FilingRole; body: DebtorBody } | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) => client.listDebtors(caseId));
        if (!result.ok || cancelled) return;
        setBodies(
          Object.fromEntries(result.value.map((debtor) => [debtor.filing_role, bodyOf(debtor)])),
        );
        setLoad({ kind: 'ready' });
      } catch {
        if (!cancelled) setLoad({ kind: 'error', message: 'Could not load this intake.' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId]);

  const persist = useCallback(
    async (which: FilingRole, body: DebtorBody) => {
      pending.current = null;
      setSave((current) => ({ ...current, [which]: { kind: 'saving' } }));
      try {
        // The provenance map is built from the body rather than tracked
        // alongside it: a person typed every value on this screen, so
        // "staff_typed on each populated field" is the whole truth, and
        // deriving it means it cannot drift out of step with the record.
        const result = await call((client) =>
          client.putDebtor(caseId, which, { ...body, provenance: staffTypedProvenance(body) }),
        );
        if (!result.ok) {
          // The session ended and useApi has already navigated. Leaving this on
          // "Saving…" would announce a request that will never finish.
          setSave((current) => ({ ...current, [which]: { kind: 'idle' } }));
          return;
        }
        setFieldErrors((current) => ({ ...current, [which]: {} }));
        setSave((current) => ({ ...current, [which]: { kind: 'saved' } }));
      } catch (cause) {
        if (cause instanceof ApiValidationException) {
          // The server is the source of truth for validation (ADR 0001), so
          // its per-field messages are rendered as-is against the same dotted
          // paths the fields write to.
          setFieldErrors((current) => ({ ...current, [which]: cause.fields }));
          setSave((current) => ({
            ...current,
            [which]: { kind: 'error', message: 'Some answers need attention.' },
          }));
        } else {
          setSave((current) => ({
            ...current,
            [which]: {
              kind: 'error',
              message: 'Could not save. Retrying on your next change.',
            },
          }));
        }
      }
    },
    [call, caseId],
  );

  const change = (next: DebtorBody) => {
    setBodies((current) => ({ ...current, [role]: next }));
    if (timer.current !== null) clearTimeout(timer.current);
    pending.current = { role, body: next };
    timer.current = setTimeout(() => {
      // Cleared here, not only on the next edit. Leaving it set made the guard
      // in switchRole permanently true after the first edit of the session, so
      // every tab press re-sent an identical record.
      timer.current = null;
      void persist(role, next);
    }, AUTOSAVE_DELAY_MS);
  };

  // Switching roles flushes first. Waiting out the debounce would mean the
  // pending edit lands under whichever role happened to be selected when the
  // timer fired — writing one debtor's name onto another's record.
  const flush = useCallback(() => {
    if (timer.current !== null) {
      clearTimeout(timer.current);
      timer.current = null;
    }
    const outstanding = pending.current;
    if (outstanding !== null) void persist(outstanding.role, outstanding.body);
  }, [persist]);

  const switchRole = (next: FilingRole) => {
    // Flush first: waiting out the debounce would land the edit under whichever
    // role was selected when the timer fired. Nothing shared is reset here —
    // save state and errors are keyed by role, so the flush's answer arrives at
    // the record it belongs to however late it is.
    flush();
    setRole(next);
  };

  const switchSection = (next: string) => {
    const chosen = SECTION_OPTIONS.find((option) => option.value === next);
    if (chosen === undefined) return;
    // Leaving the debtor section is navigation like any other: the pending
    // debounce flushes rather than being dropped with the section.
    flush();
    setSection(chosen.value);
  };

  // FLUSHES on unmount rather than discarding. Clearing the timer alone lost
  // every keystroke typed in the last 800ms whenever the user navigated away —
  // back to the case list, or any in-app link — with the status region still
  // reading "Changes save automatically" as the work went.
  useEffect(() => flush, [flush]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const saveState: SaveState = save[role] ?? { kind: 'idle' };

  const spec = COLLECTION_SPECS.find((candidate) => candidate.collection === section);

  return (
    <>
      <Heading level={1}>Intake</Heading>

      <View style={styles.sectionPicker}>
        <Field.Root>
          <Field.Label>Section</Field.Label>
          <Select options={[...SECTION_OPTIONS]} value={section} onValueChange={switchSection} />
        </Field.Root>
      </View>

      {/* ONE region, always mounted, whose text changes. aria-live announces a
          CHANGE to a region already in the DOM — a region that appears at the
          same moment as its message is silent, which made the load error the
          one message most worth hearing and the one guaranteed not to be. */}
      <Text
        aria-live={load.kind === 'error' ? 'assertive' : 'polite'}
        style={[styles.status, load.kind === 'error' ? { color: theme.colors.danger } : muted]}
      >
        {section === 'debtor' && load.kind === 'loading'
          ? 'Loading this intake…'
          : load.kind === 'error'
            ? load.message
            : ''}
      </Text>

      {spec !== undefined ? (
        // `key` remounts the editor on a section change so one section's list,
        // form and errors cannot leak into another's.
        <CollectionEditor key={spec.collection} caseId={caseId} spec={spec} />
      ) : null}

      {section === 'debtor' && load.kind === 'ready' ? (
        // The design system's Tabs, not a hand-rolled one. A `Text` with
        // `onPress` renders as a plain div: react-native-web assigns a tabIndex
        // only to the six roles it auto-focuses, and `tab` is not among them —
        // so the first version of this could not be reached by keyboard at all,
        // and two of the three records this screen exists to collect were
        // mouse-only. WCAG 2.1.1, Level A. Tabs' native leaf is a Pressable,
        // which RNW does focus and activate on Enter, and it brings the
        // tabpanel pairing the hand-rolled version also lacked.
        //
        // Whole records, not columns: a joint filing is two debtor records, and
        // a non-filing spouse may appear on 106I without filing.
        <Tabs.Root
          defaultValue="debtor_1"
          value={role}
          onValueChange={(next) => switchRole(next as FilingRole)}
          aria-label="Who this is about"
        >
          <Tabs.List>
            {ROLES.map((option) => (
              <Tabs.Tab key={option.value} value={option.value}>
                {option.label}
              </Tabs.Tab>
            ))}
          </Tabs.List>

          <Tabs.Panel value={role}>
            <Heading level={2}>{ROLES.find((option) => option.value === role)?.label}</Heading>

            <Text aria-live="polite" style={[styles.status, muted]}>
              {saveState.kind === 'saving'
                ? 'Saving…'
                : saveState.kind === 'saved'
                  ? 'Saved'
                  : saveState.kind === 'error'
                    ? saveState.message
                    : 'Changes save automatically'}
            </Text>

            <DebtorFields
              body={bodies[role] ?? {}}
              onChange={change}
              errors={fieldErrors[role] ?? {}}
            />
          </Tabs.Panel>
        </Tabs.Root>
      ) : null}
    </>
  );
}

const styles = StyleSheet.create({
  sectionPicker: { marginBottom: spacing.sm },
  status: { fontSize: fontSizes.label },
});
