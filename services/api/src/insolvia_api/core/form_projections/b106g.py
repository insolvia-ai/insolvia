"""B106G @ 2015-12-01 (revision 12/15) — Schedule G's mapping.

Executory contracts and unexpired leases in creation order onto the
thirteen printed rows: counterparty name and address, and the one
description box that carries what the contract is for. Row numbering stays
blank for packet assembly, like every other schedule's.
"""

from __future__ import annotations

from ..form_fill import Text
from ..form_templates import FormRelease
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    full_name,
    row_fill,
    text_or_none,
    yes_no,
)


def project_b106g_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
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

    contracts = [body for _, body in case_file.contract_leases]
    values["line_1_any_contracts"] = yes_no(
        release, "line_1_any_contracts", bool(contracts)
    )
    for index, contract in enumerate(contracts):
        row_fill(
            release,
            values,
            "line_2_counterparty_name",
            index,
            text_or_none(contract.counterparty_name),
            problems,
        )
        for field_id, part in (
            ("line_2_counterparty_street", contract.counterparty_address.line1),
            ("line_2_counterparty_city", contract.counterparty_address.city),
            ("line_2_counterparty_state", contract.counterparty_address.state),
            ("line_2_counterparty_zip", contract.counterparty_address.postal_code),
        ):
            row_fill(release, values, field_id, index, text_or_none(part), problems)
        row_fill(
            release,
            values,
            "line_2_contract_description",
            index,
            text_or_none(contract.description),
            problems,
        )

    if problems:
        raise FormProjectionError(sorted(problems))
    return values
