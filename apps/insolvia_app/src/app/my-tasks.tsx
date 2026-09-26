import { RequireFirm } from '@/components/require-firm';
import { RequireSession } from '@/components/require-session';
import { MyTasks } from '@/screens/my-tasks';

/**
 * `/my-tasks` — every task assigned to you, across every case you can reach
 * (issue #356 / 14.4). The same two guards `/account` and `/firm` use, for
 * the same reason: a signed-in person nobody has added to a firm has no
 * tasks to show.
 */
export default function MyTasksRoute() {
  return (
    <RequireSession>
      <RequireFirm>{(membership) => <MyTasks membership={membership} />}</RequireFirm>
    </RequireSession>
  );
}
