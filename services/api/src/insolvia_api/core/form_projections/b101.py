"""B101 @ 2024-06-22 (revision 06/24) — the voluntary petition's mapping.

What deliberately does NOT project, each blank until its owner lands: tax
identifiers (line 3 — encrypted storage with audited reads is its own work),
the amended-filing caption (no `case.is_amended` yet), wet-signature lines
(never machine-filled), the court's case number, and the pro se
acknowledgment boxes (the filer's own act).
"""

from __future__ import annotations

import re
from typing import Final

from insolvia_core.petitions import (
    BUSINESS_TYPES,
    ESTIMATED_CREDITORS_BANDS,
    ESTIMATED_DOLLAR_BANDS,
    SMALL_BUSINESS_STATUSES,
)

from ..form_fill import Option, Text
from ..form_templates import FormRelease
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    address_fills,
    by_order,
    format_date,
    full_name,
    rows,
    wrap_lines,
    yes_no,
)

_CHAPTER_EXPORTS: Final = {
    7: "Chapter 7",
    11: "Chapter 11",
    12: "Chapter 12",
    13: "Chapter 13",
}

_FEE_HANDLING_EXPORTS: Final = {
    "full": "Pay entirely",
    "installments": "Pay in installments",
    "waiver": "Request fees waived",
}

_VENUE_EXPORTS: Final = {
    "lived_longest_180_days": "Lived in this district",
    "other": "Other",
}

# B101 line 15's exports, verified against the PDF's widget geometry at
# ingestion (the release manifest records the check).
_COUNSELING_EXPORTS: Final = {
    "completed_with_certificate": "1",
    "completed_certificate_pending": "2",
    "exigent_circumstances_waiver_requested": "On",
    "not_required": "4",
}

_COUNSELING_EXEMPTION_EXPORTS: Final = {
    "incapacity": "Incapacity",
    "disability": "Disability",
    "active_duty": "Active duty",
}


