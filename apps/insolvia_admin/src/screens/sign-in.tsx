import { Button, Card } from "@insolvia-ai/design-system";

import { useSession } from "../session/session";

/**
 * A button and nothing else — there is no password form here and there must
 * never be one (the same rule as the app's sign-in screen): staff
 * authenticate with their Insolvia Google Workspace account, on Google's own
 * pages.
 */
export function SignInScreen({ returnTo }: { returnTo?: string }) {
  const session = useSession();
  return (
    <div className="flex min-h-screen items-center justify-center px-6">
      <Card.Root className="w-full max-w-sm p-8 text-center">
        <h1 className="mb-2 font-serif text-xl font-bold">Insolvia Admin</h1>
        <p className="mb-6 text-sm text-muted">
          Firm provisioning and administration. Sign in with your Insolvia
          Google account.
        </p>
        <Button intent="primary" onClick={() => void session.signIn(returnTo)}>
          Sign in with Google
        </Button>
      </Card.Root>
    </div>
  );
}
