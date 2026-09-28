"""Opening a case in a route test, now that a case is opened FOR A CLIENT
(ADR 0022): `POST /v1/cases` requires `client_ids`, so every suite that opens
one first adds a client to the caller's firm through the real route.

One helper rather than a fixture: each test file composes its own app, and
this needs nothing but that file's test client and the caller's headers.
Every value is obviously fake. This repo is public.
"""

from __future__ import annotations

from typing import Any

#: The client every opened case is for unless a test says otherwise.
#: A name and nothing else — the one field a client requires — so the Debtor 1
#: the case opens with changes as little as possible about what the suites
#: that predate clients go on to assert.
CLIENT_BODY: dict[str, Any] = {"name": {"given": "Jordan", "surname": "Example"}}


def add_client(client: Any, headers: dict[str, str], **body: Any) -> str:
    """A new client in the caller's firm; its id."""
    response = client.post(
        "/v1/firm/clients", json={**CLIENT_BODY, **body}, headers=headers
    )
    assert response.status_code == 201, response.get_json()
    return str(response.get_json()["id"])


def with_client(
    client: Any, headers: dict[str, str], opening: dict[str, Any]
) -> dict[str, Any]:
    """`opening` (a `POST /v1/cases` body) for a freshly added client."""
    return {**opening, "client_ids": [add_client(client, headers)]}
