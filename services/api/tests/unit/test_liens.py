"""The lien arithmetic (issue #345) — core/liens.py, as a pure function.

Every case builds a CaseFile of just assets and claims: the derivation reads
nothing else, and a fixture that carried a whole reference case would hide
which record a figure came from. Amounts are chosen so every subtraction is
visible by eye.
"""

from __future__ import annotations

from decimal import Decimal

import pytest
from insolvia_api.core.form_projections import CaseFile
from insolvia_api.core.liens import claim_lien_json, derive_liens, liens_json
from insolvia_core.assets import AssetBody
from insolvia_core.cases import Case
from insolvia_core.claims import ClaimBody

CASE = Case(
    id="case-liens-0001",
    firm_id="firm-0001",
    created_by="subject-0001",
    chapter=7,
    district="Middle District of Florida",
    status="intake",
    created_at="2026-09-01T00:00:00Z",
    updated_at="2026-09-01T00:00:00Z",
)

HOUSE = (
    "asset-house",
    AssetBody(description="12 Byron Court", value_entire="300000.00"),
)
CAR = (
    "asset-car",
    AssetBody(
        description="2016 sedan", value_entire="9000.00", value_portion_owned="4500.00"
    ),
)


def secured(claim_id: str, **fields: object) -> tuple[str, ClaimBody]:
    return (claim_id, ClaimBody(claim_class="secured", **fields))  # type: ignore[arg-type]


def case_file(*claims: tuple[str, ClaimBody], assets=(HOUSE, CAR)) -> CaseFile:
    return CaseFile(case=CASE, assets=tuple(assets), claims=tuple(claims))


def figures_for(*claims: tuple[str, ClaimBody], assets=(HOUSE, CAR)):
    return derive_liens(case_file(*claims, assets=assets))


# ── Which claims take part ────────────────────────────────────


def test_only_secured_claims_take_part():
    figures = figures_for(
        ("c-unsecured", ClaimBody(claim_class="nonpriority_unsecured", amount="1.00")),
        ("c-unclassed", ClaimBody(asset_id="asset-car", amount="1.00")),
        secured("c-secured", amount="1.00"),
    )
    assert [c.claim_id for c in figures.claims] == ["c-secured"]
    # A claim that names an asset but is not (yet) secured is not a lien on
    # it — the class is the discriminant the schedules print by.
    assert figures.assets == ()


def test_the_figures_keep_the_claims_creation_order():
    figures = figures_for(secured("c-2", amount="2.00"), secured("c-1", amount="1.00"))
    assert [c.claim_id for c in figures.claims] == ["c-2", "c-1"]


# ── The collateral value: override, asset, unknown ───────────


def test_without_collateral_the_portions_are_unknown_not_zero():
    (lien,) = figures_for(secured("c", amount="1000.00")).claims
    assert lien.collateral_value is None
    assert lien.unsecured_amount is None
    assert lien.secured_amount is None
    assert lien.unsecured_source == "derived"


def test_a_linked_asset_supplies_the_collateral_value_and_description():
    (lien,) = figures_for(secured("c", amount="12000.00", asset_id="asset-car")).claims
    # value_entire, not the debtor's portion: the lien attaches to the
    # property. The override below is how a preparer says otherwise.
    assert lien.collateral_value == Decimal("9000.00")
    assert lien.collateral_description == "2016 sedan"
    assert lien.unsecured_amount == Decimal("3000.00")
    assert lien.secured_amount == Decimal("9000.00")


def test_the_portion_owned_stands_in_when_the_entire_value_is_absent():
    asset = ("asset-cash", AssetBody(value_portion_owned="150.00"))
    (lien,) = figures_for(
        secured("c", amount="200.00", asset_id="asset-cash"), assets=(asset,)
    ).claims
    assert lien.collateral_value == Decimal("150.00")
    assert lien.unsecured_amount == Decimal("50.00")


def test_a_typed_collateral_value_and_description_win_over_the_assets():
    (lien,) = figures_for(
        secured(
            "c",
            amount="12000.00",
            asset_id="asset-car",
            collateral_value="4500.00",
            collateral_description="Debtor 1's half of the sedan",
        )
    ).claims
    assert lien.collateral_value == Decimal("4500.00")
    assert lien.collateral_description == "Debtor 1's half of the sedan"
    assert lien.unsecured_amount == Decimal("7500.00")


