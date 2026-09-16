import {
  ApiValidationException,
  ASSET_CATEGORIES,
  DEBTOR_ATTRIBUTION,
  PROPERTY_TYPES,
  staffTypedProvenance,
} from '@insolvia-ai/api-client';
import type {
  AssetCategory,
  CaseCollection,
  CaseEntityRequest,
  CaseTotals,
} from '@insolvia-ai/api-client';
import {
  Button,
  Checkbox,
  CheckboxGroup,
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

import { bodyOf, getAt, setAt } from './collection-editor';
import type { ChoiceOption } from './collections';
import { labelize } from './collections';
import { AssetLiensPanel, formatMoney } from './liens';

/**
 * The Schedule A/B entry screen (issue 13.3 / #344) — the first screen whose
 * field set changes with a selection, so it is its own component rather
 * than a `CollectionSpec` the generic `CollectionEditor` can drive: no flat
 * field list can express "a vehicle asks for year/make/model/mileage, real
 * estate asks for county and property type, a deposit asks for an
 * institution name". `collections.ts` keeps a plain `assets` entry (title,
 * help, `summary`) for what every OTHER section still needs from it — the
 * claims section's collateral picker, and the intake screen's section list —
 * this file owns only the list-and-form UI `CollectionEditor` used to.
 *
 * SAVES ARE EXPLICIT, same rule as every other collection (see
 * `collection-editor.tsx`'s header comment) — this is a list of discrete
 * records, not one continuous one, so autosaving a half-typed asset would
 * create it from two keystrokes.
 *
 * THE ONE REQUIRED FIELD is the category's identifier — an address, a
 * make/model, or a free description, per `identifierFor` below — because a
 * property with nothing identifying it is not a usable row even under
 * progressive intake. Every other field, including the category itself
 * being unset, still saves: the server's shape-only validation (ADR 0001)
 * does not know about this rule, so it is enforced here, client-side, with a
 * message naming the field, exactly as the issue asks.
 */

type Body = Record<string, unknown>;

type Mode =
  | { readonly kind: 'list' }
  | { readonly kind: 'form'; readonly id: string | null; readonly body: Body };

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready' }
  | { readonly kind: 'error'; readonly message: string };

interface Row {
  readonly id: string;
  readonly body: Body;
}

const YES_NO_OPTIONS = [
  { value: 'yes', label: 'Yes' },
  { value: 'no', label: 'No' },
] as const;

/** Which AssetBody field answers "what is this" for a given category, and
 * the label the form uses for it — the one field `persist` requires. */
interface CategoryIdentifier {
  readonly attr: 'description' | 'detail' | 'make_model';
  readonly label: string;
}

const REAL_ESTATE_IDENTIFIER: CategoryIdentifier = {
  attr: 'description',
  label: 'Street address (or other description)',
};
const VEHICLE_IDENTIFIER: CategoryIdentifier = { attr: 'make_model', label: 'Make or model' };
const DEFAULT_IDENTIFIER: CategoryIdentifier = {
  attr: 'description',
  label: 'Describe the property',
};

/**
 * The named-row and keyword categories (`form_projections/b106ab.py`'s
 * `_ROW_LINES`/`_keyword_lines` tables) print a specific institution, issuer
 * or entity name as their identifying box — `detail` for most, `description`
 * for a few whose box is titled "name of entity"/"issuer"/"company". Mirrors
 * that table so this screen asks for the same fact the printed form does.
 */
const NAMED_ROW_IDENTIFIERS: Partial<Record<AssetCategory, CategoryIdentifier>> = {
  deposits_of_money: { attr: 'detail', label: 'Financial institution' },
  bonds_and_mutual_funds: { attr: 'detail', label: 'Financial institution' },
  non_publicly_traded_stock_and_business_interests: {
    attr: 'description',
    label: 'Name of the entity',
  },
  government_and_corporate_bonds: { attr: 'detail', label: 'Issuer name' },
  retirement_accounts: { attr: 'detail', label: 'Institution name' },
  security_deposits_and_prepayments: { attr: 'detail', label: 'Institution name' },
  annuities: { attr: 'description', label: 'Issuer name' },
  education_accounts: { attr: 'description', label: 'Institution name' },
  insurance_policy_interests: { attr: 'description', label: 'Insurance company' },
  partnership_and_joint_venture_interests: { attr: 'description', label: 'Name of the entity' },
  other_business_property: { attr: 'description', label: 'Describe the property' },
  customer_lists_and_intangibles: {
    attr: 'description',
    label: 'Describe the lists or intangibles',
  },
  money_owed_to_you: { attr: 'description', label: "Describe what's owed to you" },
  family_support_owed: { attr: 'description', label: 'Describe the support owed' },
};

function identifierFor(category: string | undefined): CategoryIdentifier {
  if (category === 'real_property') return REAL_ESTATE_IDENTIFIER;
  if (category === 'vehicle' || category === 'watercraft_aircraft_or_recreational_vehicle') {
    return VEHICLE_IDENTIFIER;
  }
  return NAMED_ROW_IDENTIFIERS[category as AssetCategory] ?? DEFAULT_IDENTIFIER;
}

/** Whether `body` already answers `identifier` — the one thing `persist`
 * blocks an empty save over. */
function identifierIsFilled(body: Body, identifier: CategoryIdentifier): boolean {
  if (identifier.attr === 'make_model') {
    const make = typeof body.make === 'string' ? body.make.trim() : '';
    const model = typeof body.model === 'string' ? body.model.trim() : '';
    return make !== '' || model !== '';
  }
  const value = body[identifier.attr];
  return typeof value === 'string' && value.trim() !== '';
}

/** Line 28/29's amount columns route by keyword over `detail` — the spec's
 * own note (`form_projections/b106ab.py`). Offering these as a choice
 * instead of free text is what keeps a save from landing on a row the form
 * then refuses to project ("names none of them"). */
const KEYWORD_OPTIONS: Partial<Record<AssetCategory, readonly ChoiceOption[]>> = {
  money_owed_to_you: [
    { value: 'federal', label: 'Federal tax refund' },
    { value: 'state', label: 'State tax refund' },
    { value: 'local', label: 'Local tax refund' },
  ],
  family_support_owed: [
    { value: 'property settlement', label: 'Property settlement' },
    { value: 'divorce', label: 'Divorce settlement' },
    { value: 'alimony', label: 'Alimony' },
    { value: 'maintenance', label: 'Maintenance' },
    { value: 'support', label: 'Support' },
  ],
};

/** The categories whose printed row shares Ownership / community property /
 * both value boxes — Parts 1-2 (`_shared_columns` in `b106ab.py`). Every
 * other part prints one amount only, so asking for these there would invite
 * an answer the form never shows. */
const SHARED_COLUMN_CATEGORIES: ReadonlySet<string> = new Set([
  'real_property',
  'vehicle',
  'watercraft_aircraft_or_recreational_vehicle',
]);

/** `real_property` → "Part 1", `vehicle` → "Part 2", … — grouping the
 * category picker by the schedule's own parts (issue 13.3 / #344). */
const PART_TITLES: readonly string[] = [
  'Part 1 — Real property',
  'Part 2 — Vehicles',
  'Part 3 — Personal and household items',
  'Part 4 — Financial assets',
  'Part 5 — Business-related property',
  'Part 6 — Farm- and fishing-related property',
  'Part 7 — Other property',
];
const PART_BOUNDARIES: readonly AssetCategory[] = [
  'real_property',
  'vehicle',
  'household_goods',
  'cash',
  'accounts_receivable',
  'farm_animals',
  'other_property_not_listed',
];

function categoryOptions(): readonly ChoiceOption[] {
  let part = 0;
  return ASSET_CATEGORIES.map((category) => {
    const boundary = PART_BOUNDARIES.indexOf(category);
    if (boundary !== -1) part = boundary;
    return { value: category, label: `${PART_TITLES[part]} — ${labelize(category)}` };
  });
}
// Computed once — the category set and its parts never change at runtime.
const CATEGORY_OPTIONS = categoryOptions();

export interface AssetsEditorProps {
  readonly caseId: string;
  /** Start on a new record's form with this body, rather than on the list —
   * unused today (nothing hands off TO assets yet) but kept symmetrical
   * with `CollectionEditor`'s prop of the same name. */
  readonly initialForm?: Body | undefined;
  /** Open the claims section on a new secured claim already pointed at this
   * property — `AssetLiensPanel`'s "add a secured claim" action. */
  readonly onOpenCollection?: ((collection: CaseCollection, body: Body) => void) | undefined;
}

export function AssetsEditor({ caseId, initialForm, onOpenCollection }: AssetsEditorProps) {
  const theme = useTheme();
  const { call } = useApi();

  const [load, setLoad] = useState<LoadState>({ kind: 'loading' });
  const [rows, setRows] = useState<readonly Row[]>([]);
  const [mode, setMode] = useState<Mode>(
    initialForm === undefined ? { kind: 'list' } : { kind: 'form', id: null, body: initialForm },
  );
  const [status, setStatus] = useState('');
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [identifierMessage, setIdentifierMessage] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [totals, setTotals] = useState<TotalsState>({ kind: 'loading' });

  const loadTotals = useCallback(async () => {
    try {
      const result = await call((client) => client.getCaseSummary(caseId));
      if (result.ok) setTotals({ kind: 'ready', totals: result.value.totals });
    } catch {
      setTotals({ kind: 'error' });
    }
  }, [call, caseId]);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) => client.listCaseEntities(caseId, 'assets'));
        if (!result.ok || cancelled) return;
        setRows(
          result.value.map((record) => ({
            id: record.id,
            body: bodyOf(record as unknown as Record<string, unknown>),
          })),
        );
        setLoad({ kind: 'ready' });
      } catch {
        if (!cancelled) setLoad({ kind: 'error', message: 'Could not load Property.' });
      }
    })();
    void loadTotals();
    return () => {
      cancelled = true;
    };
  }, [call, caseId, loadTotals]);

  const persist = useCallback(
    async (id: string | null, body: Body) => {
      const identifier = identifierFor(
        typeof body.category === 'string' ? body.category : undefined,
      );
      if (typeof body.category !== 'string' || body.category === '') {
        setIdentifierMessage('Choose a category before saving.');
        return;
      }
      if (!identifierIsFilled(body, identifier)) {
        setIdentifierMessage(`Enter ${identifier.label.toLowerCase()} before saving.`);
        return;
      }
      setIdentifierMessage(null);
      setSaving(true);
      setStatus('Saving…');
      try {
        const request = {
          ...body,
          provenance: staffTypedProvenance(body),
        } as unknown as CaseEntityRequest<'assets'>;
        const result = await call((client) =>
          id === null
            ? client.addCaseEntity(caseId, 'assets', request)
            : client.putCaseEntity(caseId, 'assets', id, request),
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
          id === null
            ? [...current, saved]
            : current.map((row) => (row.id === saved.id ? saved : row)),
        );
        setErrors({});
        setStatus('Saved');
        setMode({ kind: 'list' });
        // Recomputed on every save (issue 13.3 / #344) — the rail is only
        // ever as current as the last write, and a save is exactly when it
        // last changed.
        void loadTotals();
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
    [call, caseId, loadTotals],
  );

  const remove = useCallback(
    async (id: string) => {
      setStatus('Removing…');
      try {
        const result = await call((client) => client.deleteCaseEntity(caseId, 'assets', id));
        if (!result.ok) {
          setStatus('');
          return;
        }
        setRows((current) => current.filter((row) => row.id !== id));
        setStatus('Removed');
        void loadTotals();
      } catch {
        setStatus('Could not remove it. Try again.');
      }
    },
    [call, caseId, loadTotals],
  );

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  return (
    <View style={styles.editor}>
      <Heading level={2}>Property</Heading>
      <Text style={[styles.help, muted]}>
        One entry per item of property — every part of Schedule A/B. Choosing a category shows the
        fields that line of the form actually asks for.
      </Text>

      <TotalsRail totals={totals} />

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
          ? 'Loading Property…'
          : load.kind === 'error'
            ? load.message
            : status}
      </Text>

      {load.kind !== 'ready' ? null : mode.kind === 'list' ? (
        <View style={styles.list}>
          {rows.length === 0 ? (
            <Text style={[styles.help, muted]}>Nothing recorded yet.</Text>
          ) : (
            rows.map((row, index) => (
              <View key={row.id} style={[styles.row, { borderColor: theme.colors.line }]}>
                <Text
                  style={[
                    styles.rowSummary,
                    { color: theme.colors.ink, fontFamily: theme.typography.body },
                  ]}
                >
                  {assetSummary(row.body)}
                </Text>
                <View style={styles.rowActions}>
                  <Button
                    size="lg"
                    intent="secondary"
                    aria-label={`Edit asset ${index + 1}`}
                    onPress={() => {
                      setErrors({});
                      setIdentifierMessage(null);
                      setStatus('');
                      setMode({ kind: 'form', id: row.id, body: row.body });
                    }}
                  >
                    Edit
                  </Button>
                  <Button
                    size="lg"
                    intent="secondary"
                    aria-label={`Remove asset ${index + 1}`}
                    onPress={() => void remove(row.id)}
                  >
                    Remove
                  </Button>
                </View>
              </View>
            ))
          )}
          <Button
            size="lg"
            onPress={() => {
              setErrors({});
              setIdentifierMessage(null);
              setStatus('');
              setMode({ kind: 'form', id: null, body: {} });
            }}
          >
            Add asset
          </Button>
        </View>
      ) : (
        <AssetForm
          caseId={caseId}
          body={mode.body}
          errors={errors}
          identifierMessage={identifierMessage}
          saving={saving}
          onChange={(next) => setMode({ ...mode, body: next })}
          onSave={() => void persist(mode.id, mode.body)}
          onCancel={() => {
            setErrors({});
            setIdentifierMessage(null);
            setStatus('');
            setMode({ kind: 'list' });
          }}
          assetId={mode.id}
          onAddSecuredClaim={(body) => onOpenCollection?.('claims', body)}
        />
      )}
    </View>
  );
}

