"""The packet record (issue #96): identity, the stored item shape, and the
object key — mirroring test_documents.py for the sibling record."""

from __future__ import annotations

import pytest
from insolvia_api.core.packets import (
    PACKET_CONTENT_TYPE,
    PACKET_FILE_NAME,
    list_order,
    new_packet,
    packet_from_item,
    packet_item,
    packet_json,
    packet_object_key,
)
from insolvia_core.errors import ValidationError

CASE_ID = "11111111-2222-4333-8444-555555555555"
JOB_ID = "99999999-8888-4777-8666-555555555555"


def make_packet(**overrides):
    values = {
        "case_id": CASE_ID,
        "job_id": JOB_ID,
        "byte_size": 1234,
        "sha256": "ab" * 32,
        "form_revisions": {"form/b101": "2024-06-22"},
        "creditor_count": 4,
        "created_by": "subject-1",
    }
    values.update(overrides)
    return new_packet(**values)


def test_a_new_packet_derives_its_storage_ref_from_server_minted_ids():
    packet = make_packet()
    assert packet.storage_ref == f"cases/{CASE_ID}/packets/{packet.id}"
    assert packet.file_name == PACKET_FILE_NAME
    assert packet.content_type == PACKET_CONTENT_TYPE


@pytest.mark.parametrize(
    ("case_id", "packet_id"),
    [
        ("not-a-uuid", "11111111-2222-4333-8444-555555555555"),
        ("11111111-2222-4333-8444-555555555555", "smith-jane-packet"),
        # The \Z-not-$ rule: a trailing newline must not sneak into a key.
        (CASE_ID, "11111111-2222-4333-8444-555555555555\n"),
    ],
)
def test_object_keys_are_built_from_uuids_only(case_id, packet_id):
    with pytest.raises(ValidationError):
        packet_object_key(case_id, packet_id)


def test_the_item_shape_round_trips():
    packet = make_packet()
    item = packet_item(packet)
    assert item["PK"] == f"CASE#{CASE_ID}"
    assert item["SK"] == f"PACKET#{packet.id}"
    assert packet_from_item(item) == packet


def test_a_malformed_item_fails_loudly():
    item = packet_item(make_packet())
    del item["sha256"]
    with pytest.raises(ValidationError):
        packet_from_item(item)


def test_the_json_shape_hides_the_storage_ref():
    packet = make_packet()
    body = packet_json(packet)
    assert "storageRef" not in body
    assert body["id"] == packet.id
    assert body["sha256"] == packet.sha256
    assert body["formRevisions"] == {"form/b101": "2024-06-22"}
    assert body["creditorCount"] == 4


def test_list_order_is_creation_order_with_id_tiebreak():
    first = make_packet()
    second = make_packet()
    ordered = sorted([second, first], key=list_order)
    assert ordered[0].created_at <= ordered[1].created_at
