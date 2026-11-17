"""B106H @ 2015-12-01 (revision 12/15) — Schedule H's mapping.

Codebtors in creation order onto the eleven printed rows. The three
which-schedule checkboxes derive from what the codebtor's stored links
resolve to: a `claim_ids` entry whose claim is secured checks Schedule D,
an unsecured one checks E/F, and any `contract_lease_ids` entry checks G —
the line-number boxes themselves stay blank for packet assembly, which
owns row placement. Line 2's community-property block prints ONE
spouse-or-former-spouse; a second `community_household_member` record is
an overflow error, not a truncation.
"""

from __future__ import annotations

from ..form_fill import Check, Text
from ..form_templates import FormRelease
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    address_fills,
    full_name,
    row_fill,
    text_or_none,
    yes_no,
)


def project_b106h_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
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

    values["line_1_any_codebtors"] = yes_no(
        release, "line_1_any_codebtors", bool(case_file.codebtors)
    )

    # Line 2 — the community-property lookback; the form prints one block.
    members = case_file.community_household_members
    values["line_2_lived_in_community_state"] = yes_no(
        release, "line_2_lived_in_community_state", bool(members)
    )
    if len(members) > 1:
        problems.append(
            "line_2: the form prints one community-property block; "
            f"the case holds {len(members)} household members"
        )
    if members:
        member = members[0]
        if member.community_state:
            values["line_2_community_state"] = Text(member.community_state)
        if member.lived_with_debtor is not None:
            values["line_2_spouse_lived_with_you"] = yes_no(
                release, "line_2_spouse_lived_with_you", member.lived_with_debtor
            )
        if member.name:
            values["line_2_spouse_name"] = Text(member.name)
        address_fills(
            values,
            member.address,
            street="line_2_spouse_street",
            street2=None,
            city="line_2_spouse_city",
            state="line_2_spouse_state",
            zip_code="line_2_spouse_zip",
        )

    claim_classes = {id_: body.claim_class for id_, body in case_file.claims}
    contract_ids = {id_ for id_, _ in case_file.contract_leases}

    for index, codebtor in enumerate(case_file.codebtors):
        row_fill(
            release,
            values,
            "line_3_codebtor_name",
            index,
            text_or_none(codebtor.name),
            problems,
        )
        for field_id, part in (
            ("line_3_codebtor_street", codebtor.address.line1),
            ("line_3_codebtor_city", codebtor.address.city),
            ("line_3_codebtor_state", codebtor.address.state),
            ("line_3_codebtor_zip", codebtor.address.postal_code),
        ):
            row_fill(release, values, field_id, index, text_or_none(part), problems)

        linked = [claim_classes.get(claim_id) for claim_id in codebtor.claim_ids]
        if "secured" in linked:
            row_fill(
                release, values, "line_3_schedule_d_applies", index, Check(), problems
            )
        if "priority_unsecured" in linked or "nonpriority_unsecured" in linked:
            row_fill(
                release, values, "line_3_schedule_ef_applies", index, Check(), problems
            )
        # A dangling claim id resolves to no class; the completeness gate
        # flags it, and no schedule box is guessed here.
        if any(cl_id in contract_ids for cl_id in codebtor.contract_lease_ids):
            row_fill(
                release, values, "line_3_schedule_g_applies", index, Check(), problems
            )

    if problems:
        raise FormProjectionError(sorted(problems))
    return values
