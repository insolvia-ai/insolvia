import { CaseOverview } from '@/screens/case-overview';

/**
 * `/cases/<id>` — the case itself.
 *
 * The route that was missing. Its siblings all existed, so a case could be
 * navigated *into* six different ways and never simply opened; the case list
 * carried six links per row for exactly that reason.
 *
 * No guard and no parameter here: `_layout.tsx` holds `RequireSession` for the
 * whole subtree and `CaseShell` publishes the loaded case, which is what this
 * screen reads.
 */
export default function CaseOverviewRoute() {
  return <CaseOverview />;
}
