import { useCase } from '@/components/case-shell';
import { Plan } from '@/screens/plan';

/**
 * `/cases/<id>/plan` — the Chapter 13 plan (issue 16.2 / #366): the
 * proposal, the waterfall, and the feasibility and liquidation tests the
 * server recalculates on every save.
 *
 * Same shape as every other case route: the session guard and `caseId`
 * narrowing live in `_layout.tsx`.
 */
export default function CasePlanRoute() {
  const { caseId } = useCase();

  return <Plan caseId={caseId} />;
}
