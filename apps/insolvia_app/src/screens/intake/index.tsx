import { ApiException, ApiValidationException, revisedProvenance } from '@insolvia-ai/api-client';
import type {
  CaseCollection,
  Debtor,
  DebtorBody,
  FilingRole,
  FirmClient,
} from '@insolvia-ai/api-client';
import { Field, Select, Tabs } from '@insolvia-ai/design-system';
import { useLocalSearchParams } from 'expo-router';
import { useCallback, useEffect, useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { CaseColumn, useCase } from '@/components/case-shell';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

import { AssetsEditor } from './assets';
import { LinkClient, LinkedClient } from './client-panel';
import { CollectionEditor } from './collection-editor';
import { COLLECTION_SPECS } from './collections';
import { isCommunityPropertyState } from './community-property';
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
 * **Debtor 1 and Debtor 2 are the firm's clients, copied** (ADR 0022). The
 * role picker never mints a debtor from a save: an empty role offers to LINK
 * a client (`client-panel.tsx`), which the server copies in, and only then
 * do its fields appear. A non-filing spouse need not be a client, so their
 * fields are always there and the link is optional. A linked debtor shows
 * its client, where the two records now differ, and the two acts that
 * resolve it. And every save keeps provenance PER FIELD
 * (`revisedProvenance`): a copied value nobody touched stays `client`.
 *
 * Beyond the debtor, the screen is SECTIONED — one section per case
 * collection (issue #249), chosen from a picker rather than a twelfth tab,
 * because eleven tabs wrap into an unreadable ribbon. The debtor section
 * keeps its autosave; each collection section is a `CollectionEditor`, whose
 * saves are explicit (its header comment says why the difference is
 * deliberate).
 */

type Section = 'debtor' | CaseCollection;

/**
 * Every collection's spec MINUS `community_household_members` when no
 * debtor's residence is in a community-property state — the form asks about
 * one only where §541(a)(2) applies (issue #347). Recomputed per render from
 * `bodies` rather than memoised: it is cheap (ten specs, at most two debtors)
 * and the alternative is a `useMemo` dependency array holding a freshly
 * mapped array every render anyway.
 */
function visibleSpecs(bodies: Partial<Record<FilingRole, DebtorBody>>): typeof COLLECTION_SPECS {
  const communityPropertyState = Object.values(bodies).some((body) =>
    isCommunityPropertyState(body?.residence_address?.state),
  );
  return communityPropertyState
    ? COLLECTION_SPECS
    : COLLECTION_SPECS.filter((spec) => spec.collection !== 'community_household_members');
}

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
    // Server-owned (ADR 0022): the link to the firm client and the computed
    // divergence from it are not answers on this form. Left in the body they
    // would be walked by `staffTypedProvenance` and sent back as "typed".
    client_id: _clientId,
    differs_from_client: _differs,
    ...body
  } = debtor;
  return body;
}

/** The `message` of a 409 body (`{"error": "ConflictError", "message": …}`). */
function conflictMessage(body: string): string {
  try {
    const parsed: unknown = JSON.parse(body);
    if (typeof parsed === 'object' && parsed !== null && 'message' in parsed) {
      const { message } = parsed as { message: unknown };
      if (typeof message === 'string' && message !== '') return message;
    }
  } catch {
    // Not JSON — fall through to the generic sentence.
  }
  return 'This case’s state does not allow that right now.';
}

