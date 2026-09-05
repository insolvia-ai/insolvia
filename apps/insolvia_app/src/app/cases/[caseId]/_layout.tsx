import { Slot, useLocalSearchParams } from 'expo-router';

import { CaseShell } from '@/components/case-shell';
import { RequireSession } from '@/components/require-session';

/**
 * The layout every `/cases/<caseId>/…` screen renders inside.
 *
 * **`Slot`, not a nested `Stack`.** The root layout calls itself "the only
 * navigator in the app" and that is worth keeping: a second navigator would
 * bring its own header and its own history stack for a set of sibling tabs
 * that are really one screen with seven faces. `Slot` renders the matched
 * child and nothing else, so this is a frame rather than a navigator.
 *
 * **The guard moved up here.** It used to be repeated in all six route files —
 * the same `RequireSession` around the same shape — and a seventh route added
 * without it would have been a silent hole. One guard over the whole subtree
 * cannot be forgotten. `/cases/<id>/team` still adds `RequireFirm` on top,
 * because being in a firm is a different question from being signed in.
 *
 * **So did the parameter.** Each route file narrowed `params.caseId` from
 * `string | string[]` itself; now {@link CaseShell} publishes the loaded case
 * through `useCase()` and the route files read that. The narrowing is written
 * once, where the segment is declared.
 */
export default function CaseLayout() {
  const params = useLocalSearchParams();
  const raw = params.caseId;
  // `useLocalSearchParams` types every value as `string | string[]` because a
  // catch-all route can produce an array. This segment never does, but the
  // type is honest and is narrowed rather than cast.
  const caseId = Array.isArray(raw) ? (raw[0] ?? '') : (raw ?? '');

  return (
    <RequireSession>
      <CaseShell caseId={caseId}>
        <Slot />
      </CaseShell>
    </RequireSession>
  );
}
