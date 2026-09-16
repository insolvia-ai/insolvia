import { ApiValidationException, staffTypedProvenance } from '@insolvia-ai/api-client';
import type {
  AssetExemptionFigures,
  ExemptionAnalysis,
  ExemptionBody,
  ExemptionEntryAvailability,
  ExemptionSet,
} from '@insolvia-ai/api-client';
import { Alert, Button, Checkbox, Field, Input, Select } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { formatFormDate } from '@/screens/petition/statutory-dates';
import { fontSizes, spacing, useTheme } from '@/theme';

import { formatMoney } from './liens';

/**
 * The Schedule C workbench, beside a property's form (issue #346): the
 * exemptions claimed on THIS asset, the room the elected scheme leaves, and
 * the arithmetic between them — every figure the SERVER's
 * (`GET /v1/cases/{id}/exemption-analysis`), the same way `AssetLiensPanel`
 * shows `core/liens.py`'s.
 *
 * NOTHING HERE ADDS UP. Current value, liens, net equity, what is claimed
 * and what is left unexempt are all read off the analysis; the default
 * amount for a new claim is the server's own suggestion for the chosen
 * statute (the lesser of the unexempt equity and the statute's remaining
 * room), not a subtraction done in a screen (ADR 0001).
 *
 * The 106C line 1 ELECTION is a case-level fact and lives on the case
 * record, but it is set here — on the asset, where the preparer is about to
 * claim under it — because the table of statutes is empty until it is made
 * and an empty picker with no way to fill it is a dead end. The options come
 * from the analysis (the registry's opt-out rule decides whether the federal
 * list is even offered), and the API refuses a forbidden one with a message
 * this panel shows under the control.
 *
 * NO FREE-TEXT LAW: the statute is picked from the case's own table, and the
 * claim stores the registry's citation verbatim, which is what B106C prints.
 * A claim typed through the generic collection route with a citation the
 * table does not know still shows here — marked, and drawing down nothing.
 *
 * There is deliberately NO percentage field. B106C line 2 admits a dollar
 * amount OR "100% of fair market value, up to any applicable statutory
 * limit" — those are the two boxes the form prints and the two states the
 * record holds (`amount` / `claims_full_fmv`); a percentage would be a field
 * no form reads and a second definition of the claimed amount.
 */

type PanelState =
  | { readonly kind: 'unsaved' }
  | { readonly kind: 'loading' }
  | { readonly kind: 'error' }
  | { readonly kind: 'ready'; readonly analysis: ExemptionAnalysis };

interface Draft {
  readonly entryId: string | null;
  readonly amount: string;
  readonly fullFmv: boolean;
  readonly acquiredWithin1215Days: boolean | undefined;
}

const EMPTY_DRAFT: Draft = {
  entryId: null,
  amount: '',
  fullFmv: false,
  acquiredWithin1215Days: undefined,
};

const YES_NO_OPTIONS = [
  { value: 'yes', label: 'Yes' },
  { value: 'no', label: 'No' },
] as const;

/** `federal` → the wire value's label; the scheme's own name says the rest. */
function electionLabel(value: ExemptionSet, name: string): string {
  return value === 'federal' ? `Federal § 522(d) — ${name}` : `State scheme — ${name}`;
}

/** What a statute is worth to this case, in the picker's own line. */
function entryOptionLabel(entry: ExemptionEntryAvailability): string {
  const room =
    entry.available === null
      ? entry.unlimited
        ? 'no dollar limit'
        : 'no flat cap'
      : `${formatMoney(entry.available)} available`;
  return `${entry.citation} — ${room}`;
}

