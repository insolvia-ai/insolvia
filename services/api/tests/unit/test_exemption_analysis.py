"""The Schedule C workbench's arithmetic (issue #346) — core/exemption_analysis.py
as a pure function over hand-built inputs.

Every figure asserted is a registry figure (FL/TX/GA at their current
releases, federal at 04/25) or a chosen amount, so each subtraction is
visible by eye. Where a test depends on a registry number it names the entry
the number came from; test_exemptions.py is what verifies those numbers
against their sources.
"""

from __future__ import annotations

from datetime import date
from decimal import Decimal

import pytest
from insolvia_api.core.exemption_analysis import (
    ExemptionCase,
    analyse,
    analysis_json,
    compute_lookbacks,
    election_refusal,
    resolution_date,
    years_before,
)
from insolvia_core.assets import AssetBody
from insolvia_core.cases import Case
from insolvia_core.claims import ClaimBody
from insolvia_core.debtors import Address, Debtor
from insolvia_core.exemption_claims import ExemptionBody
from insolvia_core.petitions import PetitionBody
from insolvia_core.sofa import PriorAddress, SofaEntryBody

TODAY = date(2026, 9, 16)


def case(exemption_set: str | None = None) -> Case:
    return Case(
        id="case-exempt-0001",
        firm_id="firm-0001",
        created_by="subject-0001",
        chapter=7,
        district="Middle District of Florida",
        status="intake",
        created_at="2026-09-01T00:00:00Z",
        updated_at="2026-09-01T00:00:00Z",
        exemption_set=exemption_set,
    )


def debtor(role: str, state: str | None) -> Debtor:
    return Debtor(
        id=f"debtor-{role}",
        case_id="case-exempt-0001",
        filing_role=role,
        created_at="2026-09-01T00:00:00Z",
        updated_at="2026-09-01T00:00:00Z",
        residence_address=Address(state=state) if state else Address(),
    )


HOUSE = (
    "asset-house",
    AssetBody(
        category="real_property",
        description="12 Byron Court",
        value_entire="300000.00",
        value_portion_owned="300000.00",
    ),
)
CAR = (
    "asset-car",
    AssetBody(category="vehicle", description="2016 sedan", value_entire="9000.00"),
)
MORTGAGE = (
    "claim-mortgage",
    ClaimBody(
        claim_class="secured",
        amount="250000.00",
        asset_id="asset-house",
        lien_position=1,
    ),
)


def claim(
    exemption_id: str,
    asset_id: str,
    citation: str,
    amount: str | None = None,
    *,
    full_fmv: bool | None = None,
    acquired_within_1215_days: bool | None = None,
) -> tuple[str, ExemptionBody]:
    return (
        exemption_id,
        ExemptionBody(
            asset_id=asset_id,
            statute_citation=citation,
            amount=amount,
            claims_full_fmv=full_fmv,
            acquired_within_1215_days=acquired_within_1215_days,
        ),
    )


def inputs(
    *,
    state: str | None = "FL",
    exemption_set: str | None = None,
    joint: bool = False,
    petition: PetitionBody | None = None,
    assets=(HOUSE, CAR),
    claims=(MORTGAGE,),
    exemptions=(),
    sofa_entries=(),
) -> ExemptionCase:
    debtors = [debtor("debtor_1", state)] if state is not None else []
    if joint:
        debtors.append(debtor("debtor_2", state))
    return ExemptionCase(
        case=case(exemption_set),
        debtors=tuple(debtors),
        petition=petition,
        assets=tuple(assets),
        claims=tuple(claims),
        exemptions=tuple(exemptions),
        sofa_entries=tuple(sofa_entries),
    )


def analysis(**kwargs):
    return analyse(inputs(**kwargs), today=TODAY)


def entry(result, entry_id: str):
    return next(row for row in result.entries if row.entry.entry_id == entry_id)


def asset(result, asset_id: str):
    return next(row for row in result.assets if row.asset_id == asset_id)


# ── Dates ───────────────────────────────────────────────────────


def test_the_expected_filing_date_is_the_resolution_date_when_typed():
    petition = PetitionBody(expected_filing_date="2026-12-01")
    assert resolution_date(petition, TODAY) == (
        date(2026, 12, 1),
        "expected_filing_date",
    )


def test_a_floating_case_resolves_as_of_today():
    assert resolution_date(None, TODAY) == (TODAY, "today")
    assert resolution_date(PetitionBody(), TODAY) == (TODAY, "today")


def test_years_before_clamps_a_leap_day():
    assert years_before(date(2028, 2, 29), 5) == date(2023, 2, 28)
    assert years_before(date(2026, 9, 16), 10) == date(2016, 9, 16)


