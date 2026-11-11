// Wire shapes the API's clients do not name, for the OpenAPI documents
// (render-openapi.ts). Most of what the API sends and accepts is already a
// named type in packages/insolvia_api_client/src/models.ts; a route table
// names those directly. A type belongs here only when no client type is the
// wire shape: the client reshapes the answer, a route has no client method,
// or a body is built inline in client.ts.
//
// Keep each one to what the server's handler writes or reads. The contract
// suite checks every exchange it makes against the documents, so a field
// written here wrongly fails `contract/scripts/dev-test.sh`.

import type {
  CaseChapter,
  CaseCollection,
  CaseEntity,
  CaseEntityRequest,
  CaseStatus,
  ExemptionSet,
  ProvenanceEntry,
} from '../../packages/insolvia_api_client/src/models.ts';
import type { FirmSummary } from '../../packages/insolvia_admin_client/src/client.ts';

/** `GET /health` — liveness, and which build answered. */
export interface Health {
  readonly status: string;
  readonly service: string;
  readonly version: string;
  readonly environment: string;
}

/** A record in one of a case's collections, with the server's own fields. */
export type AnyCaseEntity = CaseEntity<CaseCollection>;

/** A record in one of a case's collections, as written. */
export type AnyCaseEntityRequest = CaseEntityRequest<CaseCollection>;

/**
 * One collection's records, under the collection's own name:
 * `GET /v1/cases/{caseId}/creditors` answers `{"creditors": [...]}`.
 */
export type CaseEntityList = Readonly<Record<string, readonly AnyCaseEntity[]>>;

/** `POST /v1/me/calendar-token` — the new feed token and the path that carries it. */
export interface CalendarTokenMinted {
  readonly token: string;
  /** `/v1/me/calendar.ics?token=…`, relative to the API's base URL. */
  readonly feedPath: string;
}

/**
 * One event in a case's lifecycle history (ADR 0027), oldest first. The
 * optional members are present only on the kinds they belong to.
 */
export interface LifecycleEvent {
  readonly kind: 'status' | 'conversion' | 'confirmation' | 'confirmation_denied';
  /** The court date the event takes effect, `YYYY-MM-DD`; `null` when it has none. */
  readonly effectiveOn: string | null;
  readonly recordedAt: string;
  /** The subject of the person who recorded it. */
  readonly recordedBy: string;
  readonly provenance: ProvenanceEntry;
  readonly candidateId?: string;
  readonly fromStatus?: CaseStatus;
  readonly toStatus?: CaseStatus;
  readonly chapter?: CaseChapter;
  readonly filingId?: string;
  readonly fromChapter?: CaseChapter;
  readonly toChapter?: CaseChapter;
  readonly planVersionId?: string;
}

/**
 * `POST /v1/cases` as sent. The client's `CreateCaseRequest` says
 * `clientIds`; the wire says `client_ids`.
 */
export interface CreateCaseBody {
  readonly chapter: CaseChapter;
  /** The court's CM/ECF code (`"flmb"`), from `GET /v1/courts`. */
  readonly court: string;
  /** A division of that court. */
  readonly division: string;
  /** The firm clients the case is for: Debtor 1, then Debtor 2 on a joint case. */
  readonly client_ids: readonly string[];
}

/**
 * `PATCH /v1/cases/{caseId}` as sent: any subset, each key absent to leave it
 * unchanged and `null` (where allowed) to clear it. The client's
 * `UpdateCaseChanges` nests the court and camel-cases the rest.
 */
export interface UpdateCaseBody {
  readonly chapter?: CaseChapter;
  /** Sent together with `division`. */
  readonly court?: string;
  readonly division?: string;
  readonly status?: CaseStatus;
  readonly exemption_set?: ExemptionSet;
  /** `YYYY-MM-DD`. */
  readonly filed_at?: string | null;
  /** The 341 meeting, ISO 8601. */
  readonly meeting_341_at?: string | null;
  readonly case_number?: string | null;
  readonly judge?: string | null;
  readonly trustee?: string | null;
  readonly office_file_number?: string | null;
}

/** The admin service's firm without its user count: what a write answers. */
export type FirmRecord = Omit<FirmSummary, 'userCount'>;
