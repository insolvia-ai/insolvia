import type { Case, ProspectStage } from '@insolvia-ai/api-client';
import type { BadgeIntent } from '@insolvia-ai/design-system';

/**
 * How a case's own status reads, rather than the wire's snake_case, and the
 * badge it wears — THE one mapping, shared by the case list, a client's
 * record, the case's own header, the dashboard's recent cases and the
 * overview's lifecycle panel, which show the same cases and should not
 * disagree about what "ready_to_file" is called. Exhaustive over the
 * lifecycle (issue #355), so a status added there cannot ship unnamed.
 */
export const CASE_STATUS_LABEL: Readonly<Record<Case['status'], string>> = {
  prospect: 'Prospect',
  intake: 'In intake',
  ready_to_file: 'Ready to file',
  filed: 'Filed',
  discharged: 'Discharged',
  dismissed: 'Dismissed',
  closed: 'Closed',
};

export const CASE_STATUS_INTENT: Readonly<Record<Case['status'], BadgeIntent>> = {
  prospect: 'warning',
  intake: 'neutral',
  ready_to_file: 'success',
  filed: 'primary',
  discharged: 'success',
  dismissed: 'danger',
  closed: 'neutral',
};

/** Where a prospect sits in the funnel, as a person says it. */
export const PROSPECT_STAGE_LABEL: Readonly<Record<ProspectStage, string>> = {
  possible: 'Possible',
  consultation_scheduled: 'Consultation scheduled',
  awaiting_signed_agreement: 'Awaiting signed agreement',
  exhausted: 'Exhausted',
};