def test_the_lookbacks_are_calendar_dates_from_the_filing_date():
    lookbacks = compute_lookbacks(TODAY)
    assert lookbacks.section_522o == date(2016, 9, 16)
    assert lookbacks.section_522p == date(2023, 5, 20)  # 1,215 days
    assert lookbacks.section_522q == date(2021, 9, 16)
    assert lookbacks.domicile_period_start == date(2024, 9, 16)  # 730 days


# ── The election ────────────────────────────────────────────────


def test_an_opt_out_state_forces_the_state_scheme_without_an_election():
    result = analysis(state="FL")
    assert result.election.opted_out is True
    assert result.election.opt_out_citation == "Fla. Stat. § 222.20"
    assert result.election.effective == "state_and_federal_nonbankruptcy"
    assert [o.value for o in result.election.options] == [
        "state_and_federal_nonbankruptcy"
    ]
    assert not any("Choose" in problem for problem in result.problems)
    assert {row.entry.entry_id for row in result.entries} >= {
        "fl-homestead",
        "fl-vehicle",
    }


def test_a_federal_election_in_an_opt_out_state_is_overridden_and_reported():
    result = analysis(state="FL", exemption_set="federal")
    assert result.election.stored == "federal"
    assert result.election.effective == "state_and_federal_nonbankruptcy"
    assert any("opted out" in problem for problem in result.problems)


def test_an_election_state_offers_both_and_has_no_default():
    result = analysis(state="TX")
    assert result.election.opted_out is False
    assert [o.value for o in result.election.options] == [
        "state_and_federal_nonbankruptcy",
        "federal",
    ]
    assert result.election.effective is None
    assert result.entries == ()
    assert any("Choose the exemption scheme" in p for p in result.problems)


def test_the_federal_election_lists_the_522d_entries():
    result = analysis(state="TX", exemption_set="federal")
    assert result.election.effective == "federal"
    assert entry(result, "us-homestead").limit == Decimal("31575.00")


def test_no_debtor_state_is_a_problem_not_a_guess():
    result = analysis(state=None)
    assert result.election.state is None
    assert result.election.opted_out is None
    assert result.entries == ()
    assert any("no residence state" in p for p in result.problems)


def test_a_state_outside_the_launch_set_is_a_problem_not_a_guess():
    result = analysis(state="CA")
    assert result.entries == ()
    assert any("not supported yet" in p for p in result.problems)


def test_a_filing_date_before_the_series_baseline_refuses():
    # Georgia's series begins 2026-07-01 (ADR 0017): a filing date before it
    # has no data that describes it.
    result = analysis(
        state="GA", petition=PetitionBody(expected_filing_date="2026-06-01")
    )
    assert result.as_of == date(2026, 6, 1)
    assert result.entries == ()
    assert any("no release of exemptions/ga" in p for p in result.problems)


@pytest.mark.parametrize(
    ("election", "state", "refused"),
    [
        pytest.param("federal", "FL", True, id="fl-opted-out"),
        pytest.param("federal", "GA", True, id="ga-opted-out"),
        pytest.param("federal", "TX", False, id="tx-allows-it"),
        pytest.param("state_and_federal_nonbankruptcy", "FL", False, id="state-always"),
        pytest.param("federal", None, False, id="unknown-state-cannot-tell"),
        pytest.param("federal", "CA", False, id="unsupported-state-cannot-tell"),
    ],
)
def test_election_refusal_is_the_registrys_opt_out_rule(election, state, refused):
    refusal = election_refusal(election, state=state, as_of=TODAY)
    assert (refusal is not None) is refused


# ── Per statute ─────────────────────────────────────────────────


def test_claimed_draws_the_limit_down_and_over_claiming_floors_at_zero():
    result = analysis(
        state="FL",
        exemptions=(
            claim("e-1", "asset-car", "Fla. Stat. § 222.25(1)", "3000.00"),
            claim("e-2", "asset-car", "Fla. Stat. § 222.25(1)", "4000.00"),
        ),
    )
    vehicle = entry(result, "fl-vehicle")  # $5,000.00
    assert vehicle.limit == Decimal("5000.00")
    assert vehicle.claimed == Decimal("7000.00")
    assert vehicle.available == Decimal("0.00")
    untouched = entry(result, "fl-personal-property")  # $1,000.00
    assert untouched.claimed == Decimal("0.00")
    assert untouched.available == Decimal("1000.00")


def test_an_unlimited_entry_has_no_limit_and_no_available_figure():
    result = analysis(
        state="FL",
        exemptions=(
            claim("e-1", "asset-house", "Fla. Const. art. X, § 4(a)(1)", full_fmv=True),
        ),
    )
    homestead = entry(result, "fl-homestead")
    assert homestead.entry.unlimited is True
    assert homestead.limit is None
    assert homestead.available is None
    # A full-FMV claim under an uncapped statute counts at the asset's value.
    assert homestead.claimed == Decimal("300000.00")


