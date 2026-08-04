// WEB LEAF — plain React DOM + Tailwind, WAI-ARIA tabs (automatic activation).
// Shares its entire state model with the native leaf via tabs.props; what
// lives here is the DOM: tablist/tab/tabpanel roles, aria-selected/-controls/
// -labelledby, roving tabindex, and arrow-key focus management that also
// activates the tab it moves to (Home/End jump to the ends and activate too).
import * as React from 'react';

import { cn } from '../lib/cn';
import { disabledStyles, focusRing } from '../lib/styles';
import {
  TabsRootContext,
  getPanelId,
  getTabId,
  useTabsRootContext,
  useTabsState,
  type TabsRootOwnProps,
} from './tabs.props';

// `defaultValue` is Omit-ed from the div props because React's own
// `HTMLAttributes` already declares it (as a form-control value type), and
// the tabs' string meaning must win — same reasoning as Accordion.Root.
export interface TabsRootProps
  extends Omit<React.ComponentPropsWithoutRef<'div'>, 'defaultValue'>, TabsRootOwnProps {}

const TabsRoot = React.forwardRef<HTMLDivElement, TabsRootProps>(
  ({ className, value, defaultValue, onValueChange, children, ...props }, ref) => {
    const ctx = useTabsState(value, defaultValue, onValueChange);
    return (
      <TabsRootContext.Provider value={ctx}>
        <div ref={ref} className={cn('flex flex-col', className)} {...props}>
          {children}
        </div>
      </TabsRootContext.Provider>
    );
  },
);
TabsRoot.displayName = 'Tabs.Root';

const TabsList = React.forwardRef<HTMLDivElement, React.ComponentPropsWithoutRef<'div'>>(
  ({ className, ...props }, ref) => (
    <div
      ref={ref}
      role="tablist"
      data-tabs-list=""
      className={cn('flex flex-row', className)}
      {...props}
    />
  ),
);
TabsList.displayName = 'Tabs.List';

export interface TabProps extends React.ComponentPropsWithoutRef<'button'> {
  /** Which panel this tab activates; matched against the same `value` on `Panel`. */
  value: string;
}

const Tab = React.forwardRef<HTMLButtonElement, TabProps>(
  ({ className, value, onClick, onKeyDown, ...props }, ref) => {
    const { value: activeValue, setValue, rootId } = useTabsRootContext('Tab');
    const active = activeValue === value;
    const tabId = getTabId(rootId, value);
    const panelId = getPanelId(rootId, value);

    const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
      onKeyDown?.(event);
      if (event.defaultPrevented) return;
      const nav = ['ArrowRight', 'ArrowLeft', 'Home', 'End'];
      if (!nav.includes(event.key)) return;
      const list = event.currentTarget.closest<HTMLElement>('[data-tabs-list]');
      if (!list) return;
      event.preventDefault();
      const tabs = Array.from(list.querySelectorAll<HTMLButtonElement>('[data-tabs-tab]'));
      const i = tabs.indexOf(event.currentTarget);
      const target =
        event.key === 'ArrowRight'
          ? tabs[(i + 1) % tabs.length]
          : event.key === 'ArrowLeft'
            ? tabs[(i - 1 + tabs.length) % tabs.length]
            : event.key === 'Home'
              ? tabs[0]
              : tabs[tabs.length - 1];
      if (!target) return;
      target.focus();
      // Automatic activation: moving focus to a tab also activates it.
      const targetValue = target.getAttribute('data-tabs-tab');
      if (targetValue !== null) setValue(targetValue);
    };

    return (
      <button
        ref={ref}
        type="button"
        role="tab"
        id={tabId}
        data-tabs-tab={value}
        aria-selected={active}
        aria-controls={panelId}
        tabIndex={active ? 0 : -1}
        onClick={(event) => {
          onClick?.(event);
          if (!event.defaultPrevented) setValue(value);
        }}
        onKeyDown={handleKeyDown}
        className={cn(
          'flex cursor-pointer items-center gap-sm border-b-2 px-md py-sm font-body text-sm font-medium',
          active ? 'border-primary text-ink' : 'border-transparent text-muted',
          focusRing,
          disabledStyles,
          className,
        )}
        {...props}
      />
    );
  },
);
Tab.displayName = 'Tabs.Tab';

export interface TabPanelProps extends React.ComponentPropsWithoutRef<'div'> {
  /** Which tab activates this panel; matched against the same `value` on `Tab`. */
  value: string;
}

const TabPanel = React.forwardRef<HTMLDivElement, TabPanelProps>(
  ({ className, value, children, ...props }, ref) => {
    const { value: activeValue, rootId } = useTabsRootContext('Panel');
    const active = activeValue === value;
    if (!active) return null;

    const tabId = getTabId(rootId, value);
    const panelId = getPanelId(rootId, value);

    return (
      <div
        ref={ref}
        id={panelId}
        role="tabpanel"
        aria-labelledby={tabId}
        tabIndex={0}
        className={cn('py-md font-body text-sm text-ink', className)}
        {...props}
      >
        {children}
      </div>
    );
  },
);
TabPanel.displayName = 'Tabs.Panel';

export const Tabs = {
  Root: TabsRoot,
  List: TabsList,
  Tab,
  Panel: TabPanel,
};
