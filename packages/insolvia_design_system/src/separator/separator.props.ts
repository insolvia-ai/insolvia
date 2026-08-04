// SHARED — no react-dom / react-native / base-ui import. Separator has no
// open/closed behavior, no ids, no context to share — it is a single
// non-compound part. This module is just the orientation type both leaves key
// off; per the litmus test in the package CLAUDE.md, pure data like this is
// allowed to live in one shared file instead of forking into two copies.
export type SeparatorOrientation = 'horizontal' | 'vertical';

export interface SeparatorOwnProps {
  /** Defaults to horizontal. */
  orientation?: SeparatorOrientation;
}
