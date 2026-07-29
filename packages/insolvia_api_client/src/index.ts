// A hand-written, typed TypeScript client for the Insolvia API
// (`services/api`), usable from the Expo app, scripts, or any other host with
// a platform `fetch`.
//
// The JSON contract it encodes is pinned by this package's tests, not by
// codegen — see README.md for why there is no OpenAPI step. Keep every wire
// literal in this package in sync with:
//   services/api/src/insolvia_api/api/routes/{health,waitlist}.py
//   services/api/src/insolvia_api/api/app_factory.py (error handlers)
//   services/api/src/insolvia_api/core/waitlist.py
//
// The module split is one file per concern — `models.ts`, `exceptions.ts`,
// `client.ts` — so a contract review can read the wire shapes without the
// transport in the way.
//
// This barrel is the package's only entry point (package.json exports
// `./src/index.ts`), so what is re-exported here is the public surface.

export { InsolviaApiClient } from './client.ts';
export type { FetchLike, InsolviaApiClientOptions } from './client.ts';

export { ApiException, ApiValidationException } from './exceptions.ts';
export type { ApiExceptionOptions, ApiValidationExceptionOptions } from './exceptions.ts';

export { submittedAtUtc, waitlistSubmissionToJson } from './models.ts';
export type { HealthStatus, WaitlistConfirmation, WaitlistSubmission } from './models.ts';
