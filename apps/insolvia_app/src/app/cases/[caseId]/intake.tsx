import { Intake } from '@/screens/intake';

/**
 * `/cases/<caseId>/intake` — the structured intake for one case (issue 8.5).
 *
 * The session guard moved up to `_layout.tsx`, which wraps this whole subtree.
 * `Intake` still reads `caseId` from the route itself rather than taking it as
 * a prop like its siblings: it is the one screen that was already written that
 * way, and rewriting a form this size to prove a point about consistency would
 * be a large diff for no behaviour.
 */
export default function IntakeRoute() {
  return <Intake />;
}
