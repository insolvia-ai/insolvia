import {
  ApiValidationException,
  libraryProvenance,
  staffTypedProvenance,
} from '@insolvia-ai/api-client';
import type {
  Address,
  CaseCollection,
  CaseEntityRequest,
  LibraryCreditor,
} from '@insolvia-ai/api-client';
import {
  Button,
  Checkbox,
  CheckboxGroup,
  DateInput,
  Field,
  Input,
  Select,
  Textarea,
} from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

import type { ChoiceOption, CollectionSpec, FieldSpec } from './collections';
import { COLLECTION_SPECS, labelize } from './collections';
import { AssetLiensPanel, ClaimCollateralPanel } from './liens';
import { newRowId } from './row-id';

/**
 * List, add, edit and remove for one generic case collection (issue #249) —
 * one component for all ten, driven by the specs in `collections.ts`.
 *
 * UNLIKE THE DEBTOR FORM, SAVES ARE EXPLICIT. The debtor is one continuous
 * record and autosaves; a collection is discrete records, and autosaving a
 * half-typed one would CREATE it — a creditor row born from two keystrokes,
 * then another from the next two. So a record is composed locally and lands
 * on Save, whole, with `staff_typed` provenance derived from its populated
 * fields exactly as the debtor's is. What is typed and not yet saved is lost
 * on navigation, and the form says so where the debtor form says the
 * opposite.
 *
 * Every field is optional and nothing here blocks a save — the server
 * validates shape (ADR 0001) and its per-field messages render under the
 * fields they name, keyed by the same dotted paths the fields write to.
 *
 * Two collections carry a panel the specs cannot express (issue #345): a
 * claim's form shows what its collateral covers, and a property's form
 * lists the liens on it and can open the claims section on a new claim
 * already pointed at the property. That hand-off is `onOpenCollection` —
 * the intake screen owns which section is showing, so the editor asks it —
 * and `initialForm` is how the receiving editor starts on the form instead
 * of the list. Both panels are read-only over server-derived figures.
 */

type Body = Record<string, unknown>;

type Mode =
  | { readonly kind: 'list' }
  | {
      readonly kind: 'form';
      readonly id: string | null;
      readonly body: Body;
      /**
       * The `LibraryCreditor.id` the current `name`/`address` were copied
       * from (issue 13.9 / #350) — `creditors` only, `null` otherwise and
       * whenever the fields were typed or edited by hand. Drives which
       * provenance `persist` builds: `libraryProvenance` when set,
       * `staffTypedProvenance` (the ordinary rule) when not.
       */
      readonly librarySourceId: string | null;
    };

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready' }
  | { readonly kind: 'error'; readonly message: string };

interface Row {
  readonly id: string;
  readonly body: Body;
}

interface ReferenceOption {
  readonly value: string;
  readonly label: string;
}

/**
 * A name-and-address record already on this case — another creditor, or a
 * codebtor — offered as a copy source alongside the firm's library (issue
 * 13.9 / #350). `kind` labels which collection it came from, for the
 * picker's option text; nothing here is a new party type, exactly as the
 * issue asks — it is `creditors` and `codebtors` read through the same
 * generic case-entity listing this screen already uses for everything else.
 */
interface CaseParty {
  readonly id: string;
  readonly kind: 'creditor' | 'codebtor';
  readonly name: string;
  readonly address: Address | undefined;
}

const YES_NO_OPTIONS = [
  { value: 'yes', label: 'Yes' },
  { value: 'no', label: 'No' },
] as const;

/** Which debtor roles a `debtor` reference offers — labels match the tabs. */
const DEBTOR_LABELS: Readonly<Record<string, string>> = {
  debtor_1: 'Debtor 1',
  debtor_2: 'Debtor 2',
  non_filing_spouse: 'Non-filing spouse',
};

function bodyOf(record: Record<string, unknown>): Body {
  const {
    id: _id,
    case_id: _caseId,
    created_at: _created,
    updated_at: _updated,
    provenance: _provenance,
    ...body
  } = record;
  return body;
}

/** Reads a dotted path (`payload.recipient.name`) out of a nested body. */
function getAt(body: Body, path: string): unknown {
  let value: unknown = body;
  for (const segment of path.split('.')) {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) return undefined;
    value = (value as Body)[segment];
  }
  return value;
}

