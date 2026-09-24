/**
 * The calendar's date arithmetic (issue 14.6 / #358): which days a view
 * shows, which days an event covers, and how a date reads.
 *
 * A SMALL PURE MODULE ON PURPOSE, like the petition screen's
 * `statutory-dates.ts` and for the same reason: a month grid that starts on
 * the wrong weekday or an event that lands a day off is a plausible-looking
 * calendar, and nobody notices until a hearing is missed. No date library —
 * the arithmetic is a few lines, and ADR 0004 asks a dependency to earn its
 * place.
 *
 * TWO KINDS OF TIME, kept apart. A form date (`YYYY-MM-DD`) is a calendar
 * fact with no zone — a hearing on the 14th is on the 14th wherever the
 * reader sits — and is computed in UTC so every browser agrees. A timed
 * event's instant (`...Z`) is placed on the reader's LOCAL day, because a
 * 3pm Eastern call is a 2pm call to a paralegal in Chicago and belongs on
 * whichever day that is for them.
 */

import type { CalendarEvent } from '@insolvia-ai/api-client';

export type CalendarView = 'day' | 'week' | 'month' | 'agenda';

export const VIEWS: readonly { readonly value: CalendarView; readonly label: string }[] = [
  { value: 'day', label: 'Day' },
  { value: 'week', label: 'Week' },
  { value: 'month', label: 'Month' },
  { value: 'agenda', label: 'Agenda' },
];

/** How far ahead the agenda looks from its anchor day. */
export const AGENDA_DAYS = 60;

const ISO_DATE_RE = /^(\d{4})-(\d{2})-(\d{2})$/u;
const DAY_MS = 24 * 60 * 60 * 1000;

/** A strict `YYYY-MM-DD` as a UTC-midnight `Date`, or null. */
export function parseFormDate(value: string): Date | null {
  const match = ISO_DATE_RE.exec(value);
  if (match === null) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const parsed = new Date(Date.UTC(year, month - 1, day));
  const roundTrips =
    parsed.getUTCFullYear() === year &&
    parsed.getUTCMonth() === month - 1 &&
    parsed.getUTCDate() === day;
  return roundTrips ? parsed : null;
}

