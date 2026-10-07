"""The attorney's own CM/ECF credential (ADR 0024, guardrail 3): enrol,
status and revoke under `/v1/me/filing-credentials`.

UNDER `/v1/me`, AND THAT IS THE AUTHORIZATION. There is no attorney id in
any URL or body: the credential's owner is the token's subject, always, so
only the owner enrols, reads the status of, or revokes a credential — and an
admin holding every feature still cannot reach somebody else's, because the
item's partition IS the caller (insolvia_core.filing_credentials). A
credential id that belongs to another attorney is not found: 404, the
anti-oracle answer.

GATED BY `electronic_filing`, hidden for every role by default
(insolvia_core.firms) — the feature flag for a path that is unfinished until
ADR 0024's PRs 5-8 land. An admin grants it per attorney.

THE API SEALS AND CANNOT OPEN. The dependencies hold a
`FilingCredentialSealer` and no opener; the key policy denies this
service's role Decrypt besides. Nothing these routes return contains the
password, the seed or the envelope — `credential_json` is the only
serializer, and its key set is pinned in core's tests.

Every response is `Cache-Control: no-store`: the enrolment's request body is
the one place a TOTP seed crosses a client, and nothing in front of this
service should be invited to keep any of the exchange.
"""

from __future__ import annotations

import logging
import re
from typing import Final

from flask import Blueprint, Response, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.errors import NotFoundError, ValidationError
from insolvia_core.filing_credentials import (
    credential_json,
    enrol_credential,
    list_credentials,
    parse_enrolment,
    revoke_credential,
)
from insolvia_core.firms import ADD_EDIT, ELECTRONIC_FILING, VIEW_ONLY
from insolvia_core.ports import (
    AccessLog,
    FilingCredentialSealer,
    FilingCredentialStore,
)

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies

logger = logging.getLogger(__name__)

blueprint = Blueprint("filing_credentials", __name__)

# A login, a password and a seed. 4 KiB is generous; the cap refuses a body
# that is not an enrolment before anything parses it.
MAX_REQUEST_BYTES: Final = 4 * 1024

_CREDENTIAL_ID_RE: Final = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\Z"
)


def _vault() -> tuple[FilingCredentialStore, FilingCredentialSealer, AccessLog]:
    deps = dependencies()
    if (
        deps.filing_credential_store is None
        or deps.filing_credential_sealer is None
        or deps.access_log is None
    ):
        raise RuntimeError("the filing-credential vault is not composed")
    return deps.filing_credential_store, deps.filing_credential_sealer, deps.access_log


def _no_store(response: Response) -> Response:
    response.headers["Cache-Control"] = "no-store"
    return response


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 4 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


@blueprint.get("/v1/me/filing-credentials")
@require_auth
@requires(ELECTRONIC_FILING, VIEW_ONLY)
def list_filing_credentials() -> ResponseReturnValue:
    """STATUS — the caller's own credentials, plain attributes only. Opens
    nothing, so it writes no access row (access_log's note on why there is
    no credential.read)."""
    store, _, _ = _vault()
    accessor = current_accessor()
    found = list_credentials(
        firm_id=accessor.firm_id, attorney_id=accessor.subject, store=store
    )
    return _no_store(jsonify({"credentials": [credential_json(c) for c in found]}))


@blueprint.post("/v1/me/filing-credentials")
@require_auth
@requires(ELECTRONIC_FILING, ADD_EDIT)
def enrol_filing_credential() -> ResponseReturnValue:
    """ENROL — seal and store. Answers the status view, never an echo."""
    store, sealer, access_log = _vault()
    accessor = current_accessor()
    enrolment = parse_enrolment(_json_body())
    credential = enrol_credential(
        enrolment,
        firm_id=accessor.firm_id,
        attorney_id=accessor.subject,
        sealer=sealer,
        store=store,
        access_log=access_log,
    )
    # Metadata only (GLBA, and this is a court credential besides): that an
    # enrolment happened, never the login, never the subject.
    logger.info("filing credential enrolled")
    response = _no_store(jsonify(credential_json(credential)))
    response.status_code = 201
    return response


@blueprint.delete("/v1/me/filing-credentials/<credential_id>")
@require_auth
@requires(ELECTRONIC_FILING, ADD_EDIT)
def revoke_filing_credential(credential_id: str) -> ResponseReturnValue:
    """REVOKE — destroy the item. 204; 404 when the caller has no such
    credential (theirs never existed, or it is somebody else's)."""
    store, _, access_log = _vault()
    accessor = current_accessor()
    if not _CREDENTIAL_ID_RE.match(credential_id):
        raise NotFoundError("filing credential not found")
    if not revoke_credential(
        credential_id,
        firm_id=accessor.firm_id,
        attorney_id=accessor.subject,
        store=store,
        access_log=access_log,
    ):
        raise NotFoundError("filing credential not found")
    logger.info("filing credential revoked")
    return _no_store(Response(status=204))