/** Writes a dotted path immutably; `undefined` removes the key. */
function setAt(body: Body, path: string, value: unknown): Body {
  const [head, ...rest] = path.split('.');
  if (head === undefined) return body;
  if (rest.length === 0) {
    if (value === undefined) {
      const { [head]: _removed, ...remaining } = body;
      return remaining;
    }
    return { ...body, [head]: value };
  }
  const current = body[head];
  const nested =
    typeof current === 'object' && current !== null && !Array.isArray(current)
      ? (current as Body)
      : {};
  return { ...body, [head]: setAt(nested, rest.join('.'), value) };
}

const specFor = (collection: CaseCollection): CollectionSpec | undefined =>
  COLLECTION_SPECS.find((candidate) => candidate.collection === collection);

/** The collections a spec's fields reference, for the one-time options load. */
function referencedCollections(spec: CollectionSpec): readonly CaseCollection[] {
  const referenced = new Set<CaseCollection>();
  for (const field of spec.fields({})) {
    if (field.kind === 'reference' || field.kind === 'reference-list') {
      referenced.add(field.refers);
    }
  }
  return [...referenced];
}

function needsDebtors(spec: CollectionSpec): boolean {
  return spec.fields({}).some((field) => field.kind === 'debtor');
}

/**
 * The summaries of every `linkedRows` record whose `linkedFrom.field` names
 * `rowId` — "Codebtors: Jane Doe, John Roe" under a claim or lease (issue
 * #347). Read only: editing the link happens on the codebtor's own record,
 * via its `reference-list` field, not here.
 */
function linkedSummaries(
  spec: CollectionSpec,
  linkedRows: readonly Row[],
  rowId: string,
): readonly string[] {
  const linkedFrom = spec.linkedFrom;
  if (linkedFrom === undefined) return [];
  const linkingSpec = specFor(linkedFrom.collection);
  return linkedRows
    .filter((row) => {
      const ids = row.body[linkedFrom.field];
      return Array.isArray(ids) && ids.includes(rowId);
    })
    .map((row) => linkingSpec?.summary(row.body) ?? row.id);
}

export interface CollectionEditorProps {
  readonly caseId: string;
  readonly spec: CollectionSpec;
  /** Start on a new record's form with this body, rather than on the list. */
  readonly initialForm?: Body | undefined;
  /** Open another collection's section on a new record with this body. */
  readonly onOpenCollection?: ((collection: CaseCollection, body: Body) => void) | undefined;
}

