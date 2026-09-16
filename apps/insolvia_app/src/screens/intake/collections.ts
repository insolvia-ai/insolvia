import {
  ASSET_CATEGORIES,
  BUSINESS_TYPES,
  CLAIM_CLASSES,
  CONTRACT_LEASE_INTENTIONS,
  DEBTOR_ATTRIBUTION,
  EMPLOYMENT_STATUSES,
  EXCLUDED_INCOME_CATEGORIES,
  EXPENSE_CATEGORIES,
  INTENTIONS,
  LIEN_NATURES,
  NONPRIORITY_TYPES,
  OTHER_INCOME_CATEGORIES,
  PRIORITY_TYPES,
  PROPERTY_TYPES,
  SOFA_ENTRY_TYPES,
  WHICH_HOUSEHOLDS,
} from '@insolvia-ai/api-client';
import type { CaseCollection, SofaEntryType } from '@insolvia-ai/api-client';

// Runtime choice lists the api-client does not (yet) export a constant for —
// `PayFrequency` and `DeductionCategory` are type-only there, because nothing
// consumed them at runtime before this screen. Mirrors
// `insolvia_core.income.PAY_FREQUENCIES`; a mismatch would be caught by the
// server rejecting the choice, the same as any other stale option list.
const PAY_FREQUENCIES = ['weekly', 'biweekly', 'semimonthly', 'monthly', 'other'] as const;

/**
 * What each case collection's form looks like — data, not components.
 *
 * The ten collections (issue #249) are one shape with different fields, so the
 * screen is one component (`CollectionEditor`) driven by these specs rather
 * than ten hand-written forms. What a spec CANNOT express goes in the editor,
 * not here: this module stays renderable data so the whole Chapter 7 field
 * set is reviewable in one file, next to the API modules it mirrors
 * (`services/api/src/insolvia_api/core/*.py` — field keys are the wire names,
 * verbatim).
 *
 * Every field is optional, matching the API: intake is progressive and a
 * half-finished record must save. The specs carry no "required" because the
 * storage layer has no such thing — completeness belongs to the forms engine.
 */

export interface ChoiceOption {
  readonly value: string;
  readonly label: string;
}

export type FieldSpec =
  | { readonly kind: 'text'; readonly key: string; readonly label: string }
  | { readonly kind: 'narrative'; readonly key: string; readonly label: string }
  | { readonly kind: 'money'; readonly key: string; readonly label: string }
  | { readonly kind: 'date'; readonly key: string; readonly label: string }
  | { readonly kind: 'boolean'; readonly key: string; readonly label: string }
  | { readonly kind: 'count'; readonly key: string; readonly label: string }
  | {
      readonly kind: 'choice';
      readonly key: string;
      readonly label: string;
      readonly options: readonly ChoiceOption[];
    }
  | {
      readonly kind: 'multichoice';
      readonly key: string;
      readonly label: string;
      readonly options: readonly ChoiceOption[];
    }
  | { readonly kind: 'address'; readonly key: string; readonly label: string }
  /** A name-and-address block — B107's recurring column. */
  | { readonly kind: 'party'; readonly key: string; readonly label: string }
  /**
   * A REPEATING list of notice-party rows (issue #280) — the id-keyed object
   * list. Each row carries a client-minted `id` (the API requires one, so
   * provenance can address `<key>[<id>].name`), a name, an address and an
   * account-last-four; the editor mints the id on Add, never the spec.
   */
  | {
      readonly kind: 'party-list';
      readonly key: string;
      readonly label: string;
      readonly itemLabel: string;
    }
  /** A plain string list, attributed whole by provenance. */
  | {
      readonly kind: 'strings';
      readonly key: string;
      readonly label: string;
      readonly itemLabel: string;
    }
  /** A list of calendar dates — the SOFA's "Dates" boxes. */
  | { readonly kind: 'dates'; readonly key: string; readonly label: string }
  /** A pick-one-record reference into another collection of the same case. */
  | {
      readonly kind: 'reference';
      readonly key: string;
      readonly label: string;
      readonly refers: CaseCollection;
    }
  /** A pick-many-records reference — one checkbox per record. */
  | {
      readonly kind: 'reference-list';
      readonly key: string;
      readonly label: string;
      readonly refers: CaseCollection;
    }
  /** A pick-one-debtor reference (debtors are not a generic collection). */
  | { readonly kind: 'debtor'; readonly key: string; readonly label: string };

