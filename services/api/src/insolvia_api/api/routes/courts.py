"""`GET /v1/courts` — the court registry, read-only (issue #360 / 14.8).

The districts and divisions a case may name, from the `courts/us-bankruptcy`
series in `insolvia_core.courts` (ADR 0014, specified by ADR 0024). This is
what the case-create form's court and division pickers read, and what a
client uses to turn a case's `court`/`division` codes back into names.

AUTHENTICATED BUT UNGATED BY FEATURE. Every signed-in member of a firm may
read it — a paralegal opening a case needs the list exactly as an
administrator does — so it takes `@require_auth` and `current_accessor()`
(a caller in no firm is 403, like everywhere) and no `@requires`. The data
is public (it is the courts' own published facts) and grants nothing.

THE LISTING IS THE CURRENT RELEASE, resolved as of today: a floating case
picks from the courts in force now. The pinned read a filed case wants
belongs to packet assembly, with every other series it pins.

The shape is deliberately the client's, not the record's whole: identity,
divisions with their counties and office codes, and the Case Upload status
(so a screen can say "not yet proven for this court"). The PDF, matrix and
opening rules are served to the packet, not to a picker — a later PR's
filing checklist reads them from the record directly.
"""

from __future__ import annotations

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue
from insolvia_core import courts

from insolvia_api.api.auth import current_accessor, require_auth

blueprint = Blueprint("courts", __name__)


def division_json(division: courts.Division) -> dict[str, object]:
    # A fact's value rides beside its status: the client renders an
    # unverified office code as one, and null as "not yet known".
    courthouse = division.courthouse.value
    return {
        "code": division.code,
        "name": division.name,
        "officeCode": division.office_code.value,
        "officeCodeVerified": division.office_code.verified,
        "courthouse": (
            {
                "name": courthouse.name,
                "line1": courthouse.line1,
                "line2": courthouse.line2,
                "city": courthouse.city,
                "state": courthouse.state,
                "postal_code": courthouse.postal_code,
            }
            if courthouse is not None
            else None
        ),
        "counties": [
            {"name": county.name, "fips": county.fips} for county in division.counties
        ],
    }


def district_json(district: courts.CourtDistrict) -> dict[str, object]:
    return {
        "code": district.code,
        "courtId": district.court_id,
        "name": district.name,
        "state": district.state,
        "circuit": district.circuit,
        "website": district.website,
        "divisions": [division_json(d) for d in district.divisions],
        "caseUpload": {
            "status": district.case_upload.status,
            "verifiedAt": (
                district.case_upload.verified_at.isoformat()
                if district.case_upload.verified_at is not None
                else None
            ),
        },
    }


def courts_json(release: courts.CourtsRelease) -> dict[str, object]:
    return {
        "releaseId": release.release_id,
        "effectiveDate": release.effective_date.isoformat(),
        "districts": [district_json(d) for d in release.districts],
    }


@blueprint.get("/v1/courts")
@require_auth
def list_courts_route() -> ResponseReturnValue:
    current_accessor()
    return jsonify(courts_json(courts.current())), 200