export function CollectionEditor({
  caseId,
  spec,
  initialForm,
  onOpenCollection,
}: CollectionEditorProps) {
  const theme = useTheme();
  const { call } = useApi();

  const [load, setLoad] = useState<LoadState>({ kind: 'loading' });
  const [rows, setRows] = useState<readonly Row[]>([]);
  const [mode, setMode] = useState<Mode>(
    initialForm === undefined
      ? { kind: 'list' }
      : { kind: 'form', id: null, body: initialForm, librarySourceId: null },
  );
  const [status, setStatus] = useState<string>('');
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [saving, setSaving] = useState(false);
  const [references, setReferences] = useState<
    Readonly<Partial<Record<string, readonly ReferenceOption[]>>>
  >({});
  // The records of `spec.linkedFrom.collection` (issue #347) — loaded so each
  // row below can show which of THEM name it, read only. Not `references`:
  // those are options for a picker on THIS spec's own fields, the opposite
  // direction from a backlink.
  const [linkedRows, setLinkedRows] = useState<readonly Row[]>([]);

  // Creditor-library reuse (issue 13.9 / #350) — `creditors` only. Loaded
  // lazily, once, the first time the picker is opened, rather than on every
  // mount: most collections never touch either of these two extra requests.
  const [libraryOptions, setLibraryOptions] = useState<readonly LibraryCreditor[] | null>(null);
  const [caseParties, setCaseParties] = useState<readonly CaseParty[] | null>(null);
  const [addToLibrary, setAddToLibrary] = useState(false);
  const fetchCreditorSources = useCreditorSources(caseId);
  const loadCreditorSources = useCallback(async () => {
    const { library, caseParties: parties } = await fetchCreditorSources();
    setLibraryOptions(library);
    setCaseParties(parties);
  }, [fetchCreditorSources]);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) => client.listCaseEntities(caseId, spec.collection));
        if (!result.ok || cancelled) return;
        setRows(
          result.value.map((record) => ({
            id: record.id,
            body: bodyOf(record as unknown as Record<string, unknown>),
          })),
        );
        setLoad({ kind: 'ready' });

        // Reference options are loaded AFTER the list, not before it: the
        // list is the screen, the options are one form control, and a failed
        // options load should degrade that control rather than the section.
        for (const referenced of referencedCollections(spec)) {
          const listed = await call((client) => client.listCaseEntities(caseId, referenced));
          if (!listed.ok || cancelled) return;
          const referencedSpec = specFor(referenced);
          setReferences((current) => ({
            ...current,
            [referenced]: listed.value.map((record, index) => ({
              value: record.id,
              label: `${referencedSpec ? labelize(referencedSpec.recordName) : 'Record'} ${
                index + 1
              } — ${referencedSpec?.summary(bodyOf(record as unknown as Record<string, unknown>)) ?? record.id}`,
            })),
          }));
        }
        if (needsDebtors(spec)) {
          const debtors = await call((client) => client.listDebtors(caseId));
          if (!debtors.ok || cancelled) return;
          setReferences((current) => ({
            ...current,
            debtors: debtors.value.map((debtor) => ({
              value: debtor.id,
              label: DEBTOR_LABELS[debtor.filing_role] ?? debtor.filing_role,
            })),
          }));
        }

        // A read-only "who links to me" line (issue #347) — loaded after the
        // list too, for the same reason references are: it decorates rows
        // already on screen rather than gating them.
        const linkedFrom = spec.linkedFrom;
        if (linkedFrom !== undefined) {
          const linked = await call((client) =>
            client.listCaseEntities(caseId, linkedFrom.collection),
          );
          if (!linked.ok || cancelled) return;
          setLinkedRows(
            linked.value.map((record) => ({
              id: record.id,
              body: bodyOf(record as unknown as Record<string, unknown>),
            })),
          );
        }
      } catch {
        if (!cancelled) setLoad({ kind: 'error', message: `Could not load ${spec.title}.` });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId, spec]);

  const persist = useCallback(
    async (form: Mode & { readonly kind: 'form' }) => {
      setSaving(true);
      setStatus('Saving…');
      try {
        // Cast once, here: the descriptor-driven form holds an untyped body by
        // construction, while the client's methods are typed for hand-written
        // callers. The server re-validates every field either way (ADR 0001).
        //
        // A LIBRARY PICK GETS `library` PROVENANCE, NAMING THE ENTRY (issue
        // 13.9 / #350) — `librarySourceId` is set only when the fields were
        // just copied verbatim and untouched since; any edit through
        // `FieldControl` clears it, which is what keeps this honest. Every
        // other save keeps the ordinary `staff_typed` rule.
        const request = {
          ...form.body,
          provenance:
            form.librarySourceId === null
              ? staffTypedProvenance(form.body)
              : libraryProvenance(form.body, form.librarySourceId),
        } as unknown as CaseEntityRequest<CaseCollection>;
        const result = await call((client) =>
          form.id === null
            ? client.addCaseEntity(caseId, spec.collection, request)
            : client.putCaseEntity(caseId, spec.collection, form.id, request),
        );
        if (!result.ok) {
          setStatus('');
          return;
        }
        const saved: Row = {
          id: result.value.id,
          body: bodyOf(result.value as unknown as Record<string, unknown>),
        };
        setRows((current) =>
          form.id === null
            ? [...current, saved]
            : current.map((row) => (row.id === saved.id ? saved : row)),
        );

        // "Add to the library" on save (issue 13.9 / #350) — a COPY, the
        // opposite direction of a library pick: the case record is already
        // saved, and this writes a second, independent row to the firm's
        // library from the same values. Best-effort: a failure here must not
        // make the caller think the case creditor itself failed to save, so
        // it is swallowed rather than surfaced through `errors`/`status`.
        if (spec.collection === 'creditors' && addToLibrary) {
          const name = typeof saved.body.name === 'string' ? saved.body.name.trim() : '';
          if (name !== '') {
            try {
              await call((client) =>
                client.addLibraryCreditor({
                  name,
                  address: saved.body.address as Address | undefined,
                }),
              );
            } catch {
              // Best-effort, per above.
            }
          }
        }

        setErrors({});
        setAddToLibrary(false);
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
    },
    [addToLibrary, call, caseId, spec],
  );

  const remove = useCallback(
    async (id: string) => {
      setStatus('Removing…');
      try {
        const result = await call((client) => client.deleteCaseEntity(caseId, spec.collection, id));
        if (!result.ok) {
          setStatus('');
          return;
        }
        setRows((current) => current.filter((row) => row.id !== id));
        setStatus('Removed');
      } catch {
        setStatus('Could not remove it. Try again.');
      }
    },
    [call, caseId, spec],
  );

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  return (
    <View style={styles.editor}>
      <Heading level={2}>{spec.title}</Heading>
      <Text style={[styles.help, muted]}>{spec.help}</Text>

      <Text
        aria-live={load.kind === 'error' ? 'assertive' : 'polite'}
        style={[
          styles.help,
          load.kind === 'error'
            ? { color: theme.colors.danger, fontFamily: theme.typography.body }
            : muted,
        ]}
      >
        {load.kind === 'loading'
          ? `Loading ${spec.title}…`
          : load.kind === 'error'
            ? load.message
            : status}
      </Text>

      {load.kind !== 'ready' ? null : mode.kind === 'list' ? (
        <View style={styles.list}>
          {rows.length === 0 ? (
            <Text style={[styles.help, muted]}>Nothing recorded yet.</Text>
          ) : (
            rows.map((row, index) => {
              const linked = linkedSummaries(spec, linkedRows, row.id);
              const linkingTitle =
                spec.linkedFrom !== undefined
                  ? (specFor(spec.linkedFrom.collection)?.title ?? 'Linked records')
                  : null;
              return (
                <View key={row.id} style={[styles.row, { borderColor: theme.colors.line }]}>
                  <Text
                    style={[
                      styles.rowSummary,
                      { color: theme.colors.ink, fontFamily: theme.typography.body },
                    ]}
                  >
                    {spec.summary(row.body)}
                  </Text>
                  {linkingTitle === null ? null : (
                    <Text style={[styles.help, muted]}>
                      {linked.length > 0
                        ? `${linkingTitle}: ${linked.join(', ')}`
                        : `No ${linkingTitle.toLowerCase()} linked`}
                    </Text>
                  )}
                  <View style={styles.rowActions}>
                    <Button
                      size="lg"
                      intent="secondary"
                      aria-label={`Edit ${spec.recordName} ${index + 1}`}
                      onPress={() => {
                        setErrors({});
                        setStatus('');
                        setAddToLibrary(false);
                        setMode({
                          kind: 'form',
                          id: row.id,
                          body: row.body,
                          librarySourceId: null,
                        });
                      }}
                    >
                      Edit
                    </Button>
                    <Button
                      size="lg"
                      intent="secondary"
                      aria-label={`Remove ${spec.recordName} ${index + 1}`}
                      onPress={() => void remove(row.id)}
                    >
                      Remove
                    </Button>
                  </View>
                </View>
              );
            })
          )}
          <Button
            size="lg"
            onPress={() => {
              setErrors({});
              setStatus('');
              setAddToLibrary(false);
              setMode({ kind: 'form', id: null, body: {}, librarySourceId: null });
              if (spec.collection === 'creditors') {
                void loadCreditorSources();
              }
            }}
          >
            {`Add ${spec.recordName}`}
          </Button>
        </View>
      ) : (
        <View style={styles.form}>
          {spec.collection === 'creditors' && mode.id === null ? (
            <CreditorSourcePicker
              libraryOptions={libraryOptions}
              caseParties={caseParties}
              onPickLibrary={(creditor) => {
                setMode((current) =>
                  current.kind === 'form'
                    ? {
                        ...current,
                        body: { ...current.body, name: creditor.name, address: creditor.address },
                        librarySourceId: creditor.id,
                      }
                    : current,
                );
              }}
              onPickCaseParty={(party) => {
                setMode((current) =>
                  current.kind === 'form'
                    ? {
                        ...current,
                        body: { ...current.body, name: party.name, address: party.address ?? {} },
                        librarySourceId: null,
                      }
                    : current,
                );
              }}
            />
          ) : null}
          {spec.fields(mode.body).map((field) => (
            <FieldControl
              key={field.key}
              field={field}
              body={mode.body}
              references={references}
              errors={errors}
              onChange={(next) => setMode({ ...mode, body: next, librarySourceId: null })}
            />
          ))}
          {spec.collection === 'claims' ? (
            <ClaimCollateralPanel caseId={caseId} claimId={mode.id} />
          ) : null}
          {spec.collection === 'assets' ? (
            <AssetLiensPanel
              caseId={caseId}
              assetId={mode.id}
              onAddSecuredClaim={(body) => onOpenCollection?.('claims', body)}
            />
          ) : null}
          {spec.collection === 'creditors' ? (
            <View style={styles.checkboxRow}>
              <Checkbox.Root
                aria-label="Add to your firm's creditor library"
                checked={addToLibrary}
                onCheckedChange={setAddToLibrary}
              >
                <Checkbox.Indicator>✓</Checkbox.Indicator>
              </Checkbox.Root>
              <Text
                style={[
                  styles.checkboxLabel,
                  { color: theme.colors.ink, fontFamily: theme.typography.body },
                ]}
              >
                Also save this creditor to your firm's library, for reuse on other cases
              </Text>
            </View>
          ) : null}
          <View style={styles.rowActions}>
            <Button size="lg" disabled={saving} onPress={() => void persist(mode)}>
              {mode.id === null ? `Save ${spec.recordName}` : 'Save changes'}
            </Button>
            <Button
              size="lg"
              intent="secondary"
              onPress={() => {
                setErrors({});
                setStatus('');
                setAddToLibrary(false);
                setMode({ kind: 'list' });
              }}
            >
              Cancel
            </Button>
          </View>
        </View>
      )}
    </View>
  );
}

