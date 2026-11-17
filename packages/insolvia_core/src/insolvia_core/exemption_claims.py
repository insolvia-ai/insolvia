"""The claimed exemption — 106C, one row per exemption asserted on an asset.

The entity is `exemption` (docs/reference/case-data-model.md, "Assets and
exemptions"); the module is named for the claim to keep clear of
core/exemptions.py, which owns the exemption DATASET — the registry of what
the law allows. This record is what the debtor asserts: it references an
`asset` and holds the statute citation and an amount that is *either* a
dollar figure *or* the "100% of fair market value up to the statutory limit"
election — mutually exclusive per the model, so one nullable `amount` plus a
`claims_full_fmv` boolean, not two amount fields. The exclusivity is a
completeness-gate fact, not a shape rule: an intake that has typed the amount
but not yet answered the election must persist (the usual progressive-intake
reasoning).

`acquired_within_1215_days` is a fact the debtor supplies, not a threshold we
configure — the § 522(p)/(q) caps it feeds live in the exemptions registry.

`asset_id` names an asset record, unchecked here for the usual
progressive-intake reason; a dangling reference is the completeness gate's to
flag. The property description and value 106C prints are the ASSET's —
copied at projection time, never stored here (case-data-model.md, "Derived
values are computed, never stored").
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import boolean, money, text


@dataclass(frozen=True)
class ExemptionBody:
    asset_id: str | None = None
    statute_citation: str | None = None
    amount: str | None = None
    claims_full_fmv: bool | None = None
    acquired_within_1215_days: bool | None = None


def parse_exemption(payload: Mapping[str, object]) -> ExemptionBody:
    errors: dict[str, str] = {}
    body = ExemptionBody(
        asset_id=text(payload.get("asset_id"), "asset_id", errors, limit=64),
        statute_citation=text(
            payload.get("statute_citation"), "statute_citation", errors
        ),
        amount=money(payload.get("amount"), "amount", errors),
        claims_full_fmv=boolean(
            payload.get("claims_full_fmv"), "claims_full_fmv", errors
        ),
        acquired_within_1215_days=boolean(
            payload.get("acquired_within_1215_days"),
            "acquired_within_1215_days",
            errors,
        ),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


EXEMPTION: EntityKind[ExemptionBody] = EntityKind(
    name="exemption",
    collection="exemptions",
    sk_prefix="EXEMPTION",
    parse_body=parse_exemption,
)
