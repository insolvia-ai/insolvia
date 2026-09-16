import { Petition } from '@/screens/petition';

/**
 * `/cases/<caseId>/petition` — the B101 answers (issue #342).
 *
 * The session guard is `_layout.tsx`, which wraps this whole subtree, the
 * same as every other case route.
 */
export default function PetitionRoute() {
  return <Petition />;
}
