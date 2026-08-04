// SHARED — imports `react` and `../lib/controllable` only. No react-dom, no
// react-native. This is the tabs' entire behavior model: which tab is active,
// how selecting one mutates that (controlled or uncontrolled, via the shared
// useControllableState helper), and the id-composition rule that links a Tab
// to its Panel. Both leaves consume it verbatim; they differ only in the
// elements and a11y wiring rendered around it — and web is the only platform
// that needs the derived ids at all (native links Tab/Panel purely through
// `value` equality, no DOM id/aria-* concepts exist there), but the derivation
// stays here anyway because it is a pure string rule, not renderer-specific.
import * as React from 'react';

import { useControllableState } from '../lib/controllable';

export interface TabsRootContextValue {
  value: string;
  setValue: (next: string) => void;
  /** Root `React.useId()` — the seed for the web leaf's tab/panel id pairs. */
  rootId: string;
}

export const TabsRootContext = React.createContext<TabsRootContextValue | null>(null);

export function useTabsRootContext(part: string): TabsRootContextValue {
  const ctx = React.useContext(TabsRootContext);
  if (!ctx) throw new Error(`Tabs.${part} must be rendered inside <Tabs.Root>`);
  return ctx;
}

export interface TabsRootOwnProps {
  /** The active tab's value. Supplying this makes Tabs controlled — pair it
   * with `onValueChange` and update it yourself in response. */
  value?: string;
  /**
   * The tab active on first render, AND the seed `useControllableState` needs
   * even in controlled mode. Required rather than optional: Tabs does not
   * auto-activate the first `Tab` on mount (there is no reliable "first tab"
   * to discover from `Root` alone — `List`'s children are the caller's, not
   * introspected here), so an uncontrolled `<Tabs.Root>` with no
   * `defaultValue` would start with nothing selected and no way to reach that
   * state again short of a click. Requiring it is the simplest behavior that
   * is always correct; auto-selecting "the first one" would need to reach
   * into `List`'s children, which is exactly the kind of cross-leaf
   * introspection this package avoids (see Field's `index.ts` note).
   */
  defaultValue: string;
  /** Fires with the value of the newly activated tab, in both modes. */
  onValueChange?: ((next: string) => void) | undefined;
}

/**
 * The active-tab state machine — pure React, identical on both platforms.
 * Controlled when `value` is supplied, uncontrolled otherwise.
 */
export function useTabsState(
  value: string | undefined,
  defaultValue: string,
  onValueChange: ((next: string) => void) | undefined,
): TabsRootContextValue {
  const [current, setCurrent] = useControllableState(value, defaultValue, onValueChange);
  const rootId = React.useId();
  return React.useMemo(() => ({ value: current, setValue: setCurrent, rootId }), [
    current,
    setCurrent,
    rootId,
  ]);
}

/**
 * A Tab/Panel pair for the same `value` always derives matching ids from the
 * shared root id — pure string rule, web-only consumer, but kept here rather
 * than duplicated because both the Tab and the Panel leaf need to agree on it
 * independently (neither renders the other).
 */
export function getTabId(rootId: string, value: string): string {
  return `${rootId}-tab-${value}`;
}

export function getPanelId(rootId: string, value: string): string {
  return `${rootId}-panel-${value}`;
}
