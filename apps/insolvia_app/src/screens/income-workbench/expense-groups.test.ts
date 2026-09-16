import { EXPENSE_CATEGORIES } from '@insolvia-ai/api-client';
import type { ExpenseBody } from '@insolvia-ai/api-client';

import { EXPENSE_GROUPS, groupExpenseTotals } from './expense-groups';

describe('EXPENSE_GROUPS', () => {
  test('every category the API defines is routed to exactly one group', () => {
    // The invariant this screen depends on: a category the API grows and
    // this file forgets to route would silently vanish from every total.
    const routed = EXPENSE_GROUPS.flatMap((group) => group.categories);
    expect(new Set(routed).size).toBe(routed.length); // no category twice
    expect([...routed].sort()).toEqual([...EXPENSE_CATEGORIES].sort());
  });
});

describe('groupExpenseTotals', () => {
  function expense(category: string, amount: string): ExpenseBody {
    return { category: category as ExpenseBody['category'], amount };
  }

  test('sums same-group categories together', () => {
    const totals = groupExpenseTotals([
      expense('electricity_heat_gas', '150.00'),
      expense('water_sewer_garbage', '50.00'),
    ]);

    const utilities = totals.find((group) => group.name === 'Utilities');
    expect(utilities?.total).toBe('200.00');
    expect(utilities?.recordCount).toBe(2);
  });

  test('a group with nothing entered totals zero, not omitted', () => {
    const totals = groupExpenseTotals([]);

    expect(totals.every((group) => group.total === '0.00')).toBe(true);
    expect(totals.length).toBe(EXPENSE_GROUPS.length);
  });

  test('an expense with no category is not counted in any group', () => {
    const totals = groupExpenseTotals([{ amount: '75.00' }]);

    expect(totals.reduce((sum, group) => sum + group.recordCount, 0)).toBe(0);
  });

  test('health care and transportation are their own groups', () => {
    // The two lines the IRS standards sit beside (issue #348) — this pins
    // that they stay addressable by name for that display.
    const names = groupExpenseTotals([]).map((group) => group.name);
    expect(names).toContain('Health care');
    expect(names).toContain('Transportation');
  });
});