/**
 * Where the "Add {recordName}" flow's `creditors`-only picker sources its two
 * lists from (issue 13.9 / #350): the firm's whole library
 * (`listLibraryCreditors`) and the parties already on this case — its other
 * creditors and its codebtors, both already generic case-entity listings.
 */
function partyOf(
  record: { readonly id: string },
  body: Body,
  kind: CaseParty['kind'],
): CaseParty | null {
  const name = typeof body.name === 'string' ? body.name : undefined;
  if (name === undefined) return null;
  return { id: record.id, kind, name, address: body.address as Address | undefined };
}

function useCreditorSources(caseId: string) {
  const { call } = useApi();
  return useCallback(async (): Promise<{
    readonly library: readonly LibraryCreditor[];
    readonly caseParties: readonly CaseParty[];
  }> => {
    const [libraryResult, creditorsResult, codebtorsResult] = await Promise.all([
      call((client) => client.listLibraryCreditors()),
      call((client) => client.listCaseEntities(caseId, 'creditors')),
      call((client) => client.listCaseEntities(caseId, 'codebtors')),
    ]);
    const library = libraryResult.ok ? libraryResult.value : [];
    const creditorParties: CaseParty[] = creditorsResult.ok
      ? creditorsResult.value.flatMap((record) => {
          const body = bodyOf(record as unknown as Record<string, unknown>);
          const party = partyOf(record, body, 'creditor');
          return party === null ? [] : [party];
        })
      : [];
    const codebtorParties: CaseParty[] = codebtorsResult.ok
      ? codebtorsResult.value.flatMap((record) => {
          const body = bodyOf(record as unknown as Record<string, unknown>);
          const party = partyOf(record, body, 'codebtor');
          return party === null ? [] : [party];
        })
      : [];
    return { library, caseParties: [...creditorParties, ...codebtorParties] };
  }, [call, caseId]);
}

