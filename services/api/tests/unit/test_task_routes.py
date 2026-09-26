"""The task endpoints (issue #356 / 14.4).

Weighted the same way test_case_entity_routes.py and test_document_routes.py
are: what these REFUSE matters most, because `TaskStore` enforces no
ownership of its own and the case lookup is the only thing between one firm's
checklist and another's. `GET /v1/me/tasks` gets its own section — it is the
one route here that reads across cases, and the property that matters is that
it can never hand back a task from a case the caller cannot otherwise reach
(ADR 0009).

Tokens are signed for real, mirroring the neighbours above.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.task_store import MemoryTaskStore
from insolvia_core.firms import (
    HIDDEN,
    TASKS,
    VIEW_ONLY,
    Firm,
    FirmUser,
    default_permissions,
)

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"
CARL = "00000000-0000-4000-8000-00000000ca71"
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


def member(
    subject: str,
    firm_id: str,
    *,
    is_admin: bool = True,
    access_all_cases: bool = False,
    permissions: dict[str, str] | None = None,
) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role="attorney",
        is_admin=is_admin,
        access_all_cases=access_all_cases,
        permissions=permissions or default_permissions("attorney"),
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def access_log():
    return MemoryAccessLog()


@pytest.fixture
def firm_store():
    firms = MemoryFirmStore()
    firms.create_firm(firm(FIRM_A, "Example & Partners"))
    firms.create_firm(firm(FIRM_B, "Other Firm LLP"))
    # ALICE: admin of firm A. BOB: a non-admin colleague at firm A, unlinked
    # to any case by default — the "assigned to me can't see it" case. CARL:
    # a different firm entirely.
    firms.add_user(member(ALICE, FIRM_A, is_admin=True))
    firms.add_user(member(BOB, FIRM_A, is_admin=False, access_all_cases=False))
    firms.add_user(member(CARL, FIRM_B, is_admin=True))
    return firms


@pytest.fixture
def case_store():
    return MemoryCaseStore()


@pytest.fixture
def task_store():
    return MemoryTaskStore()


@pytest.fixture
def client(access_log, firm_store, case_store, task_store):
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
            case_store=case_store,
            firm_store=firm_store,
            access_log=access_log,
            task_store=task_store,
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases", json={"chapter": 7, "district": "NDCA"}, headers=auth(subject)
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def add_task(client, case_id, subject=ALICE, body=None):
    return client.post(
        f"/v1/cases/{case_id}/tasks",
        json=body if body is not None else {"subject": "Get the vehicle payoff"},
        headers=auth(subject),
    )


# ── Auth, permission and ownership ──────────────────────────────


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("post", "/v1/cases/any-id/tasks"),
        ("get", "/v1/cases/any-id/tasks"),
        ("get", "/v1/cases/any-id/tasks/any-task"),
        ("patch", "/v1/cases/any-id/tasks/any-task"),
        ("delete", "/v1/cases/any-id/tasks/any-task"),
        ("get", "/v1/me/tasks"),
    ],
)
def test_every_route_refuses_an_unauthenticated_caller(client, method, path):
    assert getattr(client, method)(path, json={}).status_code == 401


def test_another_firms_case_is_not_found_on_write(client):
    case_id = open_case(client, ALICE)
    assert add_task(client, case_id, subject=CARL).status_code == 404


def test_a_task_written_by_one_firm_is_invisible_to_another(client):
    case_id = open_case(client, ALICE)
    task_id = add_task(client, case_id).get_json()["id"]
    foreign = client.get(f"/v1/cases/{case_id}/tasks/{task_id}", headers=auth(CARL))
    missing = client.get("/v1/cases/no-such-case/tasks/no-such-id", headers=auth(CARL))
    assert foreign.status_code == missing.status_code == 404
    assert foreign.get_json() == missing.get_json()


def test_a_task_id_from_another_case_does_not_resolve(client):
    case_a = open_case(client)
    case_b = open_case(client)
    task_id = add_task(client, case_a).get_json()["id"]
    response = client.get(f"/v1/cases/{case_b}/tasks/{task_id}", headers=auth(ALICE))
    assert response.status_code == 404


def test_view_only_on_tasks_cannot_create(client, firm_store):
    # BOB is already firm A and unlinked to the case; here he is also linked,
    # to isolate the TASKS permission from the case-linkage question above.
    case_id = open_case(client, ALICE)
    client.put(f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(ALICE))
    firm_store.update_user(
        member(
            BOB,
            FIRM_A,
            is_admin=False,
            permissions={**default_permissions("attorney"), TASKS: VIEW_ONLY},
        )
    )
    response = add_task(client, case_id, subject=BOB)
    assert response.status_code == 403


def test_tasks_hidden_cannot_list(client, firm_store):
    case_id = open_case(client, ALICE)
    client.put(f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(ALICE))
    firm_store.update_user(
        member(
            BOB,
            FIRM_A,
            is_admin=False,
            permissions={**default_permissions("attorney"), TASKS: HIDDEN},
        )
    )
    response = client.get(f"/v1/cases/{case_id}/tasks", headers=auth(BOB))
    assert response.status_code == 403


# ── Create ───────────────────────────────────────────────────────


def test_creating_a_task_answers_201_with_the_stored_record(client):
    case_id = open_case(client)
    response = add_task(
        client,
        case_id,
        body={
            "subject": "Get the vehicle payoff",
            "description": "Call the lender on the credit report.",
            "dueDate": "2026-10-01",
            "formSeries": "b106d",
        },
    )
    assert response.status_code == 201
    body = response.get_json()
    assert body["subject"] == "Get the vehicle payoff"
    assert body["description"] == "Call the lender on the credit report."
    assert body["dueDate"] == "2026-10-01"
    assert body["formSeries"] == "b106d"
    assert body["caseId"] == case_id
    assert body["done"] is False
    assert "completedAt" not in body
    assert body["overdue"] is False
    assert body["id"]


def test_subject_is_the_only_required_field(client):
    case_id = open_case(client)
    response = add_task(client, case_id, body={"subject": "Follow up"})
    assert response.status_code == 201


def test_creating_without_a_subject_is_a_400(client):
    case_id = open_case(client)
    response = add_task(client, case_id, body={})
    assert response.status_code == 400
    assert "subject" in response.get_json()["fields"]


def test_assigning_on_create_to_someone_outside_the_firm_is_refused(client):
    case_id = open_case(client)
    response = add_task(client, case_id, body={"subject": "x", "assigneeSubject": CARL})
    assert response.status_code == 400
    assert "assigneeSubject" in response.get_json()["fields"]


def test_assigning_on_create_to_a_firm_colleague_succeeds(client):
    case_id = open_case(client)
    response = add_task(client, case_id, body={"subject": "x", "assigneeSubject": BOB})
    assert response.status_code == 201
    assert response.get_json()["assigneeSubject"] == BOB


def test_an_overdue_task_is_marked_overdue(client):
    case_id = open_case(client)
    response = add_task(
        client, case_id, body={"subject": "Old task", "dueDate": "2000-01-01"}
    )
    assert response.get_json()["overdue"] is True


# ── List and get ─────────────────────────────────────────────────


def test_listing_is_creation_order(client):
    case_id = open_case(client)
    first = add_task(client, case_id, body={"subject": "First"}).get_json()["id"]
    second = add_task(client, case_id, body={"subject": "Second"}).get_json()["id"]
    response = client.get(f"/v1/cases/{case_id}/tasks", headers=auth(ALICE))
    assert [t["id"] for t in response.get_json()["tasks"]] == [first, second]


def test_getting_an_unknown_task_is_404(client):
    case_id = open_case(client)
    response = client.get(f"/v1/cases/{case_id}/tasks/no-such-id", headers=auth(ALICE))
    assert response.status_code == 404


# ── Edit, complete, reassign ──────────────────────────────────────


def test_patch_edits_only_the_named_fields(client):
    case_id = open_case(client)
    task = add_task(
        client, case_id, body={"subject": "x", "dueDate": "2026-10-01"}
    ).get_json()
    response = client.patch(
        f"/v1/cases/{case_id}/tasks/{task['id']}",
        json={"subject": "Renamed"},
        headers=auth(ALICE),
    )
    assert response.status_code == 200
    body = response.get_json()
    assert body["subject"] == "Renamed"
    assert body["dueDate"] == "2026-10-01"


def test_patch_with_no_fields_is_a_400(client):
    case_id = open_case(client)
    task = add_task(client, case_id).get_json()
    response = client.patch(
        f"/v1/cases/{case_id}/tasks/{task['id']}", json={}, headers=auth(ALICE)
    )
    assert response.status_code == 400


def test_patch_clears_a_due_date_with_explicit_null(client):
    case_id = open_case(client)
    task = add_task(
        client, case_id, body={"subject": "x", "dueDate": "2026-10-01"}
    ).get_json()
    response = client.patch(
        f"/v1/cases/{case_id}/tasks/{task['id']}",
        json={"dueDate": None},
        headers=auth(ALICE),
    )
    assert "dueDate" not in response.get_json()


def test_completing_a_task_stamps_completed_at(client):
    case_id = open_case(client)
    task = add_task(client, case_id).get_json()
    response = client.patch(
        f"/v1/cases/{case_id}/tasks/{task['id']}",
        json={"done": True},
        headers=auth(ALICE),
    )
    body = response.get_json()
    assert body["done"] is True
    assert body["completedAt"]


def test_reopening_clears_completed_at(client):
    case_id = open_case(client)
    task = add_task(client, case_id).get_json()
    client.patch(
        f"/v1/cases/{case_id}/tasks/{task['id']}",
        json={"done": True},
        headers=auth(ALICE),
    )
    response = client.patch(
        f"/v1/cases/{case_id}/tasks/{task['id']}",
        json={"done": False},
        headers=auth(ALICE),
    )
    body = response.get_json()
    assert body["done"] is False
    assert "completedAt" not in body


def test_reassigning_to_a_colleague_succeeds(client):
    case_id = open_case(client)
    task = add_task(client, case_id).get_json()
    response = client.patch(
        f"/v1/cases/{case_id}/tasks/{task['id']}",
        json={"assigneeSubject": BOB},
        headers=auth(ALICE),
    )
    assert response.get_json()["assigneeSubject"] == BOB


def test_reassigning_to_a_stranger_is_refused(client):
    case_id = open_case(client)
    task = add_task(client, case_id).get_json()
    response = client.patch(
        f"/v1/cases/{case_id}/tasks/{task['id']}",
        json={"assigneeSubject": CARL},
        headers=auth(ALICE),
    )
    assert response.status_code == 400


def test_unassigning_with_explicit_null_succeeds(client):
    case_id = open_case(client)
    task = add_task(
        client, case_id, body={"subject": "x", "assigneeSubject": BOB}
    ).get_json()
    response = client.patch(
        f"/v1/cases/{case_id}/tasks/{task['id']}",
        json={"assigneeSubject": None},
        headers=auth(ALICE),
    )
    assert "assigneeSubject" not in response.get_json()


def test_patching_an_unknown_task_is_404(client):
    case_id = open_case(client)
    response = client.patch(
        f"/v1/cases/{case_id}/tasks/no-such-id",
        json={"subject": "x"},
        headers=auth(ALICE),
    )
    assert response.status_code == 404


# ── Delete ───────────────────────────────────────────────────────


def test_deleting_a_task_removes_it(client):
    case_id = open_case(client)
    task = add_task(client, case_id).get_json()
    response = client.delete(
        f"/v1/cases/{case_id}/tasks/{task['id']}", headers=auth(ALICE)
    )
    assert response.status_code == 204
    assert (
        client.get(
            f"/v1/cases/{case_id}/tasks/{task['id']}", headers=auth(ALICE)
        ).status_code
        == 404
    )


def test_deleting_an_unknown_task_is_404(client):
    case_id = open_case(client)
    response = client.delete(
        f"/v1/cases/{case_id}/tasks/no-such-id", headers=auth(ALICE)
    )
    assert response.status_code == 404


# ── GET /v1/me/tasks — reachability across cases (ADR 0009) ──────


def test_assigned_to_me_lists_tasks_across_reachable_cases(client):
    case_1 = open_case(client, ALICE)
    case_2 = open_case(client, ALICE)
    add_task(client, case_1, body={"subject": "In case 1", "assigneeSubject": ALICE})
    add_task(client, case_2, body={"subject": "In case 2", "assigneeSubject": ALICE})
    add_task(client, case_2, body={"subject": "Not mine", "assigneeSubject": BOB})

    response = client.get("/v1/me/tasks", headers=auth(ALICE))
    assert response.status_code == 200
    subjects = {t["subject"] for t in response.get_json()["tasks"]}
    assert subjects == {"In case 1", "In case 2"}


def test_assigned_to_me_never_leaks_a_task_from_an_unreachable_case(client):
    """THE PROPERTY THIS SECTION EXISTS FOR. BOB is a non-admin, non
    access-all-cases member of firm A — see the `member` fixture default —
    and is not linked to `case_id`. A task assigned to him there must not
    appear in his own "assigned to me" list, exactly as ADR 0009 says a case
    he is not linked to must not appear in his case list."""
    case_id = open_case(client, ALICE)
    add_task(
        client,
        case_id,
        body={"subject": "Assigned but unreachable", "assigneeSubject": BOB},
    )

    response = client.get("/v1/me/tasks", headers=auth(BOB))
    assert response.status_code == 200
    assert response.get_json()["tasks"] == []


def test_assigned_to_me_never_crosses_firms(client):
    case_id = open_case(client, ALICE)
    add_task(
        client, case_id, body={"subject": "Firm A's task", "assigneeSubject": ALICE}
    )

    response = client.get("/v1/me/tasks", headers=auth(CARL))
    assert response.status_code == 200
    assert response.get_json()["tasks"] == []


def test_assigned_to_me_reaches_a_task_once_the_case_is_linked(client, case_store):
    """The flip side of the leak test: once BOB is actually linked to the
    case, his assigned task appears — proving the emptiness above was about
    reachability, not about the listing being broken."""
    case_id = open_case(client, ALICE)
    add_task(client, case_id, body={"subject": "Now reachable", "assigneeSubject": BOB})

    link = client.put(f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(ALICE))
    assert link.status_code == 204

    response = client.get("/v1/me/tasks", headers=auth(BOB))
    subjects = {t["subject"] for t in response.get_json()["tasks"]}
    assert subjects == {"Now reachable"}


def test_assigned_to_me_sorts_by_due_date_then_undated_last(client):
    case_id = open_case(client, ALICE)
    add_task(
        client,
        case_id,
        body={"subject": "No date", "assigneeSubject": ALICE},
    )
    add_task(
        client,
        case_id,
        body={"subject": "Later", "assigneeSubject": ALICE, "dueDate": "2026-12-01"},
    )
    add_task(
        client,
        case_id,
        body={"subject": "Soonest", "assigneeSubject": ALICE, "dueDate": "2026-10-01"},
    )

    response = client.get("/v1/me/tasks", headers=auth(ALICE))
    subjects = [t["subject"] for t in response.get_json()["tasks"]]
    assert subjects == ["Soonest", "Later", "No date"]