export interface CollectionSpec {
  readonly collection: CaseCollection;
  /** The section name in the intake navigator. */
  readonly title: string;
  /** What one record is called on buttons: "Add creditor". */
  readonly recordName: string;
  /** One line under the section title saying what belongs here. */
  readonly help: string;
  /**
   * The fields of one record. A function of the body because the SOFA's
   * fields depend on the chosen entry type; every other collection ignores
   * the argument.
   */
  readonly fields: (body: Readonly<Record<string, unknown>>) => readonly FieldSpec[];
  /** One line identifying a record in the list, from whatever is filled in. */
  readonly summary: (body: Readonly<Record<string, unknown>>) => string;
  /**
   * A read-only backlink (issue #347): another collection's `reference-list`
   * field that names records of THIS collection — a codebtor's `claim_ids`
   * naming a claim, or `contract_lease_ids` naming a lease. Declared on the
   * record being pointed AT, not the pointer, so the editor can show which
   * codebtors are linked to each claim or lease row, read only.
   */
  readonly linkedFrom?: {
    readonly collection: CaseCollection;
    readonly field: string;
  };
}

/**
 * `homeowners_association_dues` → `Homeowners association dues`.
 *
 * The enum members are named for what the form line asks about, so the name IS
 * the label once unsnaked — and generating it means the app cannot render an
 * option the API would refuse, because the list itself comes from the
 * api-client's runtime array.
 */
