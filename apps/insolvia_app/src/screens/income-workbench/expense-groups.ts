import type { ExpenseBody, ExpenseCategory } from '@insolvia-ai/api-client';

import { sumMoney } from './money';

/**
 * Schedule J's ~30 lines (`insolvia_core.expenses.EXPENSE_CATEGORIES`),
 * clustered into named groups for the workbench display only — this
 * grouping is not a form concept and prints nowhere; `core/form_projections
 * /b106j.py`'s own line map is the filed shape, one row per category. Every
 * category appears in exactly one group, checked by this module's own test
 * against the api-client's live category list so a category the API grows
 * cannot go unrouted.
 */
export interface ExpenseGroupSpec {
  readonly name: string;
  readonly categories: readonly ExpenseCategory[];
}

export const EXPENSE_GROUPS: readonly ExpenseGroupSpec[] = [
  {
    name: 'Housing',
    categories: [
      'rent_or_home_ownership',
      'real_estate_taxes',
      'property_insurance',
      'home_maintenance',
      'homeowners_association_dues',
      'additional_mortgage_payments',
    ],
  },
  {
    name: 'Utilities',
    categories: [
      'electricity_heat_gas',
      'water_sewer_garbage',
      'telephone_and_internet',
      'other_utilities',
    ],
  },
  {
    name: 'Daily living',
    categories: [
      'food_and_housekeeping',
      'childcare_and_education',
      'clothing_and_laundry',
      'personal_care',
    ],
  },
  // Shown beside the IRS Local/National Standards health-care allowance.
  { name: 'Health care', categories: ['medical_and_dental', 'health_insurance'] },
  // Shown beside the IRS Local Standards transportation allowance.
  {
    name: 'Transportation',
    categories: ['transportation', 'vehicle_insurance', 'vehicle_installment_payments'],
  },
  { name: 'Insurance', categories: ['life_insurance', 'other_insurance'] },
  {
    name: 'Obligations',
    categories: [
      'taxes',
      'alimony_and_support_payments',
      'support_of_others',
      'other_installment_payments',
    ],
  },
  {
    name: 'Other property',
    categories: [
      'other_property_mortgages',
      'other_property_taxes',
      'other_property_insurance',
      'other_property_maintenance',
      'other_property_association_dues',
    ],
  },
  {
    name: 'Discretionary',
    categories: ['entertainment_and_recreation', 'charitable_contributions'],
  },
  { name: 'Other', categories: ['other'] },
];

export interface ExpenseGroupTotal {
  readonly name: string;
  readonly total: string;
  readonly recordCount: number;
}

export function groupExpenseTotals(expenses: readonly ExpenseBody[]): readonly ExpenseGroupTotal[] {
  return EXPENSE_GROUPS.map((group) => {
    const matching = expenses.filter(
      (expense) => expense.category !== undefined && group.categories.includes(expense.category),
    );
    return {
      name: group.name,
      total: sumMoney(matching.map((expense) => expense.amount)),
      recordCount: matching.length,
    };
  });
}
