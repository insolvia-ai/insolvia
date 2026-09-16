import type { CaseEntity, CaseLiens, ClaimLien } from '@insolvia-ai/api-client';
import { Button, Field, Select } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

import { COLLECTION_SPECS } from './collections';

/**
 * The two collateral views of issue #345, each read-only over figures the
 * SERVER derives (`GET /v1/cases/{id}/liens` — the same function B106D
 * prints Column B from):
 *
 * - `ClaimCollateralPanel`, beside a secured claim's form: what the
 *   collateral resolved to, the liens ahead of it, and the secured and
 *   unsecured portions — with "entered manually" where the claim's override
 *   field is what the server reported.
 * - `AssetLiensPanel`, beside a property's form: the claims linked to it and
 *   their total, "add a secured claim" already pointed at this property, and
 *   "link an existing claim".
 *
 * NOTHING HERE ADDS UP. The app holds the amounts it would need to compute a
 * deficiency, and computing it here would be a second answer to "how much
 * of this is unsecured" that agrees with Schedule D only until somebody
 * edits either (ADR 0001, and the reasoning in the API's `core/liens.py`).
 * The figures shown are as of the last save, and the panel says so.
 */

type Body = Record<string, unknown>;

/** `3000.00` → `$3,000.00`, without ever parsing the amount as a number. */
export function formatMoney(value: string): string {
  const [whole = '0', fraction = '00'] = value.split('.');
  const grouped = whole.replaceAll(/\B(?=(?:\d{3})+(?!\d))/gu, ',');
  return `$${grouped}.${fraction.padEnd(2, '0').slice(0, 2)}`;
}

const claimsSpec = COLLECTION_SPECS.find((spec) => spec.collection === 'claims');

function claimLabel(claim: CaseEntity<'claims'>, index: number): string {
  const { id: _id, case_id: _c, created_at: _a, updated_at: _u, provenance: _p, ...body } = claim;
  return `Claim ${index + 1} — ${claimsSpec?.summary(body as Body) ?? claim.id}`;
}

type ClaimPanelState =
  | { readonly kind: 'unsaved' }
  | { readonly kind: 'loading' }
  | { readonly kind: 'error' }
  | { readonly kind: 'ready'; readonly lien: ClaimLien | null };

