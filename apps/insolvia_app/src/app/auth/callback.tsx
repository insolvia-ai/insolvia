import { AuthCallback } from '@/screens/auth-callback';

/**
 * `/auth/callback` — the OAuth return leg.
 *
 * **This path must stay in step with `infra/modules/auth/main.tf`**, whose web
 * client registers `<origin>/auth/callback` as its only OAuth callback URL
 * (`web_callback_urls = "${o}/auth/callback"`). Under file-based routing the path
 * *is* this file's location, so renaming or moving the file silently breaks the
 * return leg of sign-in — `auth-callback.test.tsx` asserts the file is here for
 * exactly that reason.
 */
export default function AuthCallbackRoute() {
  return <AuthCallback />;
}
