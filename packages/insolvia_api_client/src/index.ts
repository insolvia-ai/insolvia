// A hand-written, typed TypeScript client for the Insolvia API
// (`services/api`), usable from the Expo app, scripts, or any other host with
// a platform `fetch`.
//
// The JSON contract it encodes is pinned by this package's tests, not by
// codegen — see README.md for why there is no OpenAPI step. Keep every wire
// literal in this package in sync with:
//   services/api/src/insolvia_api/api/routes/{health,waitlist,me,cases,documents}.py
//   services/api/src/insolvia_api/api/app_factory.py (error handlers)
//   services/api/src/insolvia_api/api/auth.py (the 401 body)
//   services/api/src/insolvia_api/core/{waitlist,auth,documents}.py
//
// The module split is one file per concern — `models.ts`, `exceptions.ts`,
// `client.ts` — so a contract review can read the wire shapes without the
// transport in the way.
//
// This barrel is the package's only entry point (package.json exports
// `./src/index.ts`), so what is re-exported here is the public surface.

export { InsolviaApiClient, isUploadIncomplete } from './client.ts';
export type { AccessTokenProvider, FetchLike, InsolviaApiClientOptions } from './client.ts';

export { ApiException, ApiUnauthorizedException, ApiValidationException } from './exceptions.ts';
export type {
  ApiExceptionOptions,
  ApiUnauthorizedExceptionOptions,
  ApiValidationExceptionOptions,
  UnauthorizedSource,
} from './exceptions.ts';

export {
  DOCUMENT_CONTENT_TYPES,
  DOCUMENT_KINDS,
  DOCUMENT_STATUSES,
  MAX_DOCUMENT_BYTE_SIZE,
  createCaseRequestToJson,
  createDocumentRequestToJson,
  isDocumentContentType,
  isDocumentKind,
  listCasesQuery,
  submittedAtUtc,
  updateCaseChangesToJson,
  waitlistSubmissionToJson,
} from './models.ts';
export type {
  Case,
  CaseChapter,
  CaseStatus,
  CreateCaseRequest,
  CreateDocumentRequest,
  CreateDocumentResult,
  Document,
  DocumentContentType,
  DocumentDownload,
  DocumentKind,
  DocumentStatus,
  DocumentUpload,
  HealthStatus,
  ListCasesOptions,
  ListCasesResult,
  Principal,
  UpdateCaseChanges,
  UploadDocumentOptions,
  WaitlistConfirmation,
  WaitlistSubmission,
} from './models.ts';
