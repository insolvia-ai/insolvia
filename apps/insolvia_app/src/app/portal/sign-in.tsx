import { PortalSignIn } from '@/screens/portal/sign-in';

/** `/portal/sign-in` — public by definition: guarding it would be a loop. */
export default function PortalSignInRoute() {
  return <PortalSignIn />;
}