/**
 * "Copy from your library" and "copy from this case", above the ordinary
 * name/address fields on a NEW creditor (issue 13.9 / #350). Picking either
 * fills the fields but leaves them editable — a pick is a starting point, not
 * a lock — and `onPickLibrary` is the one that also records the copy's
 * provenance source (see `persist` in the parent).
 *
 * Two `Select`s rather than one combined list: the library and the case's own
 * parties are different kinds of thing (the firm's standing data vs. this
 * case's own records) and a firm curating its library wants that told apart,
 * not merged into one alphabetized pile.
 */
function CreditorSourcePicker({
  libraryOptions,
  caseParties,
  onPickLibrary,
  onPickCaseParty,
}: {
  libraryOptions: readonly LibraryCreditor[] | null;
  caseParties: readonly CaseParty[] | null;
  onPickLibrary: (creditor: LibraryCreditor) => void;
  onPickCaseParty: (party: CaseParty) => void;
}) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  if (libraryOptions === null && caseParties === null) {
    return null;
  }

  return (
    <View style={styles.group}>
      <Text style={[styles.help, muted]}>
        Start from a creditor already on file, or fill in the fields below by hand.
      </Text>
      {libraryOptions !== null && libraryOptions.length > 0 ? (
        <Select
          aria-label="Copy from your firm's creditor library"
          placeholder="Copy from your library…"
          options={libraryOptions.map((creditor) => ({
            value: creditor.id,
            label: creditor.preferred ? `${creditor.name} (preferred)` : creditor.name,
          }))}
          onValueChange={(value) => {
            const picked = libraryOptions.find((creditor) => creditor.id === value);
            if (picked) onPickLibrary(picked);
          }}
        />
      ) : null}
      {caseParties !== null && caseParties.length > 0 ? (
        <Select
          aria-label="Copy from a party already on this case"
          placeholder="Copy from this case…"
          options={caseParties.map((party) => ({
            value: party.id,
            label: `${party.name} (${party.kind})`,
          }))}
          onValueChange={(value) => {
            const picked = caseParties.find((party) => party.id === value);
            if (picked) onPickCaseParty(picked);
          }}
        />
      ) : null}
    </View>
  );
}