def test_a_dangling_asset_id_reads_as_no_asset():
    # Shape-checked at entry, flagged by the completeness gate — here it is
    # simply collateral with no value, exactly like an unlinked claim.
    (lien,) = figures_for(secured("c", amount="500.00", asset_id="asset-gone")).claims
    assert lien.collateral_value is None
    assert lien.unsecured_amount is None


def test_collateral_worth_more_than_the_claim_leaves_no_deficiency():
    (lien,) = figures_for(
        secured("c", amount="195000.00", asset_id="asset-house")
    ).claims
    assert lien.unsecured_amount == Decimal("0.00")
    assert lien.secured_amount == Decimal("195000.00")


# ── Senior liens ──────────────────────────────────────────────


def test_a_chain_of_three_liens_on_one_asset():
    """The house is worth 300,000. The first mortgage (250,000) is covered in
    full; the second (80,000) finds 50,000 of value left and is 30,000 short;
    the third (20,000) finds nothing and is wholly unsecured."""
    figures = figures_for(
        secured("first", amount="250000.00", asset_id="asset-house", lien_position=1),
        secured("second", amount="80000.00", asset_id="asset-house", lien_position=2),
        secured("third", amount="20000.00", asset_id="asset-house", lien_position=3),
    )
    first, second, third = figures.claims

    assert (first.senior_liens, first.unsecured_amount) == (
        Decimal("0.00"),
        Decimal("0.00"),
    )
    assert first.secured_amount == Decimal("250000.00")

    assert (second.senior_liens, second.unsecured_amount) == (
        Decimal("250000.00"),
        Decimal("30000.00"),
    )
    assert second.secured_amount == Decimal("50000.00")

    assert (third.senior_liens, third.unsecured_amount) == (
        Decimal("330000.00"),
        Decimal("20000.00"),
    )
    assert third.secured_amount == Decimal("0.00")

    (house,) = figures.assets
    assert house.claim_ids == ("first", "second", "third")
    assert house.secured_total == Decimal("350000.00")


def test_reordering_the_positions_moves_the_deficiency():
    # The same two claims, the smaller one now senior: it is covered in full
    # and the larger one carries the whole shortfall.
    figures = figures_for(
        secured("big", amount="250000.00", asset_id="asset-house", lien_position=2),
        secured("small", amount="80000.00", asset_id="asset-house", lien_position=1),
    )
    big, small = figures.claims
    assert small.unsecured_amount == Decimal("0.00")
    assert big.senior_liens == Decimal("80000.00")
    assert big.unsecured_amount == Decimal("30000.00")


def test_a_claim_without_a_position_has_no_known_seniors_and_is_nobodys_senior():
    figures = figures_for(
        secured("placed", amount="250000.00", asset_id="asset-house", lien_position=1),
        secured("unplaced", amount="80000.00", asset_id="asset-house"),
        secured("junior", amount="10000.00", asset_id="asset-house", lien_position=2),
    )
    placed, unplaced, junior = figures.claims
    # Unknown order is not an order: the unplaced claim is not counted ahead
    # of anyone, and nothing is counted ahead of it.
    assert unplaced.senior_liens == Decimal("0.00")
    assert unplaced.unsecured_amount == Decimal("0.00")
    assert junior.senior_liens == Decimal("250000.00")
    assert placed.senior_liens == Decimal("0.00")
    # The asset's total still counts every linked claim, positioned or not.
    assert figures.asset("asset-house").secured_total == Decimal("340000.00")


def test_equal_positions_are_neither_senior_nor_junior_to_each_other():
    figures = figures_for(
        secured("a", amount="200000.00", asset_id="asset-house", lien_position=1),
        secured("b", amount="200000.00", asset_id="asset-house", lien_position=1),
    )
    a, b = figures.claims
    assert a.senior_liens == b.senior_liens == Decimal("0.00")


