import { useCase } from '@/components/case-shell';
import { FilingPacket } from '@/screens/packet';

/**
 * `/cases/<id>/packet` — assemble and download the Chapter 7 filing packet
 * (issue #96).
 *
 * The session guard and the `caseId` narrowing both moved up to `_layout.tsx`,
 * which wraps this whole subtree — so the route reads the case the shell
 * already loaded instead of re-deriving it from the URL.
 */
export default function CasePacketRoute() {
  const { caseId } = useCase();

  return <FilingPacket caseId={caseId} />;
}
