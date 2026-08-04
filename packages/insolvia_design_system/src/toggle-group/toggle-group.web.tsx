// WEB LEAF — plain React DOM + Tailwind. A single part, Root: a
// `role="group"` container that provides ToggleGroupContext to any `Toggle`
// rendered inside it (see toggle.props's useToggleGroupContext) and marks
// itself with `data-toggle-group-root` — the element the toggle leaf's
// ArrowLeft/ArrowRight keydown handler climbs to via `closest()` before
// querying `[data-toggle-item]` siblings, the same idiom
// accordion.web.tsx uses for its trigger navigation.
import * as React from 'react';

import { cn } from '../lib/cn';
import {
  ToggleGroupContext,
  useToggleGroupState,
  type ToggleGroupRootOwnProps,
} from './toggle-group.props';

// `defaultValue` is Omit-ed from the div props for the same reason
// Accordion's Root Omits it: this component's own `string[]` meaning must
// win over the built-in HTML attribute of the same name.
export interface ToggleGroupRootProps
  extends Omit<React.ComponentPropsWithoutRef<'div'>, 'defaultValue'>, ToggleGroupRootOwnProps {}

const ToggleGroupRoot = React.forwardRef<HTMLDivElement, ToggleGroupRootProps>(
  (
    {
      className,
      value,
      defaultValue,
      onValueChange,
      multiple = false,
      disabled = false,
      children,
      ...props
    },
    ref,
  ) => {
    const ctx = useToggleGroupState(value, defaultValue, onValueChange, multiple, disabled);
    return (
      <ToggleGroupContext.Provider value={ctx}>
        <div
          ref={ref}
          role="group"
          data-toggle-group-root=""
          className={cn('inline-flex gap-xs', className)}
          {...props}
        >
          {children}
        </div>
      </ToggleGroupContext.Provider>
    );
  },
);
ToggleGroupRoot.displayName = 'ToggleGroup.Root';

export const ToggleGroup = { Root: ToggleGroupRoot };