function FieldControl({
  field,
  body,
  references,
  errors,
  onChange,
}: {
  field: FieldSpec;
  body: Body;
  references: Readonly<Partial<Record<string, readonly ReferenceOption[]>>>;
  errors: Readonly<Record<string, string>>;
  onChange: (next: Body) => void;
}) {
  const theme = useTheme();
  const value = getAt(body, field.key);
  const message = errors[field.key];
  const set = (next: unknown) => onChange(setAt(body, field.key, next));
  const setText = (next: string) => set(next === '' ? undefined : next);

  switch (field.kind) {
    case 'text':
    case 'money':
      return (
        <Field.Root invalid={Boolean(message)}>
          <Field.Label>{field.label}</Field.Label>
          <Input
            value={typeof value === 'string' ? value : ''}
            onValueChange={setText}
            autoCorrect={false}
          />
          {field.kind === 'money' ? (
            <Field.Description>Dollars, like 1200.00.</Field.Description>
          ) : null}
          {message ? <Field.Error match>{message}</Field.Error> : null}
        </Field.Root>
      );
    case 'narrative':
      return (
        <Field.Root invalid={Boolean(message)}>
          <Field.Label>{field.label}</Field.Label>
          <Textarea value={typeof value === 'string' ? value : ''} onValueChange={setText} />
          {message ? <Field.Error match>{message}</Field.Error> : null}
        </Field.Root>
      );
    case 'date':
      return (
        <Field.Root invalid={Boolean(message)}>
          <Field.Label>{field.label}</Field.Label>
          <DateInput
            value={typeof value === 'string' ? value : ''}
            onValueChange={(next, status) => {
              // A half-typed date reports '' with status 'incomplete'; writing
              // that through would clear a saved date on the first backspace.
              if (status === 'incomplete') return;
              setText(next);
            }}
          />
          {message ? <Field.Error match>{message}</Field.Error> : null}
        </Field.Root>
      );
    case 'count':
      return (
        <Field.Root invalid={Boolean(message)}>
          <Field.Label>{field.label}</Field.Label>
          <Input
            value={typeof value === 'number' ? String(value) : ''}
            onValueChange={(next) => {
              const digits = next.replaceAll(/[^0-9]/gu, '');
              set(digits === '' ? undefined : Number(digits));
            }}
            autoCorrect={false}
          />
          {message ? <Field.Error match>{message}</Field.Error> : null}
        </Field.Root>
      );
    case 'boolean':
      return (
        <Field.Root invalid={Boolean(message)}>
          <Field.Label>{field.label}</Field.Label>
          <Select
            options={YES_NO_OPTIONS}
            value={value === true ? 'yes' : value === false ? 'no' : null}
            onValueChange={(next) => set(next === 'yes' ? true : next === 'no' ? false : undefined)}
            placeholder="Not answered"
          />
          {message ? <Field.Error match>{message}</Field.Error> : null}
        </Field.Root>
      );
    case 'choice':
      return (
        <ChoiceField
          label={field.label}
          options={field.options}
          value={typeof value === 'string' ? value : null}
          onValueChange={(next, allowed) =>
            set(allowed.some((option) => option.value === next) ? next : undefined)
          }
          message={message}
        />
      );
    case 'reference':
    case 'debtor': {
      const optionsKey = field.kind === 'debtor' ? 'debtors' : field.refers;
      const options = references[optionsKey] ?? [];
      if (options.length === 0) {
        // Nothing to point at yet. An empty picker cannot be operated, so say
        // what to do instead of rendering one.
        return (
          <Field.Root invalid={Boolean(message)}>
            <Field.Label>{field.label}</Field.Label>
            <Field.Description>
              {field.kind === 'debtor'
                ? 'Fill in the debtor under “About the debtor” first.'
                : 'Add the record it refers to first — this can be left empty for now.'}
            </Field.Description>
            {message ? <Field.Error match>{message}</Field.Error> : null}
          </Field.Root>
        );
      }
      return (
        <ChoiceField
          label={field.label}
          options={options}
          value={typeof value === 'string' ? value : null}
          onValueChange={(next, allowed) =>
            set(allowed.some((option) => option.value === next) ? next : undefined)
          }
          message={message}
        />
      );
    }
    case 'multichoice':
    case 'reference-list': {
      const options =
        field.kind === 'multichoice' ? field.options : (references[field.refers] ?? []);
      const chosen = Array.isArray(value)
        ? value.filter((v): v is string => typeof v === 'string')
        : [];
      // Server messages for a list are keyed per element (`lien_nature[0]`) or
      // whole; either way they belong under this group.
      const groupMessage =
        message ?? Object.entries(errors).find(([path]) => path.startsWith(`${field.key}[`))?.[1];
      return (
        <Field.Root invalid={Boolean(groupMessage)}>
          <Field.Label>{field.label}</Field.Label>
          <CheckboxGroup.Root
            value={[...chosen]}
            onValueChange={(next) => set(next.length === 0 ? undefined : next)}
          >
            {options.map((option) => (
              <View key={option.value} style={styles.checkboxRow}>
                <Checkbox.Root value={option.value} aria-label={option.label}>
                  <Checkbox.Indicator>✓</Checkbox.Indicator>
                </Checkbox.Root>
                <Text
                  aria-hidden
                  style={[
                    styles.checkboxLabel,
                    { color: theme.colors.ink, fontFamily: theme.typography.body },
                  ]}
                >
                  {option.label}
                </Text>
              </View>
            ))}
          </CheckboxGroup.Root>
          {groupMessage ? <Field.Error match>{groupMessage}</Field.Error> : null}
        </Field.Root>
      );
    }
    case 'address':
      return (
        <AddressGroup
          label={field.label}
          basePath={field.key}
          body={body}
          errors={errors}
          onChange={onChange}
        />
      );
    case 'party':
      return (
        <View style={styles.group}>
          <TextAt
            label={`${field.label} — name`}
            path={`${field.key}.name`}
            body={body}
            errors={errors}
            onChange={onChange}
          />
          <AddressGroup
            label={`${field.label} — address`}
            basePath={`${field.key}.address`}
            body={body}
            errors={errors}
            onChange={onChange}
          />
        </View>
      );
    case 'party-list': {
      // The id-keyed object list (issue #280). Each row is a Body of its own,
      // so TextAt/AddressGroup work on the ROW with row-relative paths; the
      // API requires a client-minted `id` per row (core/claims.py), minted on
      // Add so provenance can address `<key>[<id>].name` before the save.
      const rows = Array.isArray(value)
        ? value.filter(
            (row): row is Body => typeof row === 'object' && row !== null && !Array.isArray(row),
          )
        : [];
      const setRows = (next: readonly Body[]) => set(next.length === 0 ? undefined : [...next]);
      // Server messages are keyed by POSITION (`notice_parties[0].name`);
      // rescope each row's slice to row-relative paths so the row's own
      // controls can find them.
      const rowErrors = (index: number): Readonly<Record<string, string>> => {
        const prefix = `${field.key}[${index}].`;
        return Object.fromEntries(
          Object.entries(errors)
            .filter(([path]) => path.startsWith(prefix))
            .map(([path, text]) => [path.slice(prefix.length), text]),
        );
      };
      const listMessage = message;
      return (
        <View style={styles.group}>
          {rows.map((row, index) => {
            const scoped = rowErrors(index);
            const rowLabel = `${labelize(field.itemLabel)} ${index + 1}`;
            const wholeRowMessage = errors[`${field.key}[${index}]`];
            return (
              <View key={typeof row.id === 'string' ? row.id : index} style={styles.group}>
                <TextAt
                  label={`${rowLabel} — name`}
                  path="name"
                  body={row}
                  errors={scoped}
                  onChange={(next) =>
                    setRows(rows.map((current, at) => (at === index ? next : current)))
                  }
                />
                <AddressGroup
                  label={`${rowLabel} — address`}
                  basePath="address"
                  body={row}
                  errors={scoped}
                  onChange={(next) =>
                    setRows(rows.map((current, at) => (at === index ? next : current)))
                  }
                />
                <Field.Root invalid={Boolean(scoped.account_last4)}>
                  <Field.Label>{`${rowLabel} — account number, last four digits`}</Field.Label>
                  <Input
                    value={typeof row.account_last4 === 'string' ? row.account_last4 : ''}
                    onValueChange={(next) =>
                      setRows(
                        rows.map((current, at) =>
                          at === index
                            ? setAt(current, 'account_last4', next === '' ? undefined : next)
                            : current,
                        ),
                      )
                    }
                    autoCorrect={false}
                  />
                  {scoped.account_last4 ? (
                    <Field.Error match>{scoped.account_last4}</Field.Error>
                  ) : null}
                </Field.Root>
                {wholeRowMessage ? (
                  <Text
                    style={[
                      styles.help,
                      { color: theme.colors.danger, fontFamily: theme.typography.body },
                    ]}
                  >
                    {wholeRowMessage}
                  </Text>
                ) : null}
                <Button
                  size="lg"
                  intent="secondary"
                  onPress={() => setRows(rows.filter((_, at) => at !== index))}
                >
                  {`Remove ${field.itemLabel} ${index + 1}`}
                </Button>
              </View>
            );
          })}
          {listMessage ? (
            <Text
              style={[
                styles.help,
                { color: theme.colors.danger, fontFamily: theme.typography.body },
              ]}
            >
              {listMessage}
            </Text>
          ) : null}
          <Button
            size="lg"
            intent="secondary"
            onPress={() => setRows([...rows, { id: newRowId() }])}
          >
            {`Add ${field.itemLabel}`}
          </Button>
        </View>
      );
    }
    case 'strings':
    case 'dates': {
      const items = Array.isArray(value)
        ? value.filter((item): item is string => typeof item === 'string')
        : [];
      const setItems = (next: readonly string[]) => set(next.length === 0 ? undefined : [...next]);
      return (
        <View style={styles.group}>
          {items.map((item, index) => (
            // Index keys are fine here: rows are only appended and removed by
            // their button, and the list re-renders whole from the body.
            <View key={index} style={styles.listItemRow}>
              <Field.Root invalid={Boolean(errors[`${field.key}[${index}]`])}>
                <Field.Label>{`${field.kind === 'dates' ? 'Date' : ((field as { itemLabel?: string }).itemLabel ?? 'Item')} ${index + 1}`}</Field.Label>
                {field.kind === 'dates' ? (
                  <DateInput
                    value={item}
                    onValueChange={(next, status) => {
                      if (status === 'incomplete') return;
                      setItems(items.map((current, at) => (at === index ? next : current)));
                    }}
                  />
                ) : (
                  <Input
                    value={item}
                    onValueChange={(next) =>
                      setItems(items.map((current, at) => (at === index ? next : current)))
                    }
                    autoCorrect={false}
                  />
                )}
                {errors[`${field.key}[${index}]`] ? (
                  <Field.Error match>{errors[`${field.key}[${index}]`]}</Field.Error>
                ) : null}
              </Field.Root>
              <Button
                size="lg"
                intent="secondary"
                onPress={() => setItems(items.filter((_, at) => at !== index))}
              >
                {`Remove ${field.label.toLowerCase()} ${index + 1}`}
              </Button>
            </View>
          ))}
          <Button size="lg" intent="secondary" onPress={() => setItems([...items, ''])}>
            {`Add ${field.label.toLowerCase()}`}
          </Button>
        </View>
      );
    }
  }
}