/** One line identifying a record, the same rule `collections.ts`'s
 * `assets` spec's `summary` uses. */
function assetSummary(body: Body): string {
  const category = typeof body.category === 'string' ? body.category : undefined;
  const description = typeof body.description === 'string' ? body.description : undefined;
  const make = typeof body.make === 'string' ? body.make : undefined;
  const model = typeof body.model === 'string' ? body.model : undefined;
  const makeModel = [make, model].filter((part): part is string => part !== undefined).join(' ');
  return (
    description ??
    (makeModel !== '' ? makeModel : undefined) ??
    (category !== undefined ? labelize(category) : 'New asset')
  );
}

function AssetForm({
  caseId,
  assetId,
  body,
  errors,
  identifierMessage,
  saving,
  onChange,
  onSave,
  onCancel,
  onAddSecuredClaim,
}: {
  readonly caseId: string;
  readonly assetId: string | null;
  readonly body: Body;
  readonly errors: Readonly<Record<string, string>>;
  readonly identifierMessage: string | null;
  readonly saving: boolean;
  readonly onChange: (next: Body) => void;
  readonly onSave: () => void;
  readonly onCancel: () => void;
  readonly onAddSecuredClaim: (body: Body) => void;
}) {
  const theme = useTheme();
  const category = typeof body.category === 'string' ? body.category : undefined;
  const identifier = identifierFor(category);
  const showSharedColumns = category !== undefined && SHARED_COLUMN_CATEGORIES.has(category);
  const isVehicle =
    category === 'vehicle' || category === 'watercraft_aircraft_or_recreational_vehicle';
  const keywordOptions =
    category === undefined ? undefined : KEYWORD_OPTIONS[category as AssetCategory];

  return (
    <View style={styles.form}>
      <SelectField
        label="Category"
        options={CATEGORY_OPTIONS}
        value={category ?? null}
        onValueChange={(next) => onChange(setAt(body, 'category', next === '' ? undefined : next))}
        message={errors.category}
      />

      {category === 'real_property' ? (
        <>
          <TextField
            label={identifier.label}
            path="description"
            body={body}
            errors={errors}
            onChange={onChange}
            required
          />
          <TextField label="County" path="county" body={body} errors={errors} onChange={onChange} />
          <MultichoiceField
            label="What is the property?"
            path="property_types"
            options={PROPERTY_TYPES.map((value) => ({ value, label: labelize(value) }))}
            body={body}
            errors={errors}
            onChange={onChange}
          />
          <TextField
            label="Nature of the ownership interest"
            path="ownership_interest_description"
            body={body}
            errors={errors}
            onChange={onChange}
          />
        </>
      ) : isVehicle ? (
        <>
          <View style={styles.group}>
            <TextField label="Year" path="year" body={body} errors={errors} onChange={onChange} />
            <TextField
              label="Make"
              path="make"
              body={body}
              errors={errors}
              onChange={onChange}
              required
            />
            <TextField
              label="Model"
              path="model"
              body={body}
              errors={errors}
              onChange={onChange}
              required
            />
            {category === 'vehicle' ? (
              <TextField
                label="Mileage"
                path="mileage"
                body={body}
                errors={errors}
                onChange={onChange}
              />
            ) : null}
          </View>
          <NarrativeField
            label="Other information"
            path="detail"
            body={body}
            errors={errors}
            onChange={onChange}
          />
        </>
      ) : category === 'customer_lists_and_intangibles' ? (
        <>
          <NarrativeField
            label={identifier.label}
            path="description"
            body={body}
            errors={errors}
            onChange={onChange}
            required
          />
          <BooleanField
            label="Do your lists include personally identifiable information (as defined in 11 U.S.C. § 101(41A))?"
            path="includes_personal_information"
            body={body}
            errors={errors}
            onChange={onChange}
          />
        </>
      ) : keywordOptions !== undefined ? (
        <>
          <NarrativeField
            label={identifier.label}
            path="description"
            body={body}
            errors={errors}
            onChange={onChange}
            required
          />
          <SelectField
            label="What kind of amount is this?"
            options={keywordOptions}
            value={typeof body.detail === 'string' ? body.detail : null}
            onValueChange={(next) =>
              onChange(setAt(body, 'detail', next === '' ? undefined : next))
            }
            message={errors.detail}
          />
        </>
      ) : identifier.attr === 'detail' ? (
        <TextField
          label={identifier.label}
          path="detail"
          body={body}
          errors={errors}
          onChange={onChange}
          required
        />
      ) : (
        <>
          <TextField
            label={identifier.label}
            path="description"
            body={body}
            errors={errors}
            onChange={onChange}
            required={identifier.attr === 'description'}
          />
          {category === 'non_publicly_traded_stock_and_business_interests' ||
          category === 'partnership_and_joint_venture_interests' ? (
            <TextField
              label="Percentage of ownership"
              path="detail"
              body={body}
              errors={errors}
              onChange={onChange}
            />
          ) : category === 'insurance_policy_interests' ? (
            <TextField
              label="Beneficiary"
              path="detail"
              body={body}
              errors={errors}
              onChange={onChange}
            />
          ) : null}
        </>
      )}

      <View style={styles.group}>
        <MoneyField
          label="Current value of the entire property"
          path="value_entire"
          body={body}
          errors={errors}
          onChange={onChange}
        />
        <MoneyField
          label="Current value of the portion you own"
          path="value_portion_owned"
          body={body}
          errors={errors}
          onChange={onChange}
          description="Leave blank if the value isn't known yet, or for a share with no fixed portion."
        />
      </View>

      {showSharedColumns ? (
        <View style={styles.group}>
          <SelectField
            label="Who has an interest in the property?"
            options={DEBTOR_ATTRIBUTION.map((value) => ({ value, label: labelize(value) }))}
            value={typeof body.ownership_interest === 'string' ? body.ownership_interest : null}
            onValueChange={(next) =>
              onChange(setAt(body, 'ownership_interest', next === '' ? undefined : next))
            }
            message={errors.ownership_interest}
          />
          <BooleanField
            label="Is this community property?"
            path="community_property"
            body={body}
            errors={errors}
            onChange={onChange}
          />
        </View>
      ) : null}

      {identifierMessage !== null ? (
        <Text
          aria-live="assertive"
          style={[
            styles.identifierMessage,
            { color: theme.colors.danger, fontFamily: theme.typography.body },
          ]}
        >
          {identifierMessage}
        </Text>
      ) : null}

      <AssetLiensPanel caseId={caseId} assetId={assetId} onAddSecuredClaim={onAddSecuredClaim} />

      <View style={styles.rowActions}>
        <Button size="lg" disabled={saving} onPress={onSave}>
          {assetId === null ? 'Save asset' : 'Save changes'}
        </Button>
        <Button size="lg" intent="secondary" onPress={onCancel}>
          Cancel
        </Button>
      </View>
    </View>
  );
}

