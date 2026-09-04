"""B106C @ 2025-04-01 (revision 04/25) — Schedule C's mapping.

One row per claimed exemption; the property description and value are the
referenced ASSET's (the model stores no copy). Two answers draw on the
exemptions registry (core/exemptions.py):

- **Line 1** — which § 522(b) set is claimed. In an opt-out state the law
  forces the answer (§ 522(b)(3), the "state and federal nonbankruptcy"
  box), so the projection derives it from the debtor's state via
  `schemes_for_state`. Where the state allows the federal election the
  choice is the debtor's own fact; the model assigns it to
  `case.exemption_set`, which code has not grown yet, so the box stays
  blank until it does.
- **Line 3** — the § 522(q) homestead-cap question. The cap is a registry
  figure (`us-homestead-misconduct-cap`), never a constant in code; the
  claimed homestead is the exemptions on real-property assets, a full-FMV
  claim counting at the asset's portion-owned value. The 1,215-day
  follow-up prints only on a yes, from the homestead exemption's own
  stored answer.

Both registry reads resolve as of the case's `created_at` date — the
stand-in for a filing date until `case.filed_at` and the pinned
`constants_set_id` land (the registry read is configuration access, and it
is deterministic over the case file, which keeps the projection pure).
"""

from __future__ import annotations

from datetime import date
from decimal import Decimal

from insolvia_core.exemption_claims import ExemptionBody

from ..exemptions import federal_limits, schemes_for_state
from ..form_fill import Option, Text
from ..form_templates import FormRelease
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    amount,
    format_money,
    full_name,
    row_fill,
    yes_no,
)

_HOMESTEAD_CAP_LIMIT_ID = "us-homestead-misconduct-cap"


def _as_of(case_file: CaseFile) -> date:
    """The registry resolution date: the case's creation date, standing in
    for the filing date until `case.filed_at` exists."""
    return date.fromisoformat(case_file.case.created_at[:10])


def _exemption_set_export(case_file: CaseFile) -> str | None:
    """Line 1's export where the opt-out rule forces it; None otherwise."""
    debtor1 = case_file.debtor("debtor_1")
    state = debtor1.residence_address.state if debtor1 is not None else None
    if not state:
        return None
    try:
        schemes = schemes_for_state(state, _as_of(case_file))
    except (KeyError, LookupError):
        # Outside the launch set, or before the series' baseline — the
        # completeness gate surfaces "unsupported state"; nothing to force.
        return None
    if len(schemes) == 1:
        # Opted out: § 522(b)(3) is the only box the law allows.
        return "state and federal"
    return None


def _claimed_amount(exemption: ExemptionBody, case_file: CaseFile) -> Decimal:
    if exemption.claims_full_fmv:
        asset = case_file.asset(exemption.asset_id)
        return amount(asset.value_portion_owned if asset else None)
    return amount(exemption.amount)


def _homestead_answers(case_file: CaseFile) -> tuple[bool, bool | None]:
    """(claiming more than the § 522(q) cap?, acquired within 1,215 days?)"""
    homesteads = [
        exemption
        for exemption in case_file.exemptions
        if (asset := case_file.asset(exemption.asset_id)) is not None
        and asset.category == "real_property"
    ]
    cap = next(
        (
            Decimal(limit.amount)
            for limit in federal_limits(_as_of(case_file))
            if limit.limit_id == _HOMESTEAD_CAP_LIMIT_ID
        ),
        None,
    )
    claimed = sum(
        (_claimed_amount(exemption, case_file) for exemption in homesteads),
        Decimal("0"),
    )
    over_cap = cap is not None and claimed > cap
    acquired: bool | None = None
    answers = [
        e.acquired_within_1215_days
        for e in homesteads
        if e.acquired_within_1215_days is not None
    ]
    if answers:
        acquired = any(answers)
    return over_cap, acquired


def project_b106c_0425(release: FormRelease, case_file: CaseFile) -> FieldValues:
    problems: list[str] = []
    values: FieldValues = {}

    values["caption.district"] = Text(case_file.case.district)
    for role, field_id in (
        ("debtor_1", "caption.debtor1_name"),
        ("debtor_2", "caption.debtor2_name"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and (name := full_name(debtor.name)):
            values[field_id] = Text(name)

    if (export := _exemption_set_export(case_file)) is not None:
        values["line_1_exemption_set"] = Option(export)

    for index, exemption in enumerate(case_file.exemptions):
        asset = case_file.asset(exemption.asset_id)
        if asset is not None and asset.description:
            row_fill(
                release,
                values,
                "line_2_property_description",
                index,
                Text(asset.description),
                problems,
            )
        # The "line from Schedule A/B" column is packet assembly's: the row
        # a property prints on is decided when the packet lays pages out.
        if asset is not None and asset.value_portion_owned is not None:
            row_fill(
                release,
                values,
                "line_2_current_value",
                index,
                Text(format_money(asset.value_portion_owned)),
                problems,
            )
        if exemption.claims_full_fmv is not None:
            row_fill(
                release,
                values,
                "line_2_exemption_kind",
                index,
                Option("fair market" if exemption.claims_full_fmv else "On"),
                problems,
            )
        if not exemption.claims_full_fmv and exemption.amount is not None:
            row_fill(
                release,
                values,
                "line_2_exemption_amount",
                index,
                Text(format_money(exemption.amount)),
                problems,
            )
        if exemption.statute_citation:
            row_fill(
                release,
                values,
                "line_2_statute_citation",
                index,
                Text(exemption.statute_citation),
                problems,
            )

    over_cap, acquired = _homestead_answers(case_file)
    values["line_3_homestead_over_cap"] = yes_no(
        release, "line_3_homestead_over_cap", over_cap
    )
    if over_cap and acquired is not None:
        values["line_3_acquired_within_1215_days"] = yes_no(
            release, "line_3_acquired_within_1215_days", acquired
        )

    if problems:
        raise FormProjectionError(sorted(problems))
    return values