function ChoiceField({
  label,
  options,
  value,
  onValueChange,
  message,
}: {
  label: string;
  options: readonly ChoiceOption[];
  value: string | null;
  onValueChange: (next: string, allowed: readonly ChoiceOption[]) => void;
  message: string | undefined;
}) {
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{label}</Field.Label>
      <Select
        options={[...options]}
        value={value}
        onValueChange={(next) => onValueChange(next, options)}
        placeholder="Choose one"
      />
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

function TextAt({
  label,
  path,
  body,
  errors,
  onChange,
}: {
  label: string;
  path: string;
  body: Body;
  errors: Readonly<Record<string, string>>;
  onChange: (next: Body) => void;
}) {
  const value = getAt(body, path);
  const message = errors[path];
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{label}</Field.Label>
      <Input
        value={typeof value === 'string' ? value : ''}
        onValueChange={(next) => onChange(setAt(body, path, next === '' ? undefined : next))}
        autoCorrect={false}
      />
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

function AddressGroup({
  label,
  basePath,
  body,
  errors,
  onChange,
}: {
  label: string;
  basePath: string;
  body: Body;
  errors: Readonly<Record<string, string>>;
  onChange: (next: Body) => void;
}) {
  const parts: readonly (readonly [string, string])[] = [
    ['line1', 'Street'],
    ['line2', 'Apartment, suite or unit'],
    ['city', 'City'],
    ['state', 'State'],
    ['postal_code', 'ZIP code'],
  ];
  return (
    <View style={styles.group}>
      {parts.map(([part, partLabel]) => (
        <TextAt
          key={part}
          label={`${label} — ${partLabel}`}
          path={`${basePath}.${part}`}
          body={body}
          errors={errors}
          onChange={onChange}
        />
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  checkboxLabel: { fontSize: fontSizes.body },
  checkboxRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
  editor: { gap: spacing.md },
  form: { gap: spacing.md },
  group: { gap: spacing.sm },
  help: { fontSize: fontSizes.label },
  list: { gap: spacing.md },
  listItemRow: { gap: spacing.sm },
  row: { borderBottomWidth: 1, gap: spacing.sm, paddingBottom: spacing.sm },
  rowActions: { flexDirection: 'row', gap: spacing.sm },
  rowSummary: { fontSize: fontSizes.body },
});
