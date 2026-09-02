"""The pipeline job endpoints (ADR 0018).

What matters most, exactly as for every case child resource: what these
REFUSE. The case lookup is the only authorisation there is, so the foreign-
firm and unlinked answers must be indistinguishable from absence. On top of
that, the accept endpoint's idempotency rule (one active job per case and
kind) and the 503-not-lie rule for a deployment with no queue are asserted
here. Tokens are signed for real, mirroring tests/test_case_entity_routes.py.
Every identifier below is obviously fake; this repo is public.
"""

from __future__ import annotations

import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.access_log import MemoryAccessLog
from insolvia_api.adapters.memory.case_store import MemoryCaseStore
from insolvia_api.adapters.memory.job_queue import MemoryJobQueue
from insolvia_api.adapters.memory.job_store import MemoryJobStore
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.firms import Firm, FirmUser, default_permissions

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"
STAN = "00000000-0000-4000-8000-000000005taf"
KID = "test-key-1"

_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC_KEY = _PRIVATE_KEY.public_key()


def token_for(subject: str) -> str:
    now = int(time.time())
    return jwt.encode(
        {
            "sub": subject,
            "iss": ISSUER,
            "client_id": CLIENT_ID,
            "token_use": "access",
            "iat": now,
            "exp": now + 3600,
        },
        _PRIVATE_KEY,  # type: ignore[arg-type]
        algorithm="RS256",
        headers={"kid": KID},
    )


def auth(subject: str) -> dict[str, str]:
    return {"Authorization": f"Bearer {token_for(subject)}"}


def firm(firm_id: str, name: str) -> Firm:
    return Firm(
        id=firm_id,
        name=name,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def member(subject: str, firm_id: str, role: str = "attorney") -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role=role,
        is_admin=role == "attorney",
        # Staff carries access_all_cases so STAN can SEE the case (the
        # case lookup is the first gate) and the 403 below is unambiguously
        # the @requires level, not linkage.
        access_all_cases=role == "staff",
        permissions=default_permissions(role),
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def access_log():
    return MemoryAccessLog()


@pytest.fixture
def job_store():
    return MemoryJobStore()


@pytest.fixture
def job_queue():
    return MemoryJobQueue()


def build_client(access_log, job_store, job_queue, case_store=None):
    firms = MemoryFirmStore()
    firms.create_firm(firm(FIRM_A, "Example & Partners"))
    firms.create_firm(firm(FIRM_B, "Other Firm LLP"))
    firms.add_user(member(ALICE, FIRM_A))
    firms.add_user(member(BOB, FIRM_B))
    # STAN is staff: CASES is view_only, so he can read a job's status but
    # not accept one — the split the two routes' @requires levels encode.
    firms.add_user(member(STAN, FIRM_A, role="staff"))
    app = create_app(
        ApiDependencies(
            config=load_config(
                {
                    "INSOLVIA_ENV": "local",
                    "AUTH_ISSUER_URL": ISSUER,
                    "AUTH_CLIENT_ID": CLIENT_ID,
                }
            ),
            waitlist_store=MemoryWaitlistStore(),
            mailer=InMemoryMailerClient(),
            jwks_provider=StaticJwksProvider({KID: _PUBLIC_KEY}),
            case_store=case_store if case_store is not None else MemoryCaseStore(),
            firm_store=firms,
            access_log=access_log,
            job_store=job_store,
            job_queue=job_queue,
        )
    )
    return app.test_client()


@pytest.fixture
def client(access_log, job_store, job_queue):
    return build_client(access_log, job_store, job_queue)


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases", json={"chapter": 7, "district": "NDCA"}, headers=auth(subject)
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def accept_echo(client, case_id, subject=ALICE):
    return client.post(
        f"/v1/cases/{case_id}/jobs", json={"kind": "echo"}, headers=auth(subject)
    )


# ── Auth and ownership ──────────────────────────────────────────


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("post", "/v1/cases/any-id/jobs"),
        ("get", "/v1/cases/any-id/jobs/any-job"),
    ],
)
def test_every_route_refuses_an_unauthenticated_caller(client, method, path):
    assert getattr(client, method)(path, json={}).status_code == 401


def test_another_firms_case_is_not_found_on_accept(client):
    case_id = open_case(client, ALICE)
    assert accept_echo(client, case_id, subject=BOB).status_code == 404


def test_a_job_is_invisible_to_another_firm(client):
    case_id = open_case(client, ALICE)
    job_id = accept_echo(client, case_id).get_json()["id"]
    foreign = client.get(f"/v1/cases/{case_id}/jobs/{job_id}", headers=auth(BOB))
    missing = client.get("/v1/cases/no-such-case/jobs/no-such-id", headers=auth(BOB))
    assert foreign.status_code == missing.status_code == 404
    assert foreign.get_json() == missing.get_json()


def test_a_job_id_does_not_resolve_through_another_case(client):
    first = open_case(client)
    second = open_case(client)
    job_id = accept_echo(client, first).get_json()["id"]
    response = client.get(f"/v1/cases/{second}/jobs/{job_id}", headers=auth(ALICE))
    assert response.status_code == 404


def test_view_only_staff_can_read_but_not_accept(client):
    case_id = open_case(client, ALICE)
    job_id = accept_echo(client, case_id).get_json()["id"]
    # 403, not 404: STAN can see this case; lying about its existence would
    # contradict his own listing.
    assert accept_echo(client, case_id, subject=STAN).status_code == 403
    read = client.get(f"/v1/cases/{case_id}/jobs/{job_id}", headers=auth(STAN))
    assert read.status_code == 200