export function AssetExemptionsPanel({
  caseId,
  assetId,
}: {
  readonly caseId: string;
  readonly assetId: string | null;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [state, setState] = useState<PanelState>(
    assetId === null ? { kind: 'unsaved' } : { kind: 'loading' },
  );
  const [draft, setDraft] = useState<Draft>(EMPTY_DRAFT);
  const [status, setStatus] = useState('');
  const [electionError, setElectionError] = useState<string | null>(null);
  const [claimErrors, setClaimErrors] = useState<Readonly<Record<string, string>>>({});
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    if (assetId === null) return;
    try {
      const result = await call((client) => client.getCaseExemptionAnalysis(caseId));
      if (!result.ok) return;
      setState({ kind: 'ready', analysis: result.value });
    } catch {
      setState({ kind: 'error' });
    }
  }, [assetId, call, caseId]);

  useEffect(() => {
    void load();
  }, [load]);

  const elect = useCallback(
    async (next: ExemptionSet) => {
      setBusy(true);
      setStatus('Saving the election…');
      setElectionError(null);
      try {
        const result = await call((client) => client.updateCase(caseId, { exemptionSet: next }));
        if (!result.ok) {
          setStatus('');
          return;
        }
        setStatus('Election saved');
        setDraft(EMPTY_DRAFT);
        await load();
      } catch (cause) {
        if (cause instanceof ApiValidationException) {
          setElectionError(cause.fields.exemption_set ?? 'That election is not available.');
          setStatus('');
        } else {
          setStatus('Could not save the election. Try again.');
        }
      } finally {
        setBusy(false);
      }
    },
    [call, caseId, load],
  );

  const addClaim = useCallback(
    async (entry: ExemptionEntryAvailability) => {
      if (assetId === null) return;
      setBusy(true);
      setStatus('Saving…');
      setClaimErrors({});
      try {
        // The registry's citation, verbatim — what B106C prints and what the
        // analysis matches the claim back to. `amount` is omitted, not sent
        // as '', under the full-FMV election; the record holds one or the
        // other (docs/reference/case-data-model.md).
        const body: ExemptionBody = {
          asset_id: assetId,
          statute_citation: entry.citation,
          claims_full_fmv: draft.fullFmv,
          ...(draft.fullFmv || draft.amount.trim() === '' ? {} : { amount: draft.amount.trim() }),
          ...(draft.acquiredWithin1215Days === undefined
            ? {}
            : { acquired_within_1215_days: draft.acquiredWithin1215Days }),
        };
        const result = await call((client) =>
          client.addCaseEntity(caseId, 'exemptions', {
            ...body,
            provenance: staffTypedProvenance(body as unknown as Record<string, unknown>),
          }),
        );
        if (!result.ok) {
          setStatus('');
          return;
        }
        setDraft(EMPTY_DRAFT);
        setStatus('Claimed');
        await load();
      } catch (cause) {
        if (cause instanceof ApiValidationException) {
          setClaimErrors(cause.fields);
          setStatus('Some answers need attention.');
        } else {
          setStatus('Could not save the claim. Try again.');
        }
      } finally {
        setBusy(false);
      }
    },
    [assetId, call, caseId, draft, load],
  );

  const removeClaim = useCallback(
    async (exemptionId: string) => {
      setBusy(true);
      setStatus('Removing…');
      try {
        const result = await call((client) =>
          client.deleteCaseEntity(caseId, 'exemptions', exemptionId),
        );
        if (!result.ok) {
          setStatus('');
          return;
        }
        setStatus('Removed');
        await load();
      } catch {
        setStatus('Could not remove it. Try again.');
      } finally {
        setBusy(false);
      }
    },
    [call, caseId, load],
  );

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const danger = { color: theme.colors.danger, fontFamily: theme.typography.body };

  if (state.kind === 'unsaved') {
    return (
      <View style={styles.panel}>
        <Heading level={3} size="body">
          Exemptions on this property
        </Heading>
        <Text style={[styles.note, muted]}>
          Save the property first — an exemption is claimed on a saved record.
        </Text>
      </View>
    );
  }

  if (state.kind !== 'ready') {
    return (
      <View style={styles.panel}>
        <Heading level={3} size="body">
          Exemptions on this property
        </Heading>
        <Text style={[styles.note, state.kind === 'error' ? danger : muted]}>
          {state.kind === 'error' ? 'Could not load the exemption figures.' : 'Loading exemptions…'}
        </Text>
      </View>
    );
  }

  const { analysis } = state;
  const figures: AssetExemptionFigures | undefined = analysis.assets.find(
    (row) => row.assetId === assetId,
  );
  const chosen = analysis.entries.find((entry) => entry.entryId === draft.entryId);
  const isHomestead = figures?.category === 'real_property';
  const electionValue = analysis.election.stored ?? analysis.election.effective;
  const filingDate = formatFormDate(analysis.asOf);

  return (
    <View style={styles.panel}>
      <Heading level={3} size="body">
        Exemptions on this property
      </Heading>
      <Text aria-live="polite" style={[styles.note, muted]}>
        {status}
      </Text>

      {/* ── The election ─────────────────────────────────────────── */}
      {analysis.election.options.length === 0 ? null : (
        <Field.Root invalid={electionError !== null}>
          <Field.Label>Exemption scheme for this case (Schedule C, line 1)</Field.Label>
          <Select
            options={analysis.election.options.map((option) => ({
              value: option.value,
              label: electionLabel(option.value, option.name),
            }))}
            value={electionValue}
            onValueChange={(next) => {
              if (next === 'federal' || next === 'state_and_federal_nonbankruptcy') {
                void elect(next);
              }
            }}
            placeholder="Choose the scheme"
          />
          <Field.Description>
            {analysis.election.optedOut === true
              ? `${analysis.election.state ?? 'This state'} has opted out of the federal list (${analysis.election.optOutCitation ?? '§ 522(b)(2)'}); the state scheme is the only choice.`
              : 'The state scheme, or the federal § 522(d) list — one for the whole case.'}
          </Field.Description>
          {electionError === null ? null : <Field.Error match>{electionError}</Field.Error>}
        </Field.Root>
      )}

      {analysis.problems.map((problem) => (
        <Text key={problem} style={[styles.note, muted]}>
          {problem}
        </Text>
      ))}
      {analysis.warnings.map((warning) => (
        <Alert.Root key={warning} intent="warning">
          <Alert.Title>Check before claiming</Alert.Title>
          <Alert.Description>{warning}</Alert.Description>
        </Alert.Root>
      ))}

      {/* ── The arithmetic ───────────────────────────────────────── */}
      {figures === undefined ? null : (
        <View style={styles.figures}>
          <Figure
            label="Current value"
            value={
              figures.currentValue === null ? 'Not known yet' : formatMoney(figures.currentValue)
            }
          />
          <Figure label="Liens on this property" value={formatMoney(figures.liens)} />
          <Figure
            label="Net equity"
            value={figures.netEquity === null ? 'Not known yet' : formatMoney(figures.netEquity)}
          />
          <Figure label="Claimed exempt" value={formatMoney(figures.claimed)} />
          <Figure
            label="Unexempt"
            value={figures.unexempt === null ? 'Not known yet' : formatMoney(figures.unexempt)}
            note={
              figures.homesteadCap === null
                ? undefined
                : figures.capApplied
                  ? `Capped: acquired within 1,215 days of filing, so § 522(p) limits the homestead claim to ${formatMoney(figures.homesteadCap)}.`
                  : `Acquired within 1,215 days of filing: § 522(p) caps the homestead claim at ${formatMoney(figures.homesteadCap)}.`
            }
          />
          <Text style={[styles.note, muted]}>
            {`As of the last save, resolved for a filing on ${filingDate}${
              analysis.asOfSource === 'today' ? ' (today — no expected filing date is set)' : ''
            }.`}
          </Text>
        </View>
      )}

      {/* ── The lookback dates ───────────────────────────────────── */}
      <View style={styles.figures}>
        <Text style={[styles.row, ink]}>
          {`§ 522(o) — transfers on or after ${formatFormDate(analysis.lookbacks.section522o)} (10 years) can reduce the homestead exemption.`}
        </Text>
        <Text style={[styles.row, ink]}>
          {`§ 522(p) — a homestead acquired on or after ${formatFormDate(analysis.lookbacks.section522p)} (1,215 days) is capped.`}
        </Text>
        <Text style={[styles.row, ink]}>
          {`§ 522(q) — misconduct on or after ${formatFormDate(analysis.lookbacks.section522q)} (5 years) caps the homestead.`}
        </Text>
      </View>

      {/* ── The claims already on it ─────────────────────────────── */}
      {figures === undefined || figures.claims.length === 0 ? (
        <Text style={[styles.note, muted]}>No exemption is claimed on this property yet.</Text>
      ) : (
        <View style={styles.figures}>
          {figures.claims.map((claim, index) => (
            <View key={claim.exemptionId} style={styles.claim}>
              <Text style={[styles.row, ink]}>
                {`${claim.statuteCitation ?? 'No statute'} — ${
                  claim.claimsFullFmv === true
                    ? `100% of fair market value (${formatMoney(claim.claimed)})`
                    : formatMoney(claim.claimed)
                }${claim.knownStatute ? '' : ' — not in the elected scheme'}`}
              </Text>
              <Button
                size="lg"
                intent="secondary"
                disabled={busy}
                aria-label={`Remove exemption ${index + 1}`}
                onPress={() => void removeClaim(claim.exemptionId)}
              >
                Remove
              </Button>
            </View>
          ))}
        </View>
      )}

      {/* ── A new claim ──────────────────────────────────────────── */}
      {analysis.entries.length === 0 ? null : (
        <View style={styles.form}>
          <Field.Root invalid={Boolean(claimErrors.statute_citation)}>
            <Field.Label>Claim an exemption under</Field.Label>
            <Select
              options={analysis.entries.map((entry) => ({
                value: entry.entryId,
                label: entryOptionLabel(entry),
              }))}
              value={draft.entryId}
              onValueChange={(next) => {
                const entry = analysis.entries.find((candidate) => candidate.entryId === next);
                if (entry === undefined) return;
                setDraft((current) => ({
                  ...current,
                  entryId: entry.entryId,
                  // The server's default for this asset under this statute.
                  amount: figures?.suggestions[entry.entryId] ?? '',
                }));
              }}
              placeholder="Choose a statute"
            />
            {chosen === undefined ? null : (
              <Field.Description>
                {`${chosen.description}.${chosen.notes === '' ? '' : ` ${chosen.notes}`}${
                  chosen.carryover === null
                    ? ''
                    : ` Includes ${formatMoney(chosen.carryover)} of unused homestead carried over.`
                }`}
              </Field.Description>
            )}
            {claimErrors.statute_citation ? (
              <Field.Error match>{claimErrors.statute_citation}</Field.Error>
            ) : null}
          </Field.Root>

          <View style={styles.checkboxRow}>
            <Checkbox.Root
              aria-label="Claim 100% of fair market value, up to the statutory limit"
              checked={draft.fullFmv}
              onCheckedChange={(checked) =>
                setDraft((current) => ({ ...current, fullFmv: checked === true }))
              }
            >
              <Checkbox.Indicator>✓</Checkbox.Indicator>
            </Checkbox.Root>
            <Text aria-hidden style={[styles.checkboxLabel, ink]}>
              Claim 100% of fair market value, up to the statutory limit
            </Text>
          </View>

          {draft.fullFmv ? null : (
            <Field.Root invalid={Boolean(claimErrors.amount)}>
              <Field.Label>Amount claimed exempt</Field.Label>
              <Input
                value={draft.amount}
                onValueChange={(next) => setDraft((current) => ({ ...current, amount: next }))}
                autoCorrect={false}
              />
              <Field.Description>
                Dollars, like 1200.00. Defaults to the lesser of the unexempt equity and the
                statute&apos;s remaining room.
              </Field.Description>
              {claimErrors.amount ? <Field.Error match>{claimErrors.amount}</Field.Error> : null}
            </Field.Root>
          )}

          {isHomestead ? (
            <Field.Root invalid={Boolean(claimErrors.acquired_within_1215_days)}>
              <Field.Label>
                {`Was this homestead acquired within 1,215 days before filing (on or after ${formatFormDate(analysis.lookbacks.section522p)})?`}
              </Field.Label>
              <Select
                options={YES_NO_OPTIONS}
                value={
                  draft.acquiredWithin1215Days === undefined
                    ? null
                    : draft.acquiredWithin1215Days
                      ? 'yes'
                      : 'no'
                }
                onValueChange={(next) =>
                  setDraft((current) => ({
                    ...current,
                    acquiredWithin1215Days:
                      next === 'yes' ? true : next === 'no' ? false : undefined,
                  }))
                }
                placeholder="Not answered"
              />
              {claimErrors.acquired_within_1215_days ? (
                <Field.Error match>{claimErrors.acquired_within_1215_days}</Field.Error>
              ) : null}
            </Field.Root>
          ) : null}

          <View style={styles.actions}>
            <Button
              size="lg"
              disabled={busy || chosen === undefined}
              onPress={() => {
                if (chosen !== undefined) void addClaim(chosen);
              }}
            >
              Claim exemption
            </Button>
          </View>
        </View>
      )}
    </View>
  );
}