def project_b101_0624(release: FormRelease, case_file: CaseFile) -> FieldValues:
    problems: list[str] = []
    values: FieldValues = {}
    case = case_file.case
    debtor1 = case_file.debtor("debtor_1")
    debtor2 = case_file.debtor("debtor_2")  # B101 never prints a non-filing spouse

    values["caption.district"] = Text(case.district)
    values["caption.chapter"] = Option(_CHAPTER_EXPORTS[case.chapter])
    if debtor1 is not None and (header := full_name(debtor1.name)):
        values["caption.header_debtor1_name"] = Text(header)

    # Part 1 — identity, per debtor column.
    for debtor, col in ((debtor1, "debtor1"), (debtor2, "debtor2")):
        if debtor is None:
            continue
        name = debtor.name
        for part, box in (
            ("given", "first_name"),
            ("middle", "middle_name"),
            ("surname", "last_name"),
            ("suffix", "suffix"),
        ):
            if value := getattr(name, part, None):
                values[f"line_1_{col}_{box}"] = Text(value)

        # Line 2 — the 8-year alias rows (the form prints two per debtor).
        aliases = debtor.other_names_used
        for suffix_id, attr in (
            ("other_given", "given"),
            ("other_middle", "middle"),
            ("other_surname", "surname"),
            ("business_name", "business_name"),
        ):
            rows(
                release,
                values,
                f"line_2_{col}_{suffix_id}",
                [getattr(alias, attr, None) for alias in aliases],
                problems,
            )

        # Line 4 — EINs (two rows per debtor). The boxes are 9-character
        # digit combs, so the customary dash is stripped on the way in.
        rows(
            release,
            values,
            f"line_4_{col}_ein",
            [re.sub(r"\D", "", ein) or None for ein in debtor.employer_ids],
            problems,
        )

        # Line 5 — where the debtor lives; mailing only when it differs.
        address_fills(
            values,
            debtor.residence_address,
            street=f"line_5_{col}_street",
            street2=f"line_5_{col}_street2",
            city=f"line_5_{col}_city",
            state=f"line_5_{col}_state",
            zip_code=f"line_5_{col}_zip",
            county=f"line_5_{col}_county",
        )
        if debtor.mailing_address != type(debtor.mailing_address)() and (
            debtor.mailing_address != debtor.residence_address
        ):
            address_fills(
                values,
                debtor.mailing_address,
                street=f"line_5_{col}_mailing_street",
                # The mailing column prints a P.O. Box line where the
                # residence prints a second street line.
                street2=f"line_5_{col}_mailing_pobox",
                city=f"line_5_{col}_mailing_city",
                state=f"line_5_{col}_mailing_state",
                zip_code=f"line_5_{col}_mailing_zip",
            )

        # Line 6 — venue.
        venue = debtor.venue
        if venue.basis is not None:
            values[f"line_6_{col}_venue_basis"] = Option(_VENUE_EXPORTS[venue.basis])
        if venue.explanation:
            rows(
                release,
                values,
                f"line_6_{col}_venue_explanation",
                wrap_lines(
                    venue.explanation,
                    width=85,
                    lines=4,
                    where=f"line_6_{col}_venue_explanation",
                    problems=problems,
                ),
                problems,
            )

        # Line 15 — credit counseling, the four-way per debtor.
        counseling = debtor.credit_counseling
        if counseling.status is not None:
            values[f"line_15_{col}_credit_counseling"] = Option(
                _COUNSELING_EXPORTS[counseling.status]
            )
        if counseling.status == "not_required" and counseling.exemption_reason:
            values[f"line_15_{col}_exemption_reason"] = Option(
                _COUNSELING_EXEMPTION_EXPORTS[counseling.exemption_reason]
            )

        # Signature dates (the signature lines themselves stay wet).
        if debtor.signed_at:
            values[f"sign.{col}_executed_on"] = Text(format_date(debtor.signed_at))

    # Line 9 — prior bankruptcies (three printed rows).
    values["line_9_prior_bankruptcy"] = yes_no(
        release, "line_9_prior_bankruptcy", bool(case_file.prior_cases)
    )
    rows(
        release,
        values,
        "line_9_prior_district",
        [p.district for p in case_file.prior_cases],
        problems,
    )
    rows(
        release,
        values,
        "line_9_prior_when",
        [p.filed_on and format_date(p.filed_on) for p in case_file.prior_cases],
        problems,
    )
    rows(
        release,
        values,
        "line_9_prior_case_number",
        [p.case_number for p in case_file.prior_cases],
        problems,
    )

    # Line 10 — related pending cases (two printed rows).
    values["line_10_related_pending"] = yes_no(
        release, "line_10_related_pending", bool(case_file.related_cases)
    )
    related = case_file.related_cases
    rows(
        release,
        values,
        "line_10_related_debtor",
        [r.debtor_name for r in related],
        problems,
    )
    rows(
        release,
        values,
        "line_10_related_relationship",
        [r.relationship for r in related],
        problems,
    )
    rows(
        release,
        values,
        "line_10_related_district",
        [r.district for r in related],
        problems,
    )
    rows(
        release,
        values,
        "line_10_related_when",
        [r.filed_on and format_date(r.filed_on) for r in related],
        problems,
    )
    rows(
        release,
        values,
        "line_10_related_case_number",
        [r.case_number for r in related],
        problems,
    )

    # Line 12 — sole proprietorships (the form prints ONE business block).
    values["line_12_sole_proprietor"] = yes_no(
        release, "line_12_sole_proprietor", bool(case_file.sole_proprietorships)
    )
    if len(case_file.sole_proprietorships) > 1:
        problems.append(
            "line_12: the form prints one sole-proprietorship block; "
            f"the case holds {len(case_file.sole_proprietorships)}"
        )
    if case_file.sole_proprietorships:
        business = case_file.sole_proprietorships[0]
        if business.name:
            values["line_12_business_name"] = Text(business.name)
        address_fills(
            values,
            business.address,
            street="line_12_business_street",
            street2="line_12_business_street2",
            city="line_12_business_city",
            state="line_12_business_state",
            zip_code="line_12_business_zip",
        )
        if business.business_type is not None:
            values["line_12_business_type"] = by_order(
                release, "line_12_business_type", BUSINESS_TYPES, business.business_type
            )

    # Line 17 — the chapter answers it; only a Chapter 7 filer goes on to
    # the funds question below.
    values["line_17_filing_under_ch7"] = yes_no(
        release, "line_17_filing_under_ch7", case.chapter == 7
    )

    # Parts 2, 4, 6 — the petition's answers.
    petition = case_file.petition
    if petition is not None:
        if petition.fee_handling is not None:
            values["line_8_fee_handling"] = Option(
                _FEE_HANDLING_EXPORTS[petition.fee_handling]
            )
        if petition.rents_residence is not None:
            values["line_11_rents_residence"] = yes_no(
                release, "line_11_rents_residence", petition.rents_residence
            )
            if petition.rents_residence and (
                petition.eviction_judgment_against_you is not None
            ):
                values["line_11_eviction_judgment"] = yes_no(
                    release,
                    "line_11_eviction_judgment",
                    petition.eviction_judgment_against_you,
                )
        if petition.small_business_status is not None:
            values["line_13_small_business_status"] = by_order(
                release,
                "line_13_small_business_status",
                SMALL_BUSINESS_STATUSES,
                petition.small_business_status,
            )

        # Line 14 — hazardous property.
        hazard = petition.hazardous_property
        has_hazard = bool(
            hazard.description
            or hazard.why_immediate
            or hazard.address != type(hazard.address)()
        )
        values["line_14_hazardous_property"] = yes_no(
            release, "line_14_hazardous_property", has_hazard
        )
        if hazard.description:
            rows(
                release,
                values,
                "line_14_hazard_description",
                wrap_lines(
                    hazard.description,
                    width=95,
                    lines=2,
                    where="line_14_hazard_description",
                    problems=problems,
                ),
                problems,
            )
        if hazard.why_immediate:
            rows(
                release,
                values,
                "line_14_why_immediate",
                wrap_lines(
                    hazard.why_immediate,
                    width=95,
                    lines=2,
                    where="line_14_why_immediate",
                    problems=problems,
                ),
                problems,
            )
        address_fills(
            values,
            hazard.address,
            street="line_14_property_street",
            street2="line_14_property_street2",
            city="line_14_property_city",
            state="line_14_property_state",
            zip_code="line_14_property_zip",
        )

        # Line 16 — the debt-character three-way, printed as two gates.
        if petition.debt_character is not None:
            consumer = petition.debt_character == "consumer"
            values["line_16a_consumer_debts"] = yes_no(
                release, "line_16a_consumer_debts", consumer
            )
            if not consumer:
                values["line_16b_business_debts"] = yes_no(
                    release,
                    "line_16b_business_debts",
                    petition.debt_character == "business",
                )
            if petition.debt_character == "other" and petition.debt_character_other:
                values["line_16c_other_debts"] = Text(petition.debt_character_other)

        if case.chapter == 7 and (
            petition.ch7_funds_available_for_creditors is not None
        ):
            values["line_17_funds_available"] = yes_no(
                release,
                "line_17_funds_available",
                petition.ch7_funds_available_for_creditors,
            )

        # Lines 18-20 — the self-selected estimate brackets. Bands map by
        # ORDER, each line reading its own spec options: the exports are
        # per-line and one of line 19's is misprinted, so the position in
        # the printed bracket list is the reliable identity.
        if petition.estimated_creditors is not None:
            values["line_18_estimated_creditors"] = by_order(
                release,
                "line_18_estimated_creditors",
                ESTIMATED_CREDITORS_BANDS,
                petition.estimated_creditors,
            )
        if petition.estimated_assets is not None:
            values["line_19_estimated_assets"] = by_order(
                release,
                "line_19_estimated_assets",
                ESTIMATED_DOLLAR_BANDS,
                petition.estimated_assets,
            )
        if petition.estimated_liabilities is not None:
            values["line_20_estimated_liabilities"] = by_order(
                release,
                "line_20_estimated_liabilities",
                ESTIMATED_DOLLAR_BANDS,
                petition.estimated_liabilities,
            )

    # Part 7 — who signs. An attorney fills the attorney block; without one,
    # the pro se block carries the debtor's own contact details, and the
    # acknowledgment boxes stay the filer's own act.
    attorney = next(
        (p for p in case_file.filing_professionals if p.role == "attorney"), None
    )
    preparer = next(
        (
            p
            for p in case_file.filing_professionals
            if p.role == "bankruptcy_petition_preparer"
        ),
        None,
    )
    if attorney is not None:
        if printed := full_name(attorney.name):
            values["attorney.printed_name"] = Text(printed)
        if attorney.firm_name:
            values["attorney.firm_name"] = Text(attorney.firm_name)
        address_fills(
            values,
            attorney.address,
            street="attorney.street",
            street2="attorney.street2",
            city="attorney.city",
            state="attorney.state",
            zip_code="attorney.zip",
        )
        for field_id, value in (
            ("attorney.phone", attorney.phone),
            ("attorney.email", attorney.email),
            ("attorney.bar_number", attorney.bar_number),
            ("attorney.bar_state", attorney.bar_state),
        ):
            if value:
                values[field_id] = Text(value)
        if attorney.signature_date:
            values["attorney.date_signed"] = Text(format_date(attorney.signature_date))
    else:
        values["prose.paid_preparer"] = yes_no(
            release, "prose.paid_preparer", preparer is not None
        )
        if preparer is not None and (preparer_name := full_name(preparer.name)):
            values["prose.preparer_name"] = Text(preparer_name)
        for debtor, col in ((debtor1, "debtor1"), (debtor2, "debtor2")):
            if debtor is None:
                continue
            for field_id, value in (
                (f"prose.{col}_phone", debtor.phone),
                (f"prose.{col}_cell", debtor.mobile),
                (f"prose.{col}_email", debtor.email),
            ):
                if value:
                    values[field_id] = Text(value)
            if debtor.signed_at:
                values[f"prose.{col}_date"] = Text(format_date(debtor.signed_at))

    if problems:
        raise FormProjectionError(sorted(problems))
    return values
