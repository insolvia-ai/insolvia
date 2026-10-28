// Raw wire shapes the suite asserts more than once, transcribed from
// packages/insolvia_api_client/src/models.ts (the field names and JSON types
// it declares). A key ending in `?` may be ABSENT; it is never `null` unless
// its type says so.
import type { Spec } from './wire.ts';

export const ADDRESS: Spec = {
  'line1?': 'string',
  'line2?': 'string',
  'city?': 'string',
  'state?': 'string',
  'postal_code?': 'string',
  'county?': 'string',
};

export const SIGNATURE_BLOCK: Spec = {
  'bar_number?': 'string',
  'bar_state?': 'string',
  'firm_name?': 'string',
  'address?': ADDRESS,
  'phone?': 'string',
  'email?': 'string',
};

export const FIRM: Spec = {
  id: 'string',
  name: 'string',
  status: 'string',
  createdAt: 'string',
  updatedAt: 'string',
  defaultCourt: 'string|null',
  defaultDivision: 'string|null',
  defaultChapter: 'integer|null',
  letterhead: 'object|null',
};

export const PERMISSIONS: Spec = {
  cases: 'string',
  intake: 'string',
  documents: 'string',
};

export const FIRM_USER: Spec = {
  subject: 'string',
  email: 'string',
  firstName: 'string',
  lastName: 'string',
  displayName: 'string',
  role: 'string',
  isAdmin: 'boolean',
  accessAllCases: 'boolean',
  permissions: PERMISSIONS,
  status: 'string',
  createdAt: 'string',
  updatedAt: 'string',
  signatureBlock: 'object|null',
};

export const CASE: Spec = {
  id: 'string',
  createdBy: 'string',
  chapter: 'integer',
  district: 'string',
  'court?': 'string',
  'division?': 'string',
  status: 'string',
  createdAt: 'string',
  updatedAt: 'string',
  'formRevisions?': 'object',
  'constantsSetId?': 'string',
  'exemptionSet?': 'object',
  'filedAt?': 'string',
  'meeting341At?': 'string',
  'caseNumber?': 'string',
  'judge?': 'string',
  'trustee?': 'string',
  'officeFileNumber?': 'string',
  'archivedAt?': 'string',
  'archivedBy?': 'string',
};

export const PERSON_NAME: Spec = {
  'given?': 'string',
  'middle?': 'string',
  'surname?': 'string',
  'suffix?': 'string',
};

export const FIRM_CLIENT: Spec = {
  id: 'string',
  name: PERSON_NAME,
  status: 'string',
  created_at: 'string',
  updated_at: 'string',
  'prospect_stage?': 'string',
  'email?': 'string',
  'phone?': 'string',
  'residence_address?': ADDRESS,
  'tax_id_last_four?': 'string',
  'merged_into?': 'string',
  created_by: 'string',
};

export const DEBTOR: Spec = {
  id: 'string',
  case_id: 'string',
  filing_role: 'string',
  'client_id?': 'string',
  'name?': PERSON_NAME,
  'tax_id?': { kind: 'string', last_four: 'string' },
  'provenance?': 'object',
  'differs_from_client?': ['string'],
  created_at: 'string',
  updated_at: 'string',
};

export const DOCUMENT: Spec = {
  id: 'string',
  caseId: 'string',
  kind: 'string',
  fileName: 'string',
  contentType: 'string',
  byteSize: 'integer',
  uploadedAt: 'string',
  status: 'string',
  channel: 'string',
  requestId: 'string|null',
};

export const TASK: Spec = {
  id: 'string',
  caseId: 'string',
  subject: 'string',
  'description?': 'string',
  'dueDate?': 'string',
  'assigneeSubject?': 'string',
  'formSeries?': 'string',
  done: 'boolean',
  overdue: 'boolean',
  'completedAt?': 'string',
  createdAt: 'string',
  updatedAt: 'string',
  createdBy: 'string',
};

export const CALENDAR_EVENT: Spec = {
  id: 'string',
  'case_id?': 'string',
  title: 'string',
  'description?': 'string',
  start: 'string',
  end: 'string',
  all_day: 'boolean',
  'location?': 'string',
};

export const NOTE: Spec = {
  id: 'string',
  case_id: 'string',
  'text?': 'string',
  'form_series?': 'string',
  author_subject: 'string',
  author_name: 'string',
  created_at: 'string',
  updated_at: 'string',
};
