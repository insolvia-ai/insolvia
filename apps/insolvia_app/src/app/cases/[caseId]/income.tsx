import { useCase } from '@/components/case-shell';
import { IncomeWorkbench } from '@/screens/income-workbench';

/**
 * `/cases/<id>/income` — the income-and-expenses workbench (issue #348):
 * manual pay-period and other-income entry, Schedule I and Schedule J,
 * the IRS Standards, and the income-minus-expenses excess.
 *
 * Same shape as every other case route: the session guard and `caseId`
 * narrowing live in `_layout.tsx`, so this reads the case `CaseShell` already
 * loaded rather than re-deriving it from the URL.
 */
export default function CaseIncomeRoute() {
  const { caseId } = useCase();

  return <IncomeWorkbench caseId={caseId} />;
}
