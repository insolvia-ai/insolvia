import { isCommunityPropertyState } from './community-property';

describe('isCommunityPropertyState', () => {
  it('matches a two-letter code, case-insensitively', () => {
    expect(isCommunityPropertyState('TX')).toBe(true);
    expect(isCommunityPropertyState('tx')).toBe(true);
  });

  it('matches a full state name, with surrounding whitespace', () => {
    expect(isCommunityPropertyState(' Texas ')).toBe(true);
    expect(isCommunityPropertyState('new mexico')).toBe(true);
  });

  it('rejects a non-community-property state', () => {
    expect(isCommunityPropertyState('FL')).toBe(false);
    expect(isCommunityPropertyState('Florida')).toBe(false);
  });

  it('rejects absent or empty input', () => {
    expect(isCommunityPropertyState(undefined)).toBe(false);
    expect(isCommunityPropertyState(null)).toBe(false);
    expect(isCommunityPropertyState('')).toBe(false);
    expect(isCommunityPropertyState('   ')).toBe(false);
  });
});