export function labelize(value: string): string {
  const words = value.replaceAll('_', ' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}

const options = (values: readonly string[]): readonly ChoiceOption[] =>
  values.map((value) => ({ value, label: labelize(value) }));

const text = (key: string, label: string): FieldSpec => ({ kind: 'text', key, label });
const narrative = (key: string, label: string): FieldSpec => ({ kind: 'narrative', key, label });
const money = (key: string, label: string): FieldSpec => ({ kind: 'money', key, label });
const date = (key: string, label: string): FieldSpec => ({ kind: 'date', key, label });
const yesNo = (key: string, label: string): FieldSpec => ({ kind: 'boolean', key, label });
const choice = (key: string, label: string, values: readonly string[]): FieldSpec => ({
  kind: 'choice',
  key,
  label,
  options: options(values),
});
const multichoice = (key: string, label: string, values: readonly string[]): FieldSpec => ({
  kind: 'multichoice',
  key,
  label,
  options: options(values),
});
const party = (key: string, label: string): FieldSpec => ({ kind: 'party', key, label });
const dates = (key: string, label: string): FieldSpec => ({ kind: 'dates', key, label });

const asText = (value: unknown): string | undefined =>
  typeof value === 'string' && value !== '' ? value : undefined;

/**
 * The per-entry-type payload fields of a SOFA entry, keyed by the SAME names
 * `core/sofa.py`'s parsers read. Keys here are RELATIVE to the payload; the
 * spec below prefixes `payload.`.
 */
const SOFA_PAYLOAD_FIELDS: Readonly<Record<SofaEntryType, readonly FieldSpec[]>> = {
  marital_status: [choice('status', 'Current marital status', ['married', 'not_married'])],
  prior_address: [
    choice('which_debtor', 'Who lived there', ['debtor_1', 'debtor_2', 'both']),
    { kind: 'address', key: 'address', label: 'Address' },
    date('from_date', 'From'),
    date('to_date', 'To'),
  ],
  community_property_residence: [text('state', 'Community property state')],
  income_by_period: [
    choice('which_debtor', 'Whose income', ['debtor_1', 'debtor_2', 'both']),
    choice('kind', 'Source', ['wages_and_commissions', 'operating_a_business', 'other']),
    text('description', 'Describe the source'),
    date('period_start', 'Period start'),
    date('period_end', 'Period end'),
    money('gross_amount', 'Gross amount'),
  ],
  consumer_debt_declaration: [
    yesNo('primarily_consumer_debts', 'Are the debts primarily consumer debts?'),
  ],
  creditor_payment: [
    party('creditor', 'Creditor'),
    dates('dates', 'Dates of payment'),
    money('total_paid', 'Total paid'),
    money('amount_still_owed', 'Amount still owed'),
    multichoice('payment_for', 'The payment was for', [
      'mortgage',
      'car',
      'credit_card',
      'loan_repayment',
      'suppliers_or_vendors',
      'other',
    ]),
    text('payment_for_other', 'Other — specify'),
  ],
  insider_payment: [
    party('insider', 'Insider'),
    text('relationship', 'Relationship to the debtor'),
    dates('dates', 'Dates of payment'),
    money('total_paid', 'Total paid'),
    money('amount_still_owed', 'Amount still owed'),
    narrative('reason', 'Reason for the payment'),
  ],
  insider_benefit_payment: [
    party('recipient', 'Who was paid'),
    text('insider_name', 'Insider who benefited'),
    dates('dates', 'Dates'),
    money('total_paid', 'Total paid'),
    narrative('reason', 'Reason'),
  ],
  lawsuit: [
    text('case_title', 'Case title'),
    text('case_number', 'Case number'),
    text('nature_of_case', 'Nature of the case'),
    party('court', 'Court or agency'),
    choice('status', 'Status', ['pending', 'on_appeal', 'concluded']),
  ],
  repossession: [
    party('creditor', 'Creditor'),
    choice('action', 'What happened', ['repossessed', 'foreclosed', 'garnished', 'attached']),
    narrative('description', 'Describe the property'),
    date('date', 'Date'),
    money('value', 'Value of the property'),
  ],
  setoff: [
    party('creditor', 'Creditor'),
    narrative('description', 'Describe the action the creditor took'),
    date('date', 'Date'),
    money('amount', 'Amount'),
  ],
  receivership: [
    party('custodian', 'Assignee, receiver or custodian'),
    narrative('description', 'Describe the property'),
    money('value', 'Value'),
    text('case_title', 'Case title'),
    text('case_number', 'Case number'),
    party('court', 'Court'),
    date('date', 'Date'),
  ],
  gift: [
    party('recipient', 'Who received the gift'),
    text('relationship', 'Relationship to the debtor'),
    narrative('description', 'Describe the gift'),
    dates('dates', 'Dates given'),
    money('value', 'Value'),
  ],
  charitable_contribution: [
    party('organization', 'Charity'),
    narrative('description', 'Describe the contribution'),
    dates('dates', 'Dates'),
    money('value', 'Value'),
  ],
  loss: [
    narrative('description', 'Describe the property lost and how'),
    narrative('insurance_coverage', 'Insurance coverage, and any claim pending'),
    date('date', 'Date of loss'),
    money('value', 'Value of the loss'),
  ],
  consultant_payment: [
    party('person', 'Who was paid'),
    text('email_or_website', 'Email or website'),
    text('who_made_payment', 'Who paid, if not the debtor'),
    narrative('description', 'Describe the services'),
    date('date', 'Date of payment'),
    money('amount', 'Amount'),
  ],
  creditor_assistance_payment: [
    party('person', 'Who was paid'),
    narrative('description', 'Describe the services'),
    date('date', 'Date of payment'),
    money('amount', 'Amount'),
  ],
  property_transfer: [
    party('transferee', 'Who received the transfer'),
    text('relationship', 'Relationship to the debtor'),
    narrative('description', 'Describe the property transferred'),
    narrative('value_received', 'What was received in exchange'),
    date('date', 'Date of transfer'),
  ],
  self_settled_trust: [
    text('trust_name', 'Name of the trust'),
    narrative('description', 'Describe the property transferred'),
    date('date', 'Date of transfer'),
  ],
  closed_account: [
    party('institution', 'Financial institution'),
    text('account_last4', 'Last four digits'),
    choice('account_type', 'Type of account', [
      'checking',
      'savings',
      'money_market',
      'brokerage',
      'other',
    ]),
    date('date_closed', 'Date closed or transferred'),
    money('last_balance', 'Last balance'),
  ],
  safe_deposit_box: [
    party('institution', 'Institution'),
    { kind: 'strings', key: 'who_has_access', label: 'Who has access', itemLabel: 'Person' },
    narrative('description', 'Describe the contents'),
    yesNo('still_have', 'Do you still have it?'),
  ],
  storage_unit: [
    party('facility', 'Storage facility'),
    { kind: 'strings', key: 'who_has_access', label: 'Who has access', itemLabel: 'Person' },
    narrative('description', 'Describe the contents'),
    yesNo('still_have', 'Do you still have it?'),
  ],
  held_for_another: [
    party('owner', 'Owner of the property'),
    narrative('location', 'Where is the property?'),
    narrative('description', 'Describe the property'),
    money('value', 'Value'),
  ],
  environmental_notice: [
    choice('kind', 'Which direction', ['liability_notice_received', 'release_reported']),
    party('site', 'Site'),
    party('governmental_unit', 'Governmental unit'),
    text('environmental_law', 'Environmental law'),
    date('date', 'Date of notice'),
  ],
  environmental_proceeding: [
    text('case_title', 'Case title'),
    text('case_number', 'Case number'),
    party('court', 'Court or agency'),
    text('nature_of_case', 'Nature of the case'),
    choice('status', 'Status', ['pending', 'on_appeal', 'concluded']),
  ],
  business_connection: [
    party('business', 'Business'),
    text('nature_of_business', 'Nature of the business'),
    text('ein', 'Employer identification number'),
    date('from_date', 'From'),
    date('to_date', 'To'),
    multichoice('connection', 'Connection to the business', [
      'sole_proprietor',
      'partner',
      'officer_or_director',
      'owner_of_5_percent',
    ]),
  ],
  financial_statement_issued: [
    party('recipient', 'Who received the statement'),
    date('date_issued', 'Date issued'),
  ],
};

const isSofaType = (value: unknown): value is SofaEntryType =>
  typeof value === 'string' && (SOFA_ENTRY_TYPES as readonly string[]).includes(value);

/**
 * Every generic collection, in the order the schedules run. The intake screen
 * renders THIS list, so a collection the API grows reaches the UI by adding
 * its spec here — and only here.
 */
export const COLLECTION_SPECS: readonly CollectionSpec[] = [
  {
    collection: 'creditors',
    title: 'Creditors',
    recordName: 'creditor',
    help:
      'One entry per creditor — name and mailing address, as the creditor ' +
      'matrix prints them. The debts themselves go under Claims.',
    fields: () => [
      text('name', 'Creditor name'),
      { kind: 'address', key: 'address', label: 'Address' },
    ],
    summary: (body) => asText(body.name) ?? 'Unnamed creditor',
  },
  {
    collection: 'claims',
    title: 'Claims',
    recordName: 'claim',
    help:
      'One entry per debt. The class decides which schedule it prints on ' +
      '(106D secured, 106E/F priority and nonpriority unsecured).',
    fields: () => [
      { kind: 'reference', key: 'creditor_id', label: 'Creditor', refers: 'creditors' },
      choice('claim_class', 'Class of claim', CLAIM_CLASSES),
      text('account_last4', 'Account number — last four digits'),
      date('date_incurred', 'Date the debt was incurred'),
      money('amount', 'Amount of the claim'),
      yesNo('contingent', 'Contingent'),
      yesNo('unliquidated', 'Unliquidated'),
      yesNo('disputed', 'Disputed'),
      yesNo('subject_to_offset', 'Subject to offset'),
      choice('who_incurred', 'Who incurred the debt', DEBTOR_ATTRIBUTION),
      yesNo('community_debt', 'Community debt'),
      {
        // 106D Part 2 / 106E/F Part 3 — collection agencies, attorneys, and
        // anyone else to be notified about this debt (issue #280). Keyed as
        // core/claims.py's parser reads it, ids minted by the editor.
        kind: 'party-list',
        key: 'notice_parties',
        label: 'Others to be notified about this debt',
        itemLabel: 'notice party',
      },
      // The collateral, by reference first (issue #345): the asset supplies
      // the description and Column B's value, and the secured and unsecured
      // portions are derived server-side — `ClaimCollateralPanel` shows them
      // beside this form. The two typed fields stay as the override, for
      // collateral not on Schedule A/B or a value the preparer prefers.
      {
        kind: 'reference',
        key: 'asset_id',
        label: 'Collateral — property on Schedule A/B (secured)',
        refers: 'assets',
      },
      {
        kind: 'count',
        key: 'lien_position',
        label: 'Lien position — 1 is the senior lien (secured)',
      },
      narrative(
        'collateral_description',
        'Collateral — describe it here only if it is not the property above (secured)',
      ),
      money(
        'collateral_value',
        'Value of the collateral — overrides the property’s value (secured)',
      ),
      multichoice('lien_nature', 'Nature of the lien (secured)', LIEN_NATURES),
      text('lien_nature_other', 'Other lien — specify'),
      money(
        'unsecured_amount_override',
        'Unsecured portion — enter only to override the calculated figure (secured)',
      ),
      choice('intention', 'Statement of intention for this collateral (secured)', INTENTIONS),
      narrative('intention_explanation', 'Intention — explanation'),
      money('priority_amount', 'Priority amount (priority unsecured)'),
      money('nonpriority_amount', 'Nonpriority amount (priority unsecured)'),
      choice('priority_type', 'Type of priority', PRIORITY_TYPES),
      text('priority_type_other', 'Other priority — specify'),
      choice('nonpriority_type', 'Type of nonpriority claim', NONPRIORITY_TYPES),
      text('nonpriority_type_other', 'Other nonpriority — specify'),
    ],
    summary: (body) => {
      const claimClass = asText(body.claim_class);
      const amount = asText(body.amount);
      const parts = [
        claimClass !== undefined ? labelize(claimClass) : undefined,
        amount !== undefined ? `$${amount}` : undefined,
      ].filter((part): part is string => part !== undefined);
      return parts.length > 0 ? parts.join(' — ') : 'New claim';
    },
    // A codebtor names claims it co-signed via `claim_ids` (Schedule H's
    // "Schedule D/E/F, line __" column) — issue #347.
    linkedFrom: { collection: 'codebtors', field: 'claim_ids' },
  },
  {
    collection: 'assets',
    title: 'Property',
    recordName: 'asset',
    help: 'One entry per item of property — every part of Schedule A/B.',
    fields: (body) => [
      choice('category', 'Category', ASSET_CATEGORIES),
      ...(body.category === 'real_property'
        ? [multichoice('property_types', 'What is the property?', PROPERTY_TYPES)]
        : []),
      narrative('description', 'Describe the property'),
      text('county', 'County'),
      money('value_entire', 'Current value of the entire property'),
      money('value_portion_owned', 'Current value of the portion you own'),
      choice('ownership_interest', 'Who has an interest', DEBTOR_ATTRIBUTION),
      text('ownership_interest_description', 'Nature of the ownership interest'),
      yesNo('community_property', 'Is this community property?'),
      narrative('detail', 'Details — make, model, year, institution, account type…'),
    ],
    summary: (body) => {
      const category = asText(body.category);
      return (
        asText(body.description) ?? (category !== undefined ? labelize(category) : 'New asset')
      );
    },
  },
  {
    collection: 'employments',
    title: 'Employment',
    recordName: 'employment',
    help: 'Schedule I, Part 1 — one entry per job, per debtor.',
    fields: () => [
      { kind: 'debtor', key: 'debtor_id', label: 'Whose employment' },
      choice('status', 'Employment status', EMPLOYMENT_STATUSES),
      text('occupation', 'Occupation'),
      text('employer_name', 'Employer'),
      { kind: 'address', key: 'employer_address', label: 'Employer address' },
      date('employed_since', 'Employed since'),
    ],
    summary: (body) => asText(body.employer_name) ?? asText(body.occupation) ?? 'New employment',
  },
  {
    collection: 'pay_period_records',
    title: 'Pay records',
    recordName: 'pay period',
    help:
      'One entry per paycheck, dated — hand-entered here reads the same way ' +
      'to the means test as one pay-stub extraction would have written. ' +
      '`pay_date` is what the six-month lookback keys on.',
    fields: () => [
      { kind: 'reference', key: 'employment_id', label: 'Employer', refers: 'employments' },
      date('period_start', 'Pay period start'),
      date('period_end', 'Pay period end'),
      date('pay_date', 'Date paid'),
      money('gross', 'Gross pay this period'),
      money('net', 'Net pay this period'),
      choice('frequency', 'Pay frequency', PAY_FREQUENCIES),
    ],
    summary: (body) => {
      const payDate = asText(body.pay_date);
      const gross = asText(body.gross);
      const parts = [payDate, gross !== undefined ? `$${gross}` : undefined].filter(
        (part): part is string => part !== undefined,
      );
      return parts.length > 0 ? parts.join(' — ') : 'New pay period';
    },
  },
  {
    collection: 'other_income_records',
    title: 'Other income',
    recordName: 'receipt',
    help:
      'Dated non-wage income — the means test’s other half. An excluded ' +
      'category (Social Security Act benefits, HAVEN Act veterans’ ' +
      'compensation…) is still recorded, so the calculation can show it ' +
      'excluded rather than silently dropping it.',
    fields: () => [
      { kind: 'debtor', key: 'debtor_id', label: 'Whose income' },
      choice('category', 'Category', [...OTHER_INCOME_CATEGORIES, ...EXCLUDED_INCOME_CATEGORIES]),
      date('received_on', 'Date received'),
      money('amount', 'Amount received'),
      money('expenses', 'Operating expenses (business or rental only)'),
      text('payer', 'Payer'),
      narrative('description', 'Description'),
    ],
    summary: (body) => {
      const receivedOn = asText(body.received_on);
      const amount = asText(body.amount);
      const parts = [receivedOn, amount !== undefined ? `$${amount}` : undefined].filter(
        (part): part is string => part !== undefined,
      );
      return parts.length > 0 ? parts.join(' — ') : 'New receipt';
    },
  },
  {
    collection: 'income_summaries',
    title: 'Monthly income',
    recordName: 'income summary',
    help:
      'Schedule I, Part 2 — one column per debtor, estimated as of the filing ' +
      'date. Totals are calculated when the form is rendered, never entered.',
    fields: () => [
      { kind: 'debtor', key: 'debtor_id', label: 'Whose income' },
      money('wages', 'Monthly gross wages, salary and commissions'),
      money('overtime', 'Monthly overtime pay'),
      money('deduction_tax', 'Tax, Medicare and Social Security deductions'),
      money('deduction_mandatory_retirement', 'Mandatory retirement contributions'),
      money('deduction_voluntary_retirement', 'Voluntary retirement contributions'),
      money('deduction_retirement_loan_repayment', 'Retirement loan repayments'),
      money('deduction_insurance', 'Insurance deductions'),
      money('deduction_domestic_support', 'Domestic support obligations'),
      money('deduction_union_dues', 'Union dues'),
      money('deduction_other', 'Other deductions'),
      text('deduction_other_specify', 'Other deductions — specify'),
      money('business_net_income', 'Net income from business, profession, farm or rentals'),
      money('interest_and_dividends', 'Interest and dividends'),
      money('family_support', 'Family support payments received'),
      money('unemployment', 'Unemployment compensation'),
      money('social_security', 'Social Security'),
      money('other_government_assistance', 'Other government assistance'),
      text('other_government_assistance_specify', 'Other government assistance — specify'),
      money('pension_or_retirement', 'Pension or retirement income'),
      money('other_monthly_income', 'Other monthly income'),
      text('other_monthly_income_specify', 'Other monthly income — specify'),
      money('household_contributions', 'Regular contributions to household expenses (line 11)'),
      text('household_contributions_specify', 'Contributions — specify from whom'),
      yesNo('change_expected', 'Do you expect an increase or decrease within the year?'),
      narrative('change_explanation', 'Explain the expected change'),
    ],
    summary: (body) => {
      const wages = asText(body.wages);
      return wages !== undefined ? `Wages $${wages} monthly` : 'New income summary';
    },
  },
  {
    collection: 'households',
    title: 'Households',
    recordName: 'household',
    help:
      'Schedule J’s frame. Add a second household only when debtor 2 ' +
      'maintains a separate one (Schedule J-2).',
    fields: () => [
      choice('which_household', 'Which household', WHICH_HOUSEHOLDS),
      yesNo('separate_household', 'Does debtor 2 live in a separate household?'),
      yesNo('change_expected', 'Do you expect expenses to change within the year?'),
      narrative('change_explanation', 'Explain the expected change'),
    ],
    summary: (body) => {
      const which = asText(body.which_household);
      return which !== undefined ? labelize(which) : 'New household';
    },
  },
  {
    collection: 'expenses',
    title: 'Monthly expenses',
    recordName: 'expense',
    help:
      'Schedule J — one entry per expense line. Totals are calculated when ' +
      'the form is rendered.',
    fields: () => [
      { kind: 'reference', key: 'household_id', label: 'Household', refers: 'households' },
      choice('category', 'Expense', EXPENSE_CATEGORIES),
      text('specify_text', 'Specify'),
      money('amount', 'Monthly amount'),
    ],
    summary: (body) => {
      const category = asText(body.category);
      const amount = asText(body.amount);
      const label = category !== undefined ? labelize(category) : 'New expense';
      return amount !== undefined ? `${label} — $${amount}` : label;
    },
  },
  {
    collection: 'dependents',
    title: 'Dependents',
    recordName: 'dependent',
    help:
      'Relationship, age and residence only — the form does not ask for ' +
      'dependents’ names, so none are stored.',
    fields: () => [
      { kind: 'reference', key: 'household_id', label: 'Household', refers: 'households' },
      text('relationship', 'Relationship to the debtor'),
      { kind: 'count', key: 'age', label: 'Age' },
      yesNo('lives_with_debtor', 'Lives with the debtor?'),
    ],
    summary: (body) => {
      const relationship = asText(body.relationship);
      const age = typeof body.age === 'number' ? `, ${body.age}` : '';
      return relationship !== undefined ? `${relationship}${age}` : 'New dependent';
    },
  },
  {
    collection: 'contract_leases',
    title: 'Contracts and leases',
    recordName: 'contract or lease',
    help:
      'Schedule G — executory contracts and unexpired leases. Intention and ' +
      'the Statement of Intention flag are Form 108’s own columns, entered ' +
      'here so 108 can read them when that form is built.',
    fields: () => [
      // A plain party field for now — the library picker arrives with the
      // creditor library (issue #350).
      text('counterparty_name', 'Other party — name'),
      { kind: 'address', key: 'counterparty_address', label: 'Other party — address' },
      narrative(
        'description',
        'What the contract or lease is for, the nature of the debtor’s ' +
          'interest, the remaining term, and any government contract number',
      ),
      choice('intention', 'Intention', CONTRACT_LEASE_INTENTIONS),
      yesNo('list_on_statement_of_intention', 'List on the Statement of Intention (Form 108)?'),
    ],
    summary: (body) => asText(body.counterparty_name) ?? 'New contract or lease',
    // A codebtor names leases it co-signed via `contract_lease_ids`
    // (Schedule H's "Schedule G, line __" column) — issue #347.
    linkedFrom: { collection: 'codebtors', field: 'contract_lease_ids' },
  },
  {
    collection: 'codebtors',
    title: 'Codebtors',
    recordName: 'codebtor',
    help:
      'Schedule H — anyone else liable on the debtor’s debts. A codebtor ' +
      'without a link to a claim or lease is meaningless on this schedule, ' +
      'so link them to what they co-signed below.',
    fields: () => [
      text('name', 'Codebtor name'),
      { kind: 'address', key: 'address', label: 'Address' },
      { kind: 'reference-list', key: 'claim_ids', label: 'On which claims', refers: 'claims' },
      {
        kind: 'reference-list',
        key: 'contract_lease_ids',
        label: 'On which contracts or leases',
        refers: 'contract_leases',
      },
    ],
    summary: (body) => asText(body.name) ?? 'New codebtor',
  },
  {
    collection: 'community_household_members',
    title: 'Community property household',
    recordName: 'community property household member',
    help:
      'Schedule H line 2 / Form 107 Q3 — a spouse or former spouse in a ' +
      'community property state within the last 8 years. Shown only when ' +
      'the debtor’s state is one of them.',
    fields: () => [
      text('name', 'Name'),
      { kind: 'address', key: 'address', label: 'Address' },
      text('community_state', 'Community property state (two-letter code)'),
      yesNo('lived_with_debtor', 'Lived with the debtor?'),
    ],
    summary: (body) => asText(body.name) ?? 'New household member',
  },
  {
    collection: 'sofa_entries',
    title: 'Financial affairs',
    recordName: 'entry',
    help:
      'The Statement of Financial Affairs (Form 107) — one entry per answer, ' +
      'typed by the question it answers.',
    fields: (body) => {
      const entryType = body.entry_type;
      const payloadFields = isSofaType(entryType) ? SOFA_PAYLOAD_FIELDS[entryType] : [];
      return [
        choice('entry_type', 'What is this entry about?', SOFA_ENTRY_TYPES),
        ...payloadFields.map((field) => ({ ...field, key: `payload.${field.key}` })),
      ];
    },
    summary: (body) => {
      const entryType = asText(body.entry_type);
      return entryType !== undefined ? labelize(entryType) : 'New entry';
    },
  },
  // B101's three repeating lists (issue #342) — the petition screen's own
  // collections, reusing this same CollectionEditor rather than a bespoke
  // list UI for each. The petition body itself and the Part 7 signer block
  // are NOT here: both are one-record-per-case forms, not plain lists, and
  // live in screens/petition.
  {
    collection: 'prior_cases',
    title: 'Prior bankruptcy cases',
    recordName: 'prior case',
    help: 'B101 line 9 — any bankruptcy case filed within the last 8 years.',
    fields: () => [
      text('district', 'District'),
      date('filed_on', 'Date filed'),
      text('case_number', 'Case number'),
    ],
    summary: (body) => {
      const parts = [asText(body.district), asText(body.case_number)].filter(
        (part): part is string => part !== undefined,
      );
      return parts.length > 0 ? parts.join(' — ') : 'New prior case';
    },
  },
  {
    collection: 'related_cases',
    title: 'Related cases',
    recordName: 'related case',
    help: 'B101 line 10 — a pending case by a spouse, partner, or affiliate.',
    fields: () => [
      text('debtor_name', 'Debtor'),
      text('relationship', 'Relationship'),
      text('district', 'District'),
      date('filed_on', 'Date filed'),
      text('case_number', 'Case number'),
    ],
    summary: (body) => asText(body.debtor_name) ?? 'New related case',
  },
  {
    collection: 'sole_proprietorships',
    title: 'Sole proprietorships',
    recordName: 'sole proprietorship',
    help:
      'B101 line 12 — a business the debtor runs as a sole proprietor. ' +
      'The form prints one block; add a second only if the case genuinely has one.',
    fields: () => [
      text('name', 'Business name'),
      { kind: 'address', key: 'address', label: 'Business address' },
      choice('business_type', 'Type of business', BUSINESS_TYPES),
    ],
    summary: (body) => asText(body.name) ?? 'New sole proprietorship',
  },
];