def test_liens_on_another_asset_are_not_senior():
    figures = figures_for(
        secured("house", amount="250000.00", asset_id="asset-house", lien_position=1),
        secured("car", amount="7000.00", asset_id="asset-car", lien_position=2),
    )
    _, car = figures.claims
    assert car.senior_liens == Decimal("0.00")
    assert car.unsecured_amount == Decimal("0.00")


def test_a_senior_lien_with_no_amount_counts_as_nothing():
    figures = figures_for(
        secured("first", asset_id="asset-house", lien_position=1),
        secured("second", amount="80000.00", asset_id="asset-house", lien_position=2),
    )
    first, second = figures.claims
    assert first.unsecured_amount is None
    assert second.senior_liens == Decimal("0.00")


# ── The manual override ───────────────────────────────────────


def test_a_manual_override_replaces_the_arithmetic_and_says_so():
    (lien,) = figures_for(
        secured(
            "c",
            amount="12000.00",
            asset_id="asset-car",
            unsecured_amount_override="5000.00",
        )
    ).claims
    assert lien.unsecured_amount == Decimal("5000.00")
    assert lien.unsecured_source == "manual"
    # The secured portion follows the override, so the two still sum to
    # the claim.
    assert lien.secured_amount == Decimal("7000.00")
    # The collateral figures are still reported — the override is about the
    # portions, not about what the collateral is.
    assert lien.collateral_value == Decimal("9000.00")


def test_a_manual_override_works_with_no_collateral_at_all():
    (lien,) = figures_for(
        secured("c", amount="1000.00", unsecured_amount_override="1000.00")
    ).claims
    assert lien.unsecured_amount == Decimal("1000.00")
    assert lien.secured_amount == Decimal("0.00")


def test_an_override_above_the_claim_floors_the_secured_portion_at_zero():
    (lien,) = figures_for(
        secured("c", amount="1000.00", unsecured_amount_override="1500.00")
    ).claims
    assert lien.secured_amount == Decimal("0.00")


# ── Per asset ─────────────────────────────────────────────────


def test_an_asset_without_liens_is_absent_rather_than_zero():
    figures = figures_for(secured("c", amount="1.00", asset_id="asset-car"))
    assert [a.asset_id for a in figures.assets] == ["asset-car"]
    assert figures.asset("asset-house") is None


def test_the_assets_secured_total_is_the_sum_of_the_amounts_owed_against_it():
    # Column A's figures, not the secured portions: the car carries 12,000
    # of debt even though it is worth 9,000.
    figures = figures_for(
        secured("a", amount="7000.00", asset_id="asset-car", lien_position=1),
        secured("b", amount="5000.00", asset_id="asset-car", lien_position=2),
    )
    assert figures.asset("asset-car").secured_total == Decimal("12000.00")


# ── The wire shape ────────────────────────────────────────────


def test_json_carries_money_as_two_place_strings_and_omits_the_unknown():
    figures = figures_for(secured("c", amount="1000.00"))
    (row,) = liens_json(figures)["claims"]
    assert row == {
        "claimId": "c",
        "amount": "1000.00",
        "seniorLiens": "0.00",
        "unsecuredSource": "derived",
    }
    assert liens_json(figures)["assets"] == []


def test_json_reports_the_full_row_when_everything_is_known():
    figures = figures_for(
        secured(
            "c",
            amount="12000.00",
            asset_id="asset-car",
            lien_position=1,
            unsecured_amount_override="5000",
        )
    )
    assert claim_lien_json(figures.claims[0]) == {
        "claimId": "c",
        "assetId": "asset-car",
        "lienPosition": 1,
        "amount": "12000.00",
        "collateralDescription": "2016 sedan",
        "collateralValue": "9000.00",
        "seniorLiens": "0.00",
        "securedAmount": "7000.00",
        "unsecuredAmount": "5000.00",
        "unsecuredSource": "manual",
    }
    assert liens_json(figures)["assets"] == [
        {"assetId": "asset-car", "claimIds": ["c"], "securedTotal": "12000.00"}
    ]


@pytest.mark.parametrize("amount", ["0", "0.5", "1234.5"])
def test_every_figure_is_quantised_to_the_cent(amount):
    (lien,) = figures_for(secured("c", amount=amount, collateral_value="0")).claims
    assert claim_lien_json(lien)["unsecuredAmount"] == f"{Decimal(amount):.2f}"