def test_a_full_fmv_claim_under_a_capped_statute_counts_up_to_the_limit():
    result = analysis(
        state="TX",
        exemption_set="federal",
        exemptions=(
            claim("e-1", "asset-house", "11 U.S.C. § 522(d)(1)", full_fmv=True),
        ),
    )
    homestead = entry(result, "us-homestead")
    assert homestead.claimed == Decimal("31575.00")
    assert homestead.available == Decimal("0.00")


def test_a_wildcard_absorbs_unused_homestead_up_to_the_carryover_cap():
    # GA: wildcard $1,200 + up to $10,000 of the unused $50,000 homestead.
    untouched = analysis(state="GA")
    wildcard = entry(untouched, "ga-wildcard")
    assert wildcard.carryover == Decimal("10000.00")
    assert wildcard.limit == Decimal("11200.00")

    mostly_used = analysis(
        state="GA",
        exemptions=(
            claim("e-1", "asset-house", "O.C.G.A. § 44-13-100(a)(1)", "45000.00"),
        ),
    )
    wildcard = entry(mostly_used, "ga-wildcard")
    assert wildcard.carryover == Decimal("5000.00")
    assert wildcard.limit == Decimal("6200.00")


def test_a_joint_case_uses_the_statutes_joint_figure_where_it_names_one():
    single = entry(analysis(state="GA"), "ga-homestead")
    joint = entry(analysis(state="GA", joint=True), "ga-homestead")
    assert single.limit == Decimal("50000.00")
    assert joint.limit == Decimal("100000.00")


def test_a_claim_may_cite_any_one_authority_the_entry_lists():
    # The registry's FL homestead citation names the constitution AND the
    # implementing statute; a claim citing either half draws it down.
    result = analysis(
        state="FL",
        exemptions=(
            claim("e-1", "asset-house", "Fla. Stat. §§ 222.01-222.02", "1.00"),
        ),
    )
    assert entry(result, "fl-homestead").claimed == Decimal("1.00")
    (row,) = asset(result, "asset-house").claims
    assert row.known_statute is True


def test_a_claim_citing_a_statute_outside_the_scheme_draws_nothing_and_warns():
    result = analysis(
        state="FL",
        exemptions=(claim("e-1", "asset-car", "11 U.S.C. § 522(d)(2)", "1000.00"),),
    )
    assert all(row.claimed == Decimal("0.00") for row in result.entries)
    (row,) = asset(result, "asset-car").claims
    assert row.known_statute is False
    assert any("11 U.S.C. § 522(d)(2)" in warning for warning in result.warnings)


# ── Per asset ───────────────────────────────────────────────────


def test_value_less_liens_is_net_equity_less_claimed_is_unexempt():
    result = analysis(
        state="FL",
        exemptions=(
            claim("e-1", "asset-house", "Fla. Const. art. X, § 4(a)(2)", "1000.00"),
        ),
    )
    house = asset(result, "asset-house")
    assert house.current_value == Decimal("300000.00")
    assert house.liens == Decimal("250000.00")
    assert house.net_equity == Decimal("50000.00")
    assert house.claimed == Decimal("1000.00")
    assert house.unexempt == Decimal("49000.00")
    (row,) = house.claims
    assert row.exemption_id == "e-1"
    assert row.claimed == Decimal("1000.00")


def test_an_asset_with_no_value_reports_unknown_not_zero():
    bare = ("asset-bare", AssetBody(category="cash", description="Wallet"))
    result = analysis(state="FL", assets=(bare,), claims=())
    row = asset(result, "asset-bare")
    assert row.current_value is None
    assert row.net_equity is None
    assert row.unexempt is None
    assert row.liens == Decimal("0.00")


def test_liens_never_push_net_equity_below_zero():
    underwater = ("asset-car", AssetBody(category="vehicle", value_entire="9000.00"))
    loan = (
        "claim-loan",
        ClaimBody(claim_class="secured", amount="12000.00", asset_id="asset-car"),
    )
    result = analysis(state="FL", assets=(underwater,), claims=(loan,))
    assert asset(result, "asset-car").net_equity == Decimal("0.00")


def test_the_suggested_amount_is_the_lesser_of_unexempt_and_available():
    result = analysis(state="FL")
    house = dict(asset(result, "asset-house").suggestions)
    # $50,000 of equity against a $1,000 statute: the statute wins.
    assert house["fl-personal-property"] == Decimal("1000.00")
    # Against the unlimited homestead: the equity is the only cap.
    assert house["fl-homestead"] == Decimal("50000.00")
    car = dict(asset(result, "asset-car").suggestions)
    # $9,000 of equity against the $5,000 vehicle exemption.
    assert car["fl-vehicle"] == Decimal("5000.00")


