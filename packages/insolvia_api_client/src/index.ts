// A hand-written, typed TypeScript client for the Insolvia API
// (`services/api`), usable from the Expo app, scripts, or any other host with
// a platform `fetch`.
//
// The JSON contract it encodes is pinned by this package's tests, not by
// codegen — see README.md for why there is no OpenAPI step. Keep every wire
// literal in this package in sync with:
//   services/api/src/insolvia_api/api/routes/{health,waitlist,me,cases,documents}.py
//   services/api/src/insolvia_api/api/routes/{health,waitlist,me,cases,debtors}.py
//   services/api/src/insolvia_api/api/routes/case_entities.py (+ core/case_*.py)
//   services/api/src/insolvia_api/api/app_factory.py (error handlers)
//   services/api/src/insolvia_api/api/auth.py (the 401 body)
//   services/api/src/insolvia_api/core/{waitlist,auth,documents}.py
//   services/api/src/insolvia_api/core/{waitlist,auth,cases,debtors,provenance}.py
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
  addFirmUserRequestToJson,
  caseEntityRequestToJson,
  createCaseRequestToJson,
  createDocumentRequestToJson,
  isDocumentContentType,
  isDocumentKind,
  listCasesQuery,
  putDebtorRequestToJson,
  staffTypedProvenance,
  submittedAtUtc,
  permits,
  updateCaseChangesToJson,
  updateFirmRequestToJson,
  updateFirmUserRequestToJson,
  updateMeRequestToJson,
  waitlistSubmissionToJson,
} from './models.ts';
// The four debtor enums are exported as VALUES, not only as types: each one is
// a picker in the questionnaire, so the app needs the options at runtime, and
// re-declaring them there would put the same list in two repositories of truth.
export {
  COUNSELING_EXEMPTIONS,
  COUNSELING_STATUSES,
  FILING_ROLES,
  PROVENANCE_SOURCES,
  VENUE_BASES,
} from './models.ts';
// The case-collection enums (issue #249), values for the same reason: each is
// a picker or a checkbox set in the intake UI, mirrored from the API's core
// modules — the tests pin them member for member.
export {
  ASSET_CATEGORIES,
  CASE_COLLECTIONS,
  CLAIM_CLASSES,
  DEBTOR_ATTRIBUTION,
  EMPLOYMENT_STATUSES,
  EXPENSE_CATEGORIES,
  LIEN_NATURES,
  NONPRIORITY_TYPES,
  PRIORITY_TYPES,
  PROPERTY_TYPES,
  SOFA_ENTRY_TYPES,
  WHICH_HOUSEHOLDS,
} from './models.ts';
export type {
  AssetBody,
  AssetCategory,
  CaseCollection,
  CaseCollections,
  CaseEntity,
  CaseEntityRequest,
  ClaimBody,
  ClaimClass,
  CodebtorBody,
  CreditorBody,
  DebtorAttribution,
  DependentBody,
  EmploymentBody,
  EmploymentStatus,
  ExpenseBody,
  ExpenseCategory,
  FormDate,
  HouseholdBody,
  IncomeSummaryBody,
  LienNature,
  Money,
  NonpriorityType,
  NoticeParty,
  PriorityType,
  PropertyType,
  SofaEntryBody,
  SofaEntryType,
  SofaPayload,
  WhichHousehold,
} from './models.ts';
export type {
  AddFirmUserRequest,
  Address,
  Case,
  CaseAssignee,
  CaseChapter,
  CaseStatus,
  CounselingExemption,
  CounselingStatus,
  CreateCaseRequest,
  CreateDocumentRequest,
  CreateDocumentResult,
  Document,
  DocumentContentType,
  DocumentDownload,
  DocumentKind,
  DocumentStatus,
  DocumentUpload,
  CreditCounseling,
  Debtor,
  DebtorBody,
  DebtorBodyLike,
  FilingRole,
  Firm,
  FirmColleague,
  FirmFeature,
  FirmMembership,
  FirmRole,
  FirmStatus,
  FirmUser,
  FirmUserStatus,
  HealthStatus,
  ListCasesOptions,
  ListCasesResult,
  OtherName,
  PersonName,
  Principal,
  ProvenanceEntry,
  ProvenanceMap,
  PermissionLevel,
  ProvenanceSource,
  PutDebtorRequest,
  UpdateCaseChanges,
  UpdateFirmRequest,
  UpdateFirmUserRequest,
  UpdateMeRequest,
  UploadDocumentOptions,
  Venue,
  VenueBasis,
  WaitlistConfirmation,
  WaitlistSubmission,
} from './models.ts';
