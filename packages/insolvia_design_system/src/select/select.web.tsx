// WEB LEAF — plain React DOM + Tailwind. The WAI-ARIA select-only combobox:
// a `role="combobox"` button that owns focus the whole time, plus a
// `role="listbox"` popup addressed through `aria-activedescendant`. Focus never
// enters the list, which is what lets one keydown handler on the button drive
// everything and keeps the grammar in select.props identical on both leaves.
//
// It reads Field's context when there is one, so `<Field.Root>` + `<Select>`
// wires label, description and invalid state exactly as `<Field.Control>` does.
// Optional on purpose — a Select outside a Field still works, it just has to be
// given its own `aria-label`/`aria-labelledby`.
import * as React from 'react';

import { FieldContext } from '../field/field.props';
import { cn } from '../lib/cn';
import { focusRing } from '../lib/styles';
import {
  getListboxId,
  getOptionId,
  selectKeyIntent,
  useSelectState,
  useTypeahead,
  type SelectOwnProps,
} from './select.props';

export interface SelectProps
  extends
    Omit<React.ComponentPropsWithoutRef<'button'>, 'value' | 'defaultValue' | 'onChange'>,
    SelectOwnProps {}

export const Select = React.forwardRef<HTMLButtonElement, SelectProps>(
  (
    {
      className,
      options,
      value,
      defaultValue,
      onValueChange,
      placeholder = 'Select…',
      disabled: disabledProp = false,
      name: nameProp,
      onKeyDown,
      onBlur,
      ...props
    },
    ref,
  ) => {
    const field = React.useContext(FieldContext);
    const state = useSelectState({ options, value, defaultValue, onValueChange });
    const { open, setOpen, active, setActive, commit, selected, rootId } = state;

    const activeRef = React.useRef(active);
    activeRef.current = active;
    const typeahead = useTypeahead(
      options,
      React.useCallback(() => activeRef.current, []),
    );

    const rootRef = React.useRef<HTMLDivElement | null>(null);
    const listRef = React.useRef<HTMLUListElement | null>(null);

    // Clicking anywhere outside commits nothing and closes — the behaviour of
    // every native select, and the reason this listens on `mousedown` rather
    // than `click`: a click that starts outside and ends inside should still
    // count as leaving.
    React.useEffect(() => {
      if (!open) return;
      const onPointerDown = (event: MouseEvent) => {
        if (rootRef.current?.contains(event.target as Node)) return;
        setOpen(false);
      };
      document.addEventListener('mousedown', onPointerDown);
      return () => document.removeEventListener('mousedown', onPointerDown);
    }, [open, setOpen]);

    // Keep the highlighted option visible in a scrolling list. `block: 'nearest'`
    // so it scrolls only when it has to, rather than jumping on every arrow.
    React.useEffect(() => {
      if (!open || active === null) return;
      const el = listRef.current?.querySelector<HTMLElement>(
        `[data-select-option="${CSS.escape(active)}"]`,
      );
      // Guarded because jsdom does not implement it — and because scrolling is
      // a nicety, so an environment without it should render, not throw.
      if (typeof el?.scrollIntoView === 'function') el.scrollIntoView({ block: 'nearest' });
    }, [open, active]);

    const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
      onKeyDown?.(event);
      if (event.defaultPrevented) return;
      const intent = selectKeyIntent(event.key, event.altKey, {
        open,
        options,
        value: state.value,
        active,
        typing: typeahead.isTyping(),
      });
      if (intent.kind === 'none') return;

      // Tab must keep moving focus, so it is the one intent that does NOT
      // swallow the event; everything else would otherwise scroll the page
      // (arrows, Space) or submit the form (Enter).
      if (event.key !== 'Tab') event.preventDefault();

      switch (intent.kind) {
        case 'open':
          setOpen(true);
          setActive(intent.active);
          break;
        case 'close':
          setOpen(false);
          break;
        case 'active':
          setActive(intent.active);
          break;
        case 'commit':
          commit(intent.value);
          break;
        case 'typeahead': {
          const match = typeahead.push(intent.char);
          if (match === null) break;
          if (open) setActive(match);
          // Typing while closed picks the option outright, like a native
          // select — no popup, no extra keystroke to confirm.
          else commit(match);
          break;
        }
      }
    };

    const disabled = disabledProp;
    const listboxId = getListboxId(rootId);
    const invalid = field?.invalid ?? false;
    const name = nameProp ?? field?.name;

    return (
      <div ref={rootRef} className="relative">
        <button
          ref={ref}
          type="button"
          role="combobox"
          id={field?.controlId}
          aria-expanded={open}
          aria-controls={listboxId}
          aria-haspopup="listbox"
          // Only while open: it must name an element that exists.
          aria-activedescendant={open && active !== null ? getOptionId(rootId, active) : undefined}
          aria-describedby={field?.describedBy}
          aria-invalid={invalid ? true : undefined}
          disabled={disabled}
          onClick={() => {
            if (open) setOpen(false);
            else {
              setOpen(true);
              setActive(state.value ?? null);
            }
          }}
          onKeyDown={handleKeyDown}
          onBlur={(event) => {
            onBlur?.(event);
            // Focus leaving the widget entirely closes it. Clicking an option
            // cannot get here — the list preventDefaults its own mousedown.
            if (!rootRef.current?.contains(event.relatedTarget as Node | null)) setOpen(false);
          }}
          className={cn(
            'flex h-10 w-full items-center justify-between gap-sm rounded-md border border-line bg-card px-sm',
            'font-body text-sm text-ink',
            focusRing,
            'disabled:cursor-not-allowed disabled:bg-surface-alt disabled:text-muted',
            invalid && 'border-danger',
            className,
          )}
          {...props}
        >
          <span className={cn('truncate', selected ? 'text-ink' : 'text-muted')}>
            {selected?.label ?? placeholder}
          </span>
          <span aria-hidden="true" className="text-muted">
            ▾
          </span>
        </button>

        {/* A real form control so an ordinary form post carries the value —
            the button cannot, and the app's fetch-based submit does not care
            either way. Omitted entirely when there is no name to submit. */}
        {name === undefined ? null : <input type="hidden" name={name} value={state.value ?? ''} />}

        {open && (
          <ul
            ref={listRef}
            id={listboxId}
            role="listbox"
            aria-label={typeof props['aria-label'] === 'string' ? props['aria-label'] : undefined}
            // Keeping mousedown from doing its default is what stops the button
            // blurring when an option is clicked — without it the blur handler
            // closes the list before the click lands.
            onMouseDown={(event) => event.preventDefault()}
            className={cn(
              'absolute z-10 mt-xs max-h-60 w-full overflow-auto rounded-md border border-line bg-card py-xs',
              'font-body text-sm text-ink shadow-lg',
            )}
          >
            {options.map((option) => {
              const isSelected = option.value === state.value;
              return (
                <li
                  key={option.value}
                  id={getOptionId(rootId, option.value)}
                  role="option"
                  data-select-option={option.value}
                  aria-selected={isSelected}
                  aria-disabled={option.disabled ? true : undefined}
                  onMouseEnter={() => {
                    if (!option.disabled) setActive(option.value);
                  }}
                  onClick={() => {
                    if (!option.disabled) commit(option.value);
                  }}
                  className={cn(
                    'cursor-pointer px-sm py-xs',
                    option.value === active && 'bg-surface-alt',
                    isSelected && 'font-medium',
                    option.disabled && 'cursor-not-allowed text-muted',
                  )}
                >
                  {option.label}
                </li>
              );
            })}
          </ul>
        )}
      </div>
    );
  },
);
Select.displayName = 'Select';