# ── Accepting ───────────────────────────────────────────────────


def test_accepting_a_job_records_and_enqueues(client, job_store, job_queue):
    case_id = open_case(client)
    response = accept_echo(client, case_id)
    assert response.status_code == 202
    body = response.get_json()
    assert body["kind"] == "echo"
    assert body["status"] == "queued"
    assert body["createdBy"] == ALICE
    assert body["attempts"] == 0
    assert "failure" not in body
    assert "result" not in body

    # The record was written BEFORE the message was sent, and the message is
    # the pinned wire shape — identifiers only, never case data.
    stored = job_store.get(case_id, body["id"])
    assert stored is not None
    assert job_queue.messages == [
        {"version": 1, "jobId": body["id"], "caseId": case_id, "kind": "echo"}
    ]


def test_an_unknown_kind_is_rejected_per_field(client):
    case_id = open_case(client)
    response = client.post(
        f"/v1/cases/{case_id}/jobs",
        json={"kind": "assemble_packet_from_the_future"},
        headers=auth(ALICE),
    )
    assert response.status_code == 400
    assert "kind" in response.get_json()["fields"]


def test_a_repeat_accept_returns_the_active_job_not_a_duplicate(
    client, job_store, job_queue
):
    case_id = open_case(client)
    first = accept_echo(client, case_id).get_json()
    second = accept_echo(client, case_id)
    assert second.status_code == 202
    assert second.get_json()["id"] == first["id"]
    # One record, one message: the client re-POSTing because it cannot tell
    # whether its first request landed must not start a second pipeline run.
    assert len(job_store.list_for_case(case_id)) == 1
    assert len(job_queue.messages) == 1


def test_a_finished_job_does_not_block_a_new_accept(client, job_store):
    case_id = open_case(client)
    first = accept_echo(client, case_id).get_json()
    stored = job_store.get(case_id, first["id"])
    from insolvia_api.core.jobs import complete, start_attempt

    job_store.update(start_attempt(stored), expected_status="queued")
    running = job_store.get(case_id, first["id"])
    job_store.update(
        complete(running, {"echo": first["id"]}), expected_status="running"
    )

    second = accept_echo(client, case_id).get_json()
    assert second["id"] != first["id"]


def test_accepting_writes_the_access_log(client, access_log):
    case_id = open_case(client)
    accept_echo(client, case_id)
    actions = [event.action for event in access_log.events]
    assert "job.accept" in actions


def test_a_repeat_accept_is_not_access_logged_again(client, access_log):
    case_id = open_case(client)
    accept_echo(client, case_id)
    accept_echo(client, case_id)
    assert [e.action for e in access_log.events].count("job.accept") == 1


def test_without_a_queue_the_accept_answers_503(access_log, job_store):
    # The real deploy-order window: the api image rolls out before the infra
    # that creates the queue. Refusing is honest; accepting would record work
    # nothing will ever run.
    client = build_client(access_log, job_store, job_queue=None)
    case_id = open_case(client)
    response = accept_echo(client, case_id)
    assert response.status_code == 503
    assert response.get_json()["error"] == "PipelineUnavailable"
    assert job_store.list_for_case(case_id) == ()


def test_an_enqueue_failure_fails_the_job_rather_than_wedging_it(access_log, job_store):
    class BrokenQueue:
        def enqueue(self, job):
            raise ConnectionError("sqs unreachable")

    cases = MemoryCaseStore()
    client = build_client(access_log, job_store, BrokenQueue(), case_store=cases)
    case_id = open_case(client)
    assert accept_echo(client, case_id).status_code == 500
    jobs = job_store.list_for_case(case_id)
    assert len(jobs) == 1
    # Failed, not queued: a queued row with no message would block every
    # future accept of this kind through the idempotency rule.
    assert jobs[0].status == "failed"
    assert jobs[0].failure.category == "enqueue_failed"

    # And the client's retry starts clean once the queue is back.
    healed = build_client(access_log, job_store, MemoryJobQueue(), case_store=cases)
    retry = accept_echo(healed, case_id)
    assert retry.status_code == 202
    assert retry.get_json()["id"] != jobs[0].id


# ── Status reads ────────────────────────────────────────────────


def test_a_jobs_status_is_read_back(client):
    case_id = open_case(client)
    job_id = accept_echo(client, case_id).get_json()["id"]
    response = client.get(f"/v1/cases/{case_id}/jobs/{job_id}", headers=auth(ALICE))
    assert response.status_code == 200
    body = response.get_json()
    assert body["id"] == job_id
    assert body["status"] == "queued"


def test_a_missing_job_in_a_visible_case_is_not_found(client):
    case_id = open_case(client)
    response = client.get(f"/v1/cases/{case_id}/jobs/no-such-job", headers=auth(ALICE))
    assert response.status_code == 404


def test_a_failed_jobs_reason_reaches_the_preparer(client, job_store):
    from insolvia_api.core.jobs import JobFailure, fail, start_attempt

    case_id = open_case(client)
    job_id = accept_echo(client, case_id).get_json()["id"]
    running = job_store.update(
        start_attempt(job_store.get(case_id, job_id)), expected_status="queued"
    )
    job_store.update(
        fail(running, JobFailure("case_incomplete", "The case has no debtor yet.")),
        expected_status="running",
    )

    body = client.get(
        f"/v1/cases/{case_id}/jobs/{job_id}", headers=auth(ALICE)
    ).get_json()
    assert body["status"] == "failed"
    assert body["failure"] == {
        "category": "case_incomplete",
        "message": "The case has no debtor yet.",
    }