export function toFormDate(date: Date): string {
  const year = String(date.getUTCFullYear()).padStart(4, '0');
  const month = String(date.getUTCMonth() + 1).padStart(2, '0');
  const day = String(date.getUTCDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

/** Today, as the reader's local calendar date. */
export function today(now: Date = new Date()): string {
  const year = String(now.getFullYear()).padStart(4, '0');
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

export function addDays(date: string, days: number): string {
  const parsed = parseFormDate(date);
  if (parsed === null) return date;
  return toFormDate(new Date(parsed.getTime() + days * DAY_MS));
}

export function addMonths(date: string, months: number): string {
  const parsed = parseFormDate(date);
  if (parsed === null) return date;
  // Pinned to the 1st first, so January 31 + 1 month is February, not March.
  const first = new Date(Date.UTC(parsed.getUTCFullYear(), parsed.getUTCMonth() + months, 1));
  return toFormDate(first);
}

/** 0 = Monday … 6 = Sunday, the order the grid's columns read in. */
function weekdayIndex(date: Date): number {
  return (date.getUTCDay() + 6) % 7;
}

/** The Monday on or before `date`. */
export function startOfWeek(date: string): string {
  const parsed = parseFormDate(date);
  if (parsed === null) return date;
  return addDays(date, -weekdayIndex(parsed));
}

export function startOfMonth(date: string): string {
  return `${date.slice(0, 7)}-01`;
}

export function endOfMonth(date: string): string {
  return addDays(addMonths(startOfMonth(date), 1), -1);
}

/** The inclusive window a view shows around its anchor day. */
export function windowFor(view: CalendarView, anchor: string): { from: string; to: string } {
  switch (view) {
    case 'day':
      return { from: anchor, to: anchor };
    case 'week': {
      const from = startOfWeek(anchor);
      return { from, to: addDays(from, 6) };
    }
    case 'month':
      return { from: startOfMonth(anchor), to: endOfMonth(anchor) };
    case 'agenda':
      return { from: anchor, to: addDays(anchor, AGENDA_DAYS - 1) };
  }
}

/** Where the previous/next controls take the anchor. */
export function step(view: CalendarView, anchor: string, direction: 1 | -1): string {
  switch (view) {
    case 'day':
      return addDays(anchor, direction);
    case 'week':
      return addDays(anchor, 7 * direction);
    case 'month':
      return addMonths(anchor, direction);
    case 'agenda':
      return addDays(anchor, AGENDA_DAYS * direction);
  }
}

/** Every day from `from` to `to`, inclusive. */
export function daysBetween(from: string, to: string): readonly string[] {
  const days: string[] = [];
  for (let day = from; day <= to; day = addDays(day, 1)) {
    days.push(day);
    if (days.length > 400) break; // the API's own cap; never loops forever
  }
  return days;
}

/**
 * The month grid: whole weeks, Monday to Sunday, from the week holding the
 * 1st to the week holding the last day — five or six rows.
 */
export function monthGrid(anchor: string): readonly (readonly string[])[] {
  const first = startOfWeek(startOfMonth(anchor));
  const last = endOfMonth(anchor);
  const rows: string[][] = [];
  for (let weekStart = first; weekStart <= last; weekStart = addDays(weekStart, 7)) {
    rows.push(daysBetween(weekStart, addDays(weekStart, 6)) as string[]);
  }
  return rows;
}

/** The reader's local calendar date of a UTC instant. */
export function localDayOf(instant: string): string {
  const parsed = new Date(instant);
  return Number.isNaN(parsed.getTime()) ? instant.slice(0, 10) : today(parsed);
}

/** The first and last day an event touches, as the reader sees them. */
export function eventSpan(event: CalendarEvent): { first: string; last: string } {
  if (event.all_day) return { first: event.start, last: event.end };
  return { first: localDayOf(event.start), last: localDayOf(event.end) };
}

export function eventTouches(event: CalendarEvent, day: string): boolean {
  const span = eventSpan(event);
  return span.first <= day && day <= span.last;
}

/** The events touching `day`, all-day ones first, then by start. */
export function eventsOn(events: readonly CalendarEvent[], day: string): readonly CalendarEvent[] {
  return events
    .filter((event) => eventTouches(event, day))
    .sort((a, b) => {
      if (a.all_day !== b.all_day) return a.all_day ? -1 : 1;
      return a.start < b.start ? -1 : a.start > b.start ? 1 : 0;
    });
}

/** `2026-03-02` → "March 2, 2026". Falls back to the raw value. */
export function formatLong(date: string): string {
  const parsed = parseFormDate(date);
  if (parsed === null) return date;
  return new Intl.DateTimeFormat('en-US', {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
    timeZone: 'UTC',
  }).format(parsed);
}

/** `2026-03-02` → "Mon, Mar 2". */
export function formatShort(date: string): string {
  const parsed = parseFormDate(date);
  if (parsed === null) return date;
  return new Intl.DateTimeFormat('en-US', {
    weekday: 'short',
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
  }).format(parsed);
}

/** `2026-03` → "March 2026". */
export function formatMonth(date: string): string {
  const parsed = parseFormDate(startOfMonth(date));
  if (parsed === null) return date;
  return new Intl.DateTimeFormat('en-US', {
    year: 'numeric',
    month: 'long',
    timeZone: 'UTC',
  }).format(parsed);
}

/** A timed event's local clock time, "3:00 PM"; nothing for an all-day one. */
export function formatTime(event: CalendarEvent): string | null {
  if (event.all_day) return null;
  const parsed = new Date(event.start);
  if (Number.isNaN(parsed.getTime())) return null;
  return new Intl.DateTimeFormat('en-US', { hour: 'numeric', minute: '2-digit' }).format(parsed);
}

/** What a view's header says for its window. */
export function windowTitle(view: CalendarView, anchor: string): string {
  const { from, to } = windowFor(view, anchor);
  switch (view) {
    case 'day':
      return formatLong(anchor);
    case 'month':
      return formatMonth(anchor);
    case 'week':
    case 'agenda':
      return `${formatShort(from)} – ${formatShort(to)}`;
  }
}
