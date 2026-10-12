import type { CalendarEvent } from '@insolvia-ai/api-client';

import {
  addDays,
  addMonths,
  daysBetween,
  eventsOn,
  formatLong,
  monthGrid,
  startOfWeek,
  step,
  windowFor,
  windowTitle,
} from './dates';

function event(over: Partial<CalendarEvent>): CalendarEvent {
  return {
    id: 'e',
    title: 'x',
    start: '2026-03-02',
    end: '2026-03-02',
    all_day: true,
    attendees: [],
    generated: false,
    dismissed: false,
    created_by: 's',
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
    ...over,
  };
}

/**
 * Known answers, worked against a printed 2026 calendar — a grid that starts
 * on the wrong weekday is still a plausible calendar.
 */
describe('the calendar’s date arithmetic', () => {
  it('adds days across a month end and a leap day', () => {
    expect(addDays('2026-02-27', 3)).toBe('2026-03-02');
    expect(addDays('2028-02-28', 1)).toBe('2028-02-29');
    expect(addDays('2026-01-01', -1)).toBe('2025-12-31');
  });

  it('steps months without overshooting a short month', () => {
    expect(addMonths('2026-01-31', 1)).toBe('2026-02-01');
    expect(addMonths('2026-12-15', 1)).toBe('2027-01-01');
    expect(addMonths('2026-01-15', -1)).toBe('2025-12-01');
  });

  it('starts a week on Monday', () => {
    // 2026-03-04 is a Wednesday; 2026-03-08 a Sunday; 2026-03-02 a Monday.
    expect(startOfWeek('2026-03-04')).toBe('2026-03-02');
    expect(startOfWeek('2026-03-08')).toBe('2026-03-02');
    expect(startOfWeek('2026-03-02')).toBe('2026-03-02');
  });

  it('computes each view’s window', () => {
    expect(windowFor('day', '2026-03-04')).toEqual({ from: '2026-03-04', to: '2026-03-04' });
    expect(windowFor('week', '2026-03-04')).toEqual({ from: '2026-03-02', to: '2026-03-08' });
    expect(windowFor('month', '2026-03-04')).toEqual({ from: '2026-03-01', to: '2026-03-31' });
    expect(windowFor('agenda', '2026-03-04')).toEqual({ from: '2026-03-04', to: '2026-05-02' });
  });

  it('steps each view by its own stride', () => {
    expect(step('day', '2026-03-31', 1)).toBe('2026-04-01');
    expect(step('week', '2026-03-04', -1)).toBe('2026-02-25');
    expect(step('month', '2026-03-31', 1)).toBe('2026-04-01');
    expect(step('agenda', '2026-03-04', 1)).toBe('2026-05-03');
  });

  it('lays March 2026 out as six Monday-to-Sunday rows', () => {
    // The 1st is a Sunday, so the first row is almost all February and the
    // 31st (a Tuesday) needs a sixth — a five-row assumption is the bug a
    // grid like this hides.
    const grid = monthGrid('2026-03-15');
    expect(grid).toHaveLength(6);
    expect(grid[0]?.[0]).toBe('2026-02-23');
    expect(grid[0]?.[6]).toBe('2026-03-01');
    expect(grid[5]?.[6]).toBe('2026-04-05');
    expect(grid.every((row) => row.length === 7)).toBe(true);
  });

  it('lists the days between two dates inclusively', () => {
    expect(daysBetween('2026-03-30', '2026-04-02')).toEqual([
      '2026-03-30',
      '2026-03-31',
      '2026-04-01',
      '2026-04-02',
    ]);
  });

  it('places an all-day span on every day it covers, and a timed one on its local day', () => {
    const span = event({ id: 'span', start: '2026-03-02', end: '2026-03-04' });
    const timed = event({
      id: 'timed',
      all_day: false,
      start: '2026-03-03T15:00:00Z',
      end: '2026-03-03T16:00:00Z',
    });
    expect(eventsOn([timed, span], '2026-03-03').map((e) => e.id)).toEqual(['span', 'timed']);
    expect(eventsOn([timed, span], '2026-03-05')).toEqual([]);
  });

  it('reads a date and a window as a person would', () => {
    expect(formatLong('2026-03-02')).toBe('March 2, 2026');
    expect(windowTitle('month', '2026-03-02')).toBe('March 2026');
    expect(windowTitle('week', '2026-03-04')).toBe('Mon, Mar 2 – Sun, Mar 8');
    expect(formatLong('not-a-date')).toBe('not-a-date');
  });
});