function Figure({
  label,
  value,
  note,
}: {
  readonly label: string;
  readonly value: string;
  readonly note?: string | undefined;
}) {
  const theme = useTheme();
  return (
    <View style={styles.figure}>
      <Text
        style={[
          styles.figureLabel,
          { color: theme.colors.muted, fontFamily: theme.typography.body },
        ]}
      >
        {label}
      </Text>
      <Text
        style={[styles.figureValue, { color: theme.colors.ink, fontFamily: theme.typography.mono }]}
      >
        {value}
      </Text>
      {note === undefined ? null : (
        <Text
          style={[styles.note, { color: theme.colors.warning, fontFamily: theme.typography.body }]}
        >
          {note}
        </Text>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  actions: { flexDirection: 'row', gap: spacing.sm },
  checkboxLabel: { flexShrink: 1, fontSize: fontSizes.body },
  checkboxRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
  claim: { gap: spacing.xs },
  figure: { gap: 2 },
  figureLabel: { fontSize: fontSizes.label },
  figureValue: { fontSize: fontSizes.body },
  figures: { gap: spacing.sm },
  form: { gap: spacing.md },
  note: { fontSize: fontSizes.label },
  panel: { gap: spacing.sm, marginTop: spacing.sm },
  row: { fontSize: fontSizes.body },
});
