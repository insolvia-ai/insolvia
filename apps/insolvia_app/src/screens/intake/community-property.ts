/**
 * §541(a)(2) / Schedule H line 2 — the nine community-property states. A
 * spouse's interest in community property becomes property of the estate
 * regardless of whose name is on it, which is why 106H asks the question at
 * all: `community_household_member` (issue #289) is only worth asking about
 * where this law applies.
 *
 * One place, so a state added or dropped from the list is a one-line change
 * rather than a search across the intake screen.
 */
export const COMMUNITY_PROPERTY_STATES = [
  'AZ',
  'CA',
  'ID',
  'LA',
  'NV',
  'NM',
  'TX',
  'WA',
  'WI',
] as const;

/** Full names for the same nine states — `Address.state` (case-data-model.md)
 * is free text with no picker, so intake may hold either spelling. */
const COMMUNITY_PROPERTY_STATE_NAMES: Readonly<Record<string, string>> = {
  arizona: 'AZ',
  california: 'CA',
  idaho: 'ID',
  louisiana: 'LA',
  nevada: 'NV',
  'new mexico': 'NM',
  texas: 'TX',
  washington: 'WA',
  wisconsin: 'WI',
};

/**
 * Whether a free-text state names one of the nine community-property states,
 * matching a two-letter code or a full name, case- and whitespace-insensitive
 * ("TX", "tx", "Texas" and " Texas " all match).
 */
export function isCommunityPropertyState(state: string | null | undefined): boolean {
  if (typeof state !== 'string') return false;
  const normalized = state.trim().toLowerCase();
  if (normalized === '') return false;
  if (normalized.length === 2) {
    return (COMMUNITY_PROPERTY_STATES as readonly string[]).includes(normalized.toUpperCase());
  }
  return normalized in COMMUNITY_PROPERTY_STATE_NAMES;
}