def test_the_1215_day_homestead_cap_binds_a_recently_acquired_homestead():
    result = analysis(
        state="FL",
        claims=(),
        exemptions=(
            claim(
                "e-1",
                "asset-house",
                "Fla. Const. art. X, § 4(a)(1)",
                full_fmv=True,
                acquired_within_1215_days=True,
            ),
        ),
    )
    house = asset(result, "asset-house")
    # The registry's § 522(p) cap on the 04/25 federal release.
    assert house.homestead_cap == Decimal("214000.00")
    assert house.cap_applied is True
    assert house.claimed == Decimal("300000.00")
    assert house.unexempt == Decimal("86000.00")


def test_the_cap_is_not_applied_to_a_homestead_held_longer():
    result = analysis(
        state="FL",
        claims=(),
        exemptions=(
            claim(
                "e-1",
                "asset-house",
                "Fla. Const. art. X, § 4(a)(1)",
                full_fmv=True,
                acquired_within_1215_days=False,
            ),
        ),
    )
    house = asset(result, "asset-house")
    assert house.homestead_cap is None
    assert house.cap_applied is False
    assert house.unexempt == Decimal("0.00")


def test_the_cap_is_reported_but_not_applied_under_it():
    result = analysis(
        state="FL",
        exemptions=(
            claim(
                "e-1",
                "asset-house",
                "Fla. Const. art. X, § 4(a)(1)",
                "40000.00",
                acquired_within_1215_days=True,
            ),
        ),
    )
    house = asset(result, "asset-house")
    assert house.homestead_cap == Decimal("214000.00")
    assert house.cap_applied is False
    assert house.unexempt == Decimal("10000.00")


# ── The domicile warning ────────────────────────────────────────


def prior_address(which_debtor: str, to_date: str | None) -> SofaEntryBody:
    return SofaEntryBody(
        entry_type="prior_address",
        payload=PriorAddress(
            which_debtor=which_debtor,
            address=Address(state="NY"),
            from_date="2022-01-01",
            to_date=to_date,
        ),
    )


@pytest.mark.parametrize(
    ("entries", "warned"),
    [
        pytest.param((), False, id="nothing-on-file-cannot-tell"),
        pytest.param(
            (prior_address("debtor_1", "2025-03-01"),), True, id="moved-inside"
        ),
        pytest.param((prior_address("both", None),), True, id="undated-move"),
        pytest.param(
            (prior_address("debtor_1", "2024-01-01"),), False, id="moved-before"
        ),
        pytest.param(
            (prior_address("debtor_2", "2025-03-01"),), False, id="not-debtor-1"
        ),
    ],
)
def test_the_730_day_domicile_rule_warns_when_a_prior_address_overlaps(entries, warned):
    result = analysis(state="FL", sofa_entries=entries)
    assert any("§ 522(b)(3)(A)" in w for w in result.warnings) is warned


# ── The wire shape ──────────────────────────────────────────────


def test_the_json_carries_money_as_strings_and_null_for_unknown():
    bare = ("asset-bare", AssetBody(category="cash"))
    body = analysis_json(
        analysis(
            state="FL",
            assets=(HOUSE, bare),
            exemptions=(
                claim("e-1", "asset-house", "Fla. Const. art. X, § 4(a)(2)", "1000.00"),
            ),
        )
    )
    assert body["asOf"] == "2026-09-16"
    assert body["asOfSource"] == "today"
    assert body["election"]["effective"] == "state_and_federal_nonbankruptcy"
    assert body["election"]["optedOut"] is True
    assert body["lookbacks"]["section522p"] == "2023-05-20"
    house, unknown = body["assets"]
    assert house["netEquity"] == "50000.00"
    assert house["unexempt"] == "49000.00"
    assert house["claims"][0] == {
        "exemptionId": "e-1",
        "statuteCitation": "Fla. Const. art. X, § 4(a)(2)",
        "amount": "1000.00",
        "claimsFullFmv": None,
        "acquiredWithin1215Days": None,
        "claimed": "1000.00",
        "knownStatute": True,
    }
    # Unexempt AFTER the $1,000 claim already on it, under an uncapped statute.
    assert house["suggestions"]["fl-homestead"] == "49000.00"
    assert unknown["currentValue"] is None
    assert unknown["unexempt"] is None
    personal = next(
        e for e in body["entries"] if e["entryId"] == "fl-personal-property"
    )
    assert personal["limit"] == "1000.00"
    assert personal["claimed"] == "1000.00"
    assert personal["available"] == "0.00"
    assert isinstance(body["limits"], list)
    assert any(
        limit["limitId"] == "us-homestead-1215-day-cap" for limit in body["limits"]
    )