export function ClaimCollateralPanel({
  caseId,
  claimId,
}: {
  readonly caseId: string;
  readonly claimId: string | null;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [state, setState] = useState<ClaimPanelState>(
    claimId === null ? { kind: 'unsaved' } : { kind: 'loading' },
  );

  useEffect(() => {
    if (claimId === null) return;
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) => client.getCaseLiens(caseId));
        if (!result.ok || cancelled) return;
        setState({
          kind: 'ready',
          lien: result.value.claims.find((row) => row.claimId === claimId) ?? null,
        });
      } catch {
        if (!cancelled) setState({ kind: 'error' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId, claimId]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  return (
    <View style={styles.panel}>
      <Heading level={3} size="body">
        What the collateral covers
      </Heading>
      {state.kind === 'unsaved' ? (
        <Text style={[styles.note, muted]}>
          Save the claim to see the secured and unsecured portions — they are calculated from the
          claim, its property and any liens ahead of it, never typed.
        </Text>
      ) : state.kind === 'loading' ? (
        <Text style={[styles.note, muted]}>Calculating…</Text>
      ) : state.kind === 'error' ? (
        <Text
          style={[styles.note, { color: theme.colors.danger, fontFamily: theme.typography.body }]}
        >
          Could not load the calculated figures.
        </Text>
      ) : state.lien === null ? (
        <Text style={[styles.note, muted]}>
          Nothing is calculated for a claim that is not secured — set the class to secured first.
        </Text>
      ) : (
        <View style={styles.figures}>
          <Figure label="Collateral" value={state.lien.collateralDescription ?? '—'} text />
          <Figure
            label="Value of the collateral"
            value={
              state.lien.collateralValue === undefined
                ? 'Not known yet'
                : formatMoney(state.lien.collateralValue)
            }
          />
          <Figure label="Liens ahead of this one" value={formatMoney(state.lien.seniorLiens)} />
          <Figure
            label="Secured portion"
            value={
              state.lien.securedAmount === undefined
                ? 'Not known yet'
                : formatMoney(state.lien.securedAmount)
            }
          />
          <Figure
            label="Unsecured portion"
            value={
              state.lien.unsecuredAmount === undefined
                ? 'Not known yet'
                : formatMoney(state.lien.unsecuredAmount)
            }
            note={state.lien.unsecuredSource === 'manual' ? 'Entered manually' : 'Calculated'}
          />
          <Text style={[styles.note, muted]}>
            As of the last save. To state the unsecured portion yourself, fill in the override field
            above and save.
          </Text>
        </View>
      )}
    </View>
  );
}

type AssetPanelState =
  | { readonly kind: 'unsaved' }
  | { readonly kind: 'loading' }
  | { readonly kind: 'error' }
  | {
      readonly kind: 'ready';
      readonly liens: CaseLiens;
      readonly claims: readonly CaseEntity<'claims'>[];
    };

export function AssetLiensPanel({
  caseId,
  assetId,
  onAddSecuredClaim,
}: {
  readonly caseId: string;
  readonly assetId: string | null;
  /** Opens the claims section on a new claim already pointed at this asset. */
  readonly onAddSecuredClaim: (body: Body) => void;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [state, setState] = useState<AssetPanelState>(
    assetId === null ? { kind: 'unsaved' } : { kind: 'loading' },
  );
  const [chosen, setChosen] = useState<string | null>(null);
  const [status, setStatus] = useState('');

  const load = useCallback(async () => {
    if (assetId === null) return;
    try {
      const liens = await call((client) => client.getCaseLiens(caseId));
      if (!liens.ok) return;
      const claims = await call((client) => client.listCaseEntities(caseId, 'claims'));
      if (!claims.ok) return;
      setState({ kind: 'ready', liens: liens.value, claims: claims.value });
    } catch {
      setState({ kind: 'error' });
    }
  }, [assetId, call, caseId]);

  useEffect(() => {
    void load();
  }, [load]);

  const link = useCallback(async () => {
    if (state.kind !== 'ready' || assetId === null || chosen === null) return;
    const claim = state.claims.find((candidate) => candidate.id === chosen);
    if (claim === undefined) return;
    setStatus('Linking…');
    try {
      // The record's own provenance is kept, and only the one field this
      // action writes gains an entry — rebuilding the map as staff_typed
      // would relabel a confirmed extraction as something a person typed.
      const { id, case_id: _c, created_at: _a, updated_at: _u, provenance, ...body } = claim;
      const result = await call((client) =>
        client.putCaseEntity(caseId, 'claims', id, {
          ...body,
          asset_id: assetId,
          provenance: { ...provenance, asset_id: { source: 'staff_typed' } },
        }),
      );
      if (!result.ok) {
        setStatus('');
        return;
      }
      setChosen(null);
      setStatus('Linked');
      await load();
    } catch {
      setStatus('Could not link it. Try again.');
    }
  }, [assetId, call, caseId, chosen, load, state]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  if (state.kind === 'unsaved') {
    return (
      <View style={styles.panel}>
        <Heading level={3} size="body">
          Liens on this property
        </Heading>
        <Text style={[styles.note, muted]}>
          Save the property first — a secured claim is linked to a saved record.
        </Text>
      </View>
    );
  }

  if (state.kind !== 'ready') {
    return (
      <View style={styles.panel}>
        <Heading level={3} size="body">
          Liens on this property
        </Heading>
        <Text
          style={[
            styles.note,
            state.kind === 'error'
              ? { color: theme.colors.danger, fontFamily: theme.typography.body }
              : muted,
          ]}
        >
          {state.kind === 'error' ? 'Could not load the liens.' : 'Loading liens…'}
        </Text>
      </View>
    );
  }

  const onAsset = state.liens.assets.find((entry) => entry.assetId === assetId);
  const linked = (onAsset?.claimIds ?? []).map((claimId) => ({
    claimId,
    lien: state.liens.claims.find((row) => row.claimId === claimId),
    index: state.claims.findIndex((claim) => claim.id === claimId),
  }));
  // Only a secured claim can be a lien; one already on this property is
  // not offered a second time.
  const linkable = state.claims
    .map((claim, index) => ({ claim, index }))
    .filter(({ claim }) => claim.claim_class === 'secured' && claim.asset_id !== assetId)
    .map(({ claim, index }) => ({ value: claim.id, label: claimLabel(claim, index) }));

  return (
    <View style={styles.panel}>
      <Heading level={3} size="body">
        Liens on this property
      </Heading>
      <Text aria-live="polite" style={[styles.note, muted]}>
        {status}
      </Text>
      {linked.length === 0 ? (
        <Text style={[styles.note, muted]}>No secured claim is linked to this property yet.</Text>
      ) : (
        <View style={styles.figures}>
          {linked.map(({ claimId, lien, index }) => {
            const claim = index >= 0 ? state.claims[index] : undefined;
            const label = claim === undefined ? claimId : claimLabel(claim, index);
            const portions =
              lien?.unsecuredAmount === undefined
                ? 'portions not known yet'
                : `${formatMoney(lien.securedAmount ?? '0.00')} secured, ${formatMoney(
                    lien.unsecuredAmount,
                  )} unsecured${lien.unsecuredSource === 'manual' ? ' (entered manually)' : ''}`;
            return (
              <Text key={claimId} style={[styles.row, ink]}>
                {`${label} — ${lien?.lienPosition === undefined ? 'position not set' : `position ${lien.lienPosition}`}; ${portions}`}
              </Text>
            );
          })}
          <Figure
            label="Secured against this property"
            value={formatMoney(onAsset?.securedTotal ?? '0.00')}
          />
        </View>
      )}
      <View style={styles.actions}>
        <Button
          size="lg"
          intent="secondary"
          onPress={() => onAddSecuredClaim({ claim_class: 'secured', asset_id: assetId })}
        >
          Add a secured claim
        </Button>
      </View>
      {linkable.length === 0 ? (
        <Text style={[styles.note, muted]}>
          No other secured claim to link — a claim must be secured before it can be a lien.
        </Text>
      ) : (
        <View style={styles.actions}>
          <Field.Root>
            <Field.Label>Link an existing claim</Field.Label>
            <Select
              options={linkable}
              value={chosen}
              onValueChange={(next) =>
                setChosen(linkable.some((option) => option.value === next) ? next : null)
              }
              placeholder="Choose a secured claim"
            />
          </Field.Root>
          <Button
            size="lg"
            intent="secondary"
            disabled={chosen === null}
            onPress={() => void link()}
          >
            Link claim
          </Button>
        </View>
      )}
    </View>
  );
}

function Figure({
  label,
  value,
  note,
  text = false,
}: {
  readonly label: string;
  readonly value: string;
  readonly note?: string | undefined;
  /** Prose rather than an amount — set in the body face, not the mono. */
  readonly text?: boolean;
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
        style={[
          styles.figureValue,
          {
            color: theme.colors.ink,
            fontFamily: text ? theme.typography.body : theme.typography.mono,
          },
        ]}
      >
        {value}
      </Text>
      {note === undefined ? null : (
        <Text
          style={[styles.note, { color: theme.colors.muted, fontFamily: theme.typography.body }]}
        >
          {note}
        </Text>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  actions: { gap: spacing.sm },
  figure: { gap: 2 },
  figureLabel: { fontSize: fontSizes.label },
  figureValue: { fontSize: fontSizes.body },
  figures: { gap: spacing.sm },
  note: { fontSize: fontSizes.label },
  panel: { gap: spacing.sm, marginTop: spacing.sm },
  row: { fontSize: fontSizes.body },
});
