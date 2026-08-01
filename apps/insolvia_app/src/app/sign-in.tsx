import { SignIn } from '@/screens/sign-in';

/**
 * `/sign-in` — where the route guard sends a signed-out user, and the only
 * place sign-in starts.
 *
 * Public by definition: guarding it would be a redirect loop.
 */
export default function SignInRoute() {
  return <SignIn />;
}
