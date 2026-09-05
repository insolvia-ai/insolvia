"""What a case looks like from above: what it is worth, what it owes, and
whether it could be filed today.

WHY THIS IS NOT COMPUTED IN THE CLIENT. Every figure here is money on a
bankruptcy filing, and the app already had the raw records it would need to add
up. Summing them there would give the firm two answers to "what does this
debtor owe" — one on the case overview and one on Schedule D/E/F — that agree
only for as long as nobody edits either. ADR 0001 puts the reasoning on the
server for exactly this shape of question.

WHERE THE NUMBERS COME FROM, and why they cannot drift. Not from arithmetic
written here: every total is the SAME function the official form's projection
prints from. `secured_total` is what B106D line 1 totals, the two unsecured
totals are what B106E/F lines 6e and 6j total, and the asset pair is B106A/B
lines 55 and 62. B106Sum — the court's own summary form — is built the same way
and says why:

    'Every line is a copy or a sum of another schedule, so this module stores
     nothing and asks the other modules' shared helpers instead — the same
     functions their own projections print from, which is what keeps the
     summary incapable of disagreeing with the schedules it summarises.'

This module inherits that property. If a figure here is wrong, the filed form
is wrong in the same way, which is a real bug and not a display discrepancy.

WHAT IS DELIBERATELY ABSENT: an activity feed. The obvious source is the access
log, and it is unreadable by design — `infra/modules/case_store` grants this
service PutItem on that table and nothing else, because an audit log its own
subject can rewrite is not evidence. Synthesising one instead from record
timestamps would answer a different question ("what changed") under a name
users read as the audited one ("who did what"), and would silently omit every
read. That is a decision to take deliberately, not a field to add here.
"""

from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal

from .form_projections.b106ab import personal_property_total, real_estate_total
from .form_projections.b106d import secured_total
from .form_projections.b106ef import (
    nonpriority_unsecured_total,
    priority_unsecured_total,
)
from .packet_assembly import (
    CaseData,
    PacketProblem,
    completeness_problems,
    to_case_file,
)


@dataclass(frozen=True)
class CaseTotals:
    """The money, as Decimals. The route renders them; nothing here formats.

    Both subtotal pairs are kept alongside their sum rather than replaced by
    it. A paralegal reads "secured / priority / nonpriority", not one
    liabilities figure, and the three are what the schedules actually print —
    collapsing them here would make this the one place in the system that
    states a number no form does.
    """

    real_estate: Decimal
    personal_property: Decimal
    assets: Decimal
    secured: Decimal
    priority_unsecured: Decimal
    nonpriority_unsecured: Decimal
    liabilities: Decimal


@dataclass(frozen=True)
class CaseSummary:
    """One case, from above."""

    totals: CaseTotals
    #: Empty means this case could assemble its packet today.
    problems: tuple[PacketProblem, ...]

    @property
    def ready_to_file(self) -> bool:
        return not self.problems


def summarise(data: CaseData) -> CaseSummary:
    """The summary of one already-loaded case.

    A pure function of `CaseData`, like the projections it delegates to, so it
    is testable without a store and cannot reach for a record the caller did
    not read. The caller does the loading — `read_case_data` — because that is
    where the store dependencies already live.

    NOTE this reports readiness by running the SAME completeness gate packet
    assembly runs, not a cheaper approximation of it. A case overview that says
    "ready to file" over a case the assembler then refuses is worse than one
    that says nothing, and the only way to be sure the two agree is for them to
    be the same code.
    """
    case_file = to_case_file(data)

    real_estate = real_estate_total(case_file)
    personal_property = personal_property_total(case_file)
    secured = secured_total(case_file)
    priority = priority_unsecured_total(case_file)
    nonpriority = nonpriority_unsecured_total(case_file)

    return CaseSummary(
        totals=CaseTotals(
            real_estate=real_estate,
            personal_property=personal_property,
            assets=real_estate + personal_property,
            secured=secured,
            priority_unsecured=priority,
            nonpriority_unsecured=nonpriority,
            liabilities=secured + priority + nonpriority,
        ),
        problems=completeness_problems(data),
    )
