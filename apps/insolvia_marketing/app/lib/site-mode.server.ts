/**
 * Whether this deployment serves the real marketing site or a holding page.
 *
 * Contract with `infra/modules/marketing_site` (its `site_mode` variable feeds
 * the Lambda's `INSOLVIA_SITE_MODE`). Read at runtime, never baked in, so one
 * environment-agnostic image serves staging and production — the same rule the
 * waitlist's `INSOLVIA_API_BASE_URL` follows.
 *
 * Placeholder mode exists for a specific reason worth knowing before removing
 * it: a CloudFront distribution that is *disabled* resolves to nothing, so
 * parking production also removes `insolvia.ai` from DNS — and Cognito refuses
 * to create a custom auth domain unless the parent domain resolves. Serving a
 * holding page keeps the apex resolving while the positioning stays private.
 */
const PLACEHOLDER = "placeholder";

/**
 * Fails OPEN, deliberately: an unset or unrecognised value serves the full
 * site. The variable is set only where a holding page is wanted, so a typo or a
 * missing Lambda environment shows the real site rather than silently hiding it
 * — visible and obviously wrong beats invisible and plausible.
 */
export function isPlaceholderSite(): boolean {
  return process.env.INSOLVIA_SITE_MODE?.trim().toLowerCase() === PLACEHOLDER;
}