export function Intake() {
  const theme = useTheme();
  const { call } = useApi();
  const { caseId } = useLocalSearchParams<{ caseId: string }>();

  const [section, setSection] = useState<Section>('debtor');
  // A record another section asked to open — the property's "add a secured
  // claim", already pointed at the property (issue #345). Consumed by the
  // editor at mount; cleared by any ordinary section change.
  const [handoff, setHandoff] = useState<{
    readonly collection: CaseCollection;
    readonly body: Record<string, unknown>;
  } | null>(null);
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

  // The last record the SERVER returned for each role — what the screen was
  // loaded with, then each save's answer. Two things read it: the client
  // panel (the link, and `differs_from_client` as of the last save), and
  // `persist`, which diffs the form against it so a field nobody touched
  // keeps the provenance it arrived with (ADR 0022 — a copied field stays
  // `client` until a person changes THAT field). A ref beside the state
  // because `persist` is a stable callback and must read the newest one.
  const [records, setRecordsState] = useState<Partial<Record<FilingRole, Debtor>>>({});
  const recordsRef = useRef<Partial<Record<FilingRole, Debtor>>>({});
  const remember = useCallback((debtor: Debtor) => {
    recordsRef.current = { ...recordsRef.current, [debtor.filing_role]: debtor };
    setRecordsState(recordsRef.current);
  }, []);
  // The linked clients' records, by id, for the panel's name and diff.
  const [clients, setClients] = useState<Readonly<Record<string, FirmClient>>>({});

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
        for (const debtor of result.value) remember(debtor);
        setLoad({ kind: 'ready' });
      } catch {
        if (!cancelled) setLoad({ kind: 'error', message: 'Could not load this intake.' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId, remember]);

  // Read each linked client once — only when the caller may see the
  // directory, which the server signals by sending `differs_from_client` at
  // all. A failure leaves the panel on "Linked client"; nothing depends on it.
  const linkedIds = Object.values(records)
    .filter((debtor) => debtor.client_id !== undefined && debtor.differs_from_client !== undefined)
    .map((debtor) => debtor.client_id as string);
  const missing = linkedIds.filter((id) => !(id in clients)).join(',');
  const refreshClient = useCallback(
    async (id: string) => {
      try {
        const result = await call((client) => client.getFirmClient(id));
        if (result.ok) setClients((current) => ({ ...current, [id]: result.value }));
      } catch {
        // Shown as an unnamed link; the acts still work without it.
      }
    },
    [call],
  );
  useEffect(() => {
    for (const id of missing === '' ? [] : missing.split(',')) void refreshClient(id);
  }, [missing, refreshClient]);

  const persist = useCallback(
    async (which: FilingRole, body: DebtorBody) => {
      pending.current = null;
      setSave((current) => ({ ...current, [which]: { kind: 'saving' } }));
      try {
        // Per field, against the last record the server returned: a value
        // still what it was keeps the entry it came with — `client` on a
        // copied field, say — and only what the preparer changed or added is
        // `staff_typed`. Re-stamping the whole record would erase the copy's
        // attribution on the first autosave, and the API refuses a `client`
        // entry on a changed value, so the map has to be exactly this.
        const result = await call((client) =>
          client.putDebtor(caseId, which, {
            ...body,
            provenance: revisedProvenance(body, recordsRef.current[which]),
          }),
        );
        if (!result.ok) {
          // The session ended and useApi has already navigated. Leaving this on
          // "Saving…" would announce a request that will never finish.
          setSave((current) => ({ ...current, [which]: { kind: 'idle' } }));
          return;
        }
        // The new baseline — and the new divergence. The form's body is NOT
        // replaced: the preparer may have typed on while this was in flight.
        remember(result.value);
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
    [call, caseId, remember],
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
  const flush = useCallback((): Promise<void> => {
    if (timer.current !== null) {
      clearTimeout(timer.current);
      timer.current = null;
    }
    const outstanding = pending.current;
    return outstanding === null ? Promise.resolve() : persist(outstanding.role, outstanding.body);
  }, [persist]);

  const switchRole = (next: FilingRole) => {
    // Flush first: waiting out the debounce would land the edit under whichever
    // role was selected when the timer fired. Nothing shared is reset here —
    // save state and errors are keyed by role, so the flush's answer arrives at
    // the record it belongs to however late it is.
    void flush();
    setRole(next);
  };

  // Recomputed from `bodies` on every render — see `visibleSpecs`.
  const specs = visibleSpecs(bodies);
  const sectionOptions: readonly { readonly value: Section; readonly label: string }[] = [
    { value: 'debtor', label: 'About the debtor' },
    ...specs.map((spec) => ({ value: spec.collection, label: spec.title })),
  ];

  const switchSection = (next: string) => {
    const chosen = sectionOptions.find((option) => option.value === next);
    if (chosen === undefined) return;
    // Leaving the debtor section is navigation like any other: the pending
    // debounce flushes rather than being dropped with the section.
    void flush();
    setHandoff(null);
    setSection(chosen.value);
  };

  const openCollection = useCallback(
    (collection: CaseCollection, body: Record<string, unknown>) => {
      void flush();
      setHandoff({ collection, body });
      setSection(collection);
    },
    [flush],
  );

  // FLUSHES on unmount rather than discarding. Clearing the timer alone lost
  // every keystroke typed in the last 800ms whenever the user navigated away —
  // back to the case list, or any in-app link — with the status region still
  // reading "Changes save automatically" as the work went.
  useEffect(() => () => void flush(), [flush]);

  const { matter, reload } = useCase();

  /** A record the server just wrote as a whole — a link or a re-copy. It
   * replaces the form's body as well as the baseline: the pending edit was
   * flushed first, and what the server holds now IS the record. */
  const adopt = (debtor: Debtor) => {
    remember(debtor);
    setBodies((current) => ({ ...current, [debtor.filing_role]: bodyOf(debtor) }));
    setFieldErrors((current) => ({ ...current, [debtor.filing_role]: {} }));
    // The case's title is its debtors' names.
    void reload();
  };

  /** One of the panel's two acts, after the pending edit has landed — so the
   * server compares the record the preparer sees, and no late autosave can
   * overwrite what the act wrote. Answers a message for the panel, or null. */
  const act = async (
    which: FilingRole,
    request: 'copyDebtorFromClient' | 'copyDebtorToClient',
  ): Promise<string | null> => {
    await flush();
    try {
      const result = await call((client) => client[request](caseId, which));
      if (!result.ok) return null;
      if (request === 'copyDebtorFromClient') {
        adopt(result.value);
      } else {
        remember(result.value);
        if (result.value.client_id !== undefined) await refreshClient(result.value.client_id);
      }
      return null;
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        return Object.values(cause.fields)[0] ?? 'The client record could not be changed.';
      }
      if (cause instanceof ApiException && cause.statusCode === 409) {
        // The case's state refused it (filed, or no client linked) — the
        // server's sentence says which.
        return conflictMessage(cause.body);
      }
      return 'Could not reach the server. Try again.';
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const saveState: SaveState = save[role] ?? { kind: 'idle' };
  const record = records[role];

  // Looked up in the FILTERED list, not the full one: a debtor edited back
  // out of a community-property state while this section is open should stop
  // rendering it, the same as it never appearing in the picker.
  const spec = specs.find((candidate) => candidate.collection === section);

  return (
    <CaseColumn>
      <Heading level={1}>Intake</Heading>

      <View style={styles.sectionPicker}>
        <Field.Root>
          <Field.Label>Section</Field.Label>
          <Select options={[...sectionOptions]} value={section} onValueChange={switchSection} />
        </Field.Root>
      </View>

      {/* ONE region, always mounted, whose text changes. aria-live announces a
          CHANGE to a region already in the DOM — a region that appears at the
          same moment as its message is silent, which made the load error the
          one message most worth hearing and the one guaranteed not to be. */}
      <Text
        aria-live={load.kind === 'error' ? 'assertive' : 'polite'}
        style={[
          styles.status,
          load.kind === 'error'
            ? { color: theme.colors.danger, fontFamily: theme.typography.body }
            : muted,
        ]}
      >
        {section === 'debtor' && load.kind === 'loading'
          ? 'Loading this intake…'
          : load.kind === 'error'
            ? load.message
            : ''}
      </Text>

      {spec === undefined ? null : spec.collection === 'assets' ? (
        // The category-driven Schedule A/B screen (issue 13.3 / #344) — its
        // own component, not `CollectionEditor`, because its field set
        // changes with the category. `key` remounts it on a section change
        // for the same reason `CollectionEditor` below does.
        <AssetsEditor
          key={spec.collection}
          caseId={caseId}
          initialForm={handoff?.collection === spec.collection ? handoff.body : undefined}
          onOpenCollection={openCollection}
        />
      ) : (
        // `key` remounts the editor on a section change so one section's list,
        // form and errors cannot leak into another's.
        <CollectionEditor
          key={spec.collection}
          caseId={caseId}
          spec={spec}
          initialForm={handoff?.collection === spec.collection ? handoff.body : undefined}
        />
      )}

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

            {record?.client_id !== undefined ? (
              <LinkedClient
                key={`${role}:${record.client_id}`}
                debtor={record}
                client={clients[record.client_id] ?? null}
                filed={matter.status === 'filed'}
                onRecopy={() => act(role, 'copyDebtorFromClient')}
                onUpdateClient={() => act(role, 'copyDebtorToClient')}
              />
            ) : (
              <LinkClient
                key={role}
                caseId={caseId}
                role={role}
                required={role !== 'non_filing_spouse'}
                taken={Object.values(records)
                  .filter((debtor) => debtor.filing_role !== role)
                  .flatMap((debtor) => (debtor.client_id === undefined ? [] : [debtor.client_id]))}
                onLinked={adopt}
              />
            )}

            {/* Debtor 1 and Debtor 2 are the firm's clients, so their
                fields exist only once one is linked — a save cannot mint
                either (ADR 0022). A non-filing spouse need not be a client,
                so their fields are always here. */}
            {record !== undefined || role === 'non_filing_spouse' ? (
              <DebtorFields
                body={bodies[role] ?? {}}
                onChange={change}
                errors={fieldErrors[role] ?? {}}
              />
            ) : null}
          </Tabs.Panel>
        </Tabs.Root>
      ) : null}
    </CaseColumn>
  );
}

const styles = StyleSheet.create({
  sectionPicker: { marginBottom: spacing.sm },
  status: {
    fontSize: fontSizes.label,
    lineHeight: fontSizes.label * 1.5,
    // The heading, this line and the first field group were one flush stack;
    // the section step below separates the "what this is" pair from the form.
    marginBottom: spacing.md,
  },
});