function TextField({
  label,
  path,
  body,
  errors,
  onChange,
  required = false,
}: {
  readonly label: string;
  readonly path: string;
  readonly body: Body;
  readonly errors: Readonly<Record<string, string>>;
  readonly onChange: (next: Body) => void;
  readonly required?: boolean;
}) {
  const value = getAt(body, path);
  const message = errors[path];
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{required ? `${label} (required)` : label}</Field.Label>
      <Input
        value={typeof value === 'string' ? value : ''}
        onValueChange={(next) => onChange(setAt(body, path, next === '' ? undefined : next))}
        autoCorrect={false}
      />
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

function NarrativeField({
  label,
  path,
  body,
  errors,
  onChange,
  required = false,
}: {
  readonly label: string;
  readonly path: string;
  readonly body: Body;
  readonly errors: Readonly<Record<string, string>>;
  readonly onChange: (next: Body) => void;
  readonly required?: boolean;
}) {
  const value = getAt(body, path);
  const message = errors[path];
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{required ? `${label} (required)` : label}</Field.Label>
      <Textarea
        value={typeof value === 'string' ? value : ''}
        onValueChange={(next) => onChange(setAt(body, path, next === '' ? undefined : next))}
      />
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

function MoneyField({
  label,
  path,
  body,
  errors,
  onChange,
  description,
}: {
  readonly label: string;
  readonly path: string;
  readonly body: Body;
  readonly errors: Readonly<Record<string, string>>;
  readonly onChange: (next: Body) => void;
  readonly description?: string;
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
      <Field.Description>
        {description ?? 'Dollars, like 1200.00. Leave blank if unknown.'}
      </Field.Description>
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

function BooleanField({
  label,
  path,
  body,
  errors,
  onChange,
}: {
  readonly label: string;
  readonly path: string;
  readonly body: Body;
  readonly errors: Readonly<Record<string, string>>;
  readonly onChange: (next: Body) => void;
}) {
  const value = getAt(body, path);
  const message = errors[path];
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{label}</Field.Label>
      <Select
        options={[...YES_NO_OPTIONS]}
        value={value === true ? 'yes' : value === false ? 'no' : null}
        onValueChange={(next) =>
          onChange(setAt(body, path, next === 'yes' ? true : next === 'no' ? false : undefined))
        }
        placeholder="Not answered"
      />
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

function MultichoiceField({
  label,
  path,
  options,
  body,
  errors,
  onChange,
}: {
  readonly label: string;
  readonly path: string;
  readonly options: readonly ChoiceOption[];
  readonly body: Body;
  readonly errors: Readonly<Record<string, string>>;
  readonly onChange: (next: Body) => void;
}) {
  const theme = useTheme();
  const value = getAt(body, path);
  const chosen = Array.isArray(value)
    ? value.filter((v): v is string => typeof v === 'string')
    : [];
  const message =
    errors[path] ?? Object.entries(errors).find(([key]) => key.startsWith(`${path}[`))?.[1];
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{label}</Field.Label>
      <CheckboxGroup.Root
        value={[...chosen]}
        onValueChange={(next) => onChange(setAt(body, path, next.length === 0 ? undefined : next))}
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
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

function SelectField({
  label,
  options,
  value,
  onValueChange,
  message,
}: {
  readonly label: string;
  readonly options: readonly ChoiceOption[];
  readonly value: string | null;
  readonly onValueChange: (next: string) => void;
  readonly message: string | undefined;
}) {
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{label}</Field.Label>
      <Select
        options={[...options]}
        value={value}
        onValueChange={onValueChange}
        placeholder="Choose one"
      />
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

// --- the totals rail --------------------------------------------------------

type TotalsState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error' }
  | { readonly kind: 'ready'; readonly totals: CaseTotals };

/**
 * Property total, total secured, total exempt, total non-exempt — the four
 * figures the issue asks for, all read from `GET /v1/cases/{id}/summary`
 * and recomputed on every save. NOTHING HERE ADDS UP: every figure is
 * already the server's total (ADR 0001) — `assets` is B106A/B lines 55+62,
 * `secured` is what the linked claims total (13.4), `totalExempt` is the
 * plain sum of the case's exemption claims, and `totalNonExempt` is
 * `assets` minus `totalExempt` (13.5 owns the real exemption math).
 */
function TotalsRail({ totals }: { readonly totals: TotalsState }) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  if (totals.kind === 'loading') {
    return <Text style={[styles.help, muted]}>Calculating totals…</Text>;
  }
  if (totals.kind === 'error') {
    return (
      <Text
        style={[styles.help, { color: theme.colors.danger, fontFamily: theme.typography.body }]}
      >
        Could not load the totals.
      </Text>
    );
  }
  return (
    <View style={styles.rail} aria-label="Property totals">
      <RailFigure label="Property" value={totals.totals.assets} />
      <RailFigure label="Total secured" value={totals.totals.secured} />
      <RailFigure label="Total exempt" value={totals.totals.totalExempt} />
      <RailFigure label="Total non-exempt" value={totals.totals.totalNonExempt} />
    </View>
  );
}

function RailFigure({ label, value }: { readonly label: string; readonly value: string }) {
  const theme = useTheme();
  return (
    <View style={styles.railFigure}>
      <Text style={[styles.help, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        {label}
      </Text>
      <Text
        style={[styles.railValue, { color: theme.colors.ink, fontFamily: theme.typography.mono }]}
      >
        {formatMoney(value)}
      </Text>
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
  identifierMessage: { fontSize: fontSizes.label },
  list: { gap: spacing.md },
  rail: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.lg },
  railFigure: { gap: 2, minWidth: 120 },
  railValue: { fontSize: fontSizes.body },
  row: { borderBottomWidth: 1, gap: spacing.sm, paddingBottom: spacing.sm },
  rowActions: { flexDirection: 'row', gap: spacing.sm },
  rowSummary: { fontSize: fontSizes.body },
});
