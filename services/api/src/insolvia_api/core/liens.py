"""The lien arithmetic (issue #345): what each secured claim is secured by,
how much of it the collateral actually covers, and what each asset carries.

WHY THIS IS ONE PURE FUNCTION AND NOT A STORED FIELD. The data model refuses
to store the unsecured portion of a secured claim because it is arithmetic
over other records — and once a claim references the asset it encumbers, it
is arithmetic over THREE: the claim's amount, the asset's value, and every
lien senior to it on the same asset. Typed by hand, those three numbers
disagree the day any one of them is edited; derived here, from the same
records B106D prints from, they cannot. The B106D projection reads its
Column B from this module, the case summary reports it, and the `liens`
route hands the same figures to the intake screens — one function, so the
overview, the schedule and the form under a preparer's cursor are the same
answer.

THE RULE, per secured claim:

    collateral value  = the claim's typed `collateral_value` if present
                        (the override for collateral not on Schedule A/B,
                        or a value the preparer has reason to prefer),
                        else the linked asset's `value_entire`, else its
                        `value_portion_owned`, else unknown.
    senior liens      = the amounts of the OTHER secured claims on the same
                        asset whose `lien_position` is lower (1 is senior).
                        A claim with no position has no known seniors, and
                        is nobody's senior: unknown order is not an order,
                        and guessing one from creation order would print a
                        deficiency the preparer never asserted.
    value available   = max(collateral value - senior liens, 0): what is
                        left of the collateral once the liens ahead of this
                        one have been paid from it.
    unsecured portion = the claim's `unsecured_amount_override` if present
                        (source "manual"), else
                        max(amount - value available, 0) (source "derived");
                        unknown when the amount or the collateral value is.
    secured portion   = amount - unsecured portion, never below zero.

Written out because the issue's shorthand — "amount less collateral less
senior liens" — reads as though a senior lien reduced the CLAIM, when it
reduces the collateral: a second mortgage of 80,000 on a 300,000 house
behind a 250,000 first finds 50,000 of value, and is 30,000 short.

Per asset: the secured total is the sum of the amounts of the secured claims
linked to it — Column A's figures, not the secured portions, because that is
what "how much is owed against this property" means to the person reading
the asset, and it is the number B108's surrender/retain decision weighs.

`value_entire` before `value_portion_owned`, deliberately: a lien attaches to
the property, not to the debtor's share of it, and Column B asks for the
value of the collateral that supports the claim. Where the preparer wants the
debtor's share instead (a jointly-owned house on a joint mortgage where only
one spouse files), `collateral_value` is the override that says so.

Only claims whose class is `secured` take part. A claim linked to an asset
but not yet classed is progressive intake in flight — the class is the
discriminant the schedules print by, and a lien that is not on Schedule D is
not a lien the arithmetic should count against anyone.

Money is `Decimal` throughout and STRINGS on the wire, two places, for the
reason `ClaimBody.amount` is a string: these are figures on a filing, and a
JSON number has been through binary floating point by the time it lands.
"""

from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal
from typing import Final, Literal

from insolvia_core.assets import AssetBody
from insolvia_core.claims import ClaimBody

from .form_projections.shared import CaseFile, amount

UnsecuredSource = Literal["manual", "derived"]

_ZERO: Final = Decimal("0.00")
_CENT: Final = Decimal("0.01")


@dataclass(frozen=True)
class ClaimLien:
    """One secured claim's collateral, resolved, and the two portions.

    `collateral_description` and `collateral_value` are the RESOLVED values —
    the typed override where there is one, the asset's otherwise — which is
    what B106D prints and what a screen should show beside the claim.
    `None` on a portion means "not derivable yet" (no amount, or no
    collateral value from either source), never zero: a blank box on the
    form, and a screen that says so rather than printing $0.
    """

    claim_id: str
    asset_id: str | None
    lien_position: int | None
    amount: Decimal | None
    collateral_description: str | None
    collateral_value: Decimal | None
    senior_liens: Decimal
    secured_amount: Decimal | None
    unsecured_amount: Decimal | None
    unsecured_source: UnsecuredSource


@dataclass(frozen=True)
class AssetLiens:
    """What one asset carries: the secured claims linked to it, in creation
    order, and the sum of their amounts."""

    asset_id: str
    claim_ids: tuple[str, ...]
    secured_total: Decimal


@dataclass(frozen=True)
class LienFigures:
    """Every secured claim's figures, and every asset that carries a lien.
    An asset with no linked claim is absent rather than listed at zero —
    the reader treats absence as "no liens", and the listing then scales
    with the liens rather than with the estate."""

    claims: tuple[ClaimLien, ...]
    assets: tuple[AssetLiens, ...]

    def claim(self, claim_id: str) -> ClaimLien | None:
        return next((c for c in self.claims if c.claim_id == claim_id), None)

    def asset(self, asset_id: str) -> AssetLiens | None:
        return next((a for a in self.assets if a.asset_id == asset_id), None)


def _money(value: Decimal) -> Decimal:
    return value.quantize(_CENT)


def _collateral_value(claim: ClaimBody, asset: AssetBody | None) -> Decimal | None:
    if claim.collateral_value is not None:
        return _money(Decimal(claim.collateral_value))
    if asset is None:
        return None
    for candidate in (asset.value_entire, asset.value_portion_owned):
        if candidate is not None:
            return _money(Decimal(candidate))
    return None


def _senior_liens(
    claim: ClaimBody, siblings: list[tuple[str, ClaimBody]], own_id: str
) -> Decimal:
    if claim.asset_id is None or claim.lien_position is None:
        return _ZERO
    total = _ZERO
    for sibling_id, sibling in siblings:
        if sibling_id == own_id or sibling.asset_id != claim.asset_id:
            continue
        if (
            sibling.lien_position is None
            or sibling.lien_position >= claim.lien_position
        ):
            continue
        total += amount(sibling.amount)
    return _money(total)


def derive_liens(case_file: CaseFile) -> LienFigures:
    """The figures for every secured claim of the case, and for every asset
    one of them names. A pure function of the case file, like the
    projections: no store, so it runs under pytest and cannot read a record
    the caller did not load — the route builds a `CaseFile` of just the
    assets and the claims."""
    secured = [
        (claim_id, body)
        for claim_id, body in case_file.claims
        if body.claim_class == "secured"
    ]

    claims: list[ClaimLien] = []
    for claim_id, claim in secured:
        asset = case_file.asset(claim.asset_id)
        collateral_value = _collateral_value(claim, asset)
        description = claim.collateral_description
        if description is None and asset is not None:
            description = asset.description
        senior = _senior_liens(claim, secured, claim_id)
        claim_amount = (
            _money(Decimal(claim.amount)) if claim.amount is not None else None
        )

        unsecured: Decimal | None
        source: UnsecuredSource
        if claim.unsecured_amount_override is not None:
            unsecured = _money(Decimal(claim.unsecured_amount_override))
            source = "manual"
        elif claim_amount is None or collateral_value is None:
            unsecured = None
            source = "derived"
        else:
            available = max(collateral_value - senior, _ZERO)
            unsecured = _money(max(claim_amount - available, _ZERO))
            source = "derived"

        secured_amount = (
            _money(max(claim_amount - unsecured, _ZERO))
            if claim_amount is not None and unsecured is not None
            else None
        )
        claims.append(
            ClaimLien(
                claim_id=claim_id,
                asset_id=claim.asset_id,
                lien_position=claim.lien_position,
                amount=claim_amount,
                collateral_description=description,
                collateral_value=collateral_value,
                senior_liens=senior,
                secured_amount=secured_amount,
                unsecured_amount=unsecured,
                unsecured_source=source,
            )
        )

    assets: list[AssetLiens] = []
    for asset_id, _asset in case_file.assets:
        linked = [(cid, body) for cid, body in secured if body.asset_id == asset_id]
        if not linked:
            continue
        assets.append(
            AssetLiens(
                asset_id=asset_id,
                claim_ids=tuple(cid for cid, _ in linked),
                secured_total=_money(
                    sum((amount(body.amount) for _, body in linked), _ZERO)
                ),
            )
        )
    return LienFigures(claims=tuple(claims), assets=tuple(assets))


def _money_json(value: Decimal) -> str:
    return f"{_money(value):f}"


def claim_lien_json(lien: ClaimLien) -> dict[str, object]:
    """The wire shape: camelCase, money as two-place strings, and absent
    members ABSENT rather than null — `problem_json`'s rule, so a screen's
    `!== undefined` is the whole "is this known yet" check."""
    body: dict[str, object] = {
        "claimId": lien.claim_id,
        "seniorLiens": _money_json(lien.senior_liens),
        "unsecuredSource": lien.unsecured_source,
    }
    if lien.asset_id is not None:
        body["assetId"] = lien.asset_id
    if lien.lien_position is not None:
        body["lienPosition"] = lien.lien_position
    if lien.amount is not None:
        body["amount"] = _money_json(lien.amount)
    if lien.collateral_description is not None:
        body["collateralDescription"] = lien.collateral_description
    if lien.collateral_value is not None:
        body["collateralValue"] = _money_json(lien.collateral_value)
    if lien.secured_amount is not None:
        body["securedAmount"] = _money_json(lien.secured_amount)
    if lien.unsecured_amount is not None:
        body["unsecuredAmount"] = _money_json(lien.unsecured_amount)
    return body


def asset_liens_json(liens: AssetLiens) -> dict[str, object]:
    return {
        "assetId": liens.asset_id,
        "claimIds": list(liens.claim_ids),
        "securedTotal": _money_json(liens.secured_total),
    }


def liens_json(figures: LienFigures) -> dict[str, object]:
    return {
        "claims": [claim_lien_json(c) for c in figures.claims],
        "assets": [asset_liens_json(a) for a in figures.assets],
    }
