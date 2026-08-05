"""One DynamoDB attribute-value converter, shared by the stores in this layer.

boto3's low-level client speaks the wire format — every value wrapped in a
single-key map naming its type. The `boto3.resource` / `TypeSerializer` API
would hide that, at the price of a `Decimal`-based number model: a Decimal
escaping a store would travel up through `core` and only fail at
`json.dumps`, several layers from anything that could explain it. So the
translation is done here, in plain Python types.

This started as two flat helpers in case_store.py that handled S and N at the
top level, which is all a case item has. A debtor item carries a nested `body`
map and a `provenance` map-of-maps (core/debtors.py), so the conversion had to
become recursive — and it moved here rather than being copied, because two
converters that must agree on the encoding are two converters that will
eventually disagree about a bool.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from math import isfinite
from typing import Any


def to_attributes(item: Mapping[str, object]) -> dict[str, Any]:
    """A plain item as DynamoDB attribute values."""
    return {key: _to_value(value) for key, value in item.items()}


def from_attributes(item: Mapping[str, Any]) -> dict[str, Any]:
    """Inverse of `to_attributes`, for every type it can write.

    One asymmetry, and it is DynamoDB's rather than this module's: there is a
    single sequence type, so a tuple comes back as a list. Nothing in `core`
    depends on the difference — the parse functions rebuild their own tuples
    from whatever they are handed.
    """
    return {key: _from_value(value) for key, value in item.items()}


def _to_value(value: object) -> dict[str, Any]:
    # bool is checked BEFORE int because in Python it *is* an int: with the
    # branches the other way round `True` stores as the number 1, reads back
    # as 1, and compares equal to True at every assertion that would have
    # caught it. Nothing about the failure looks like a type error.
    if value is None:
        return {"NULL": True}
    if isinstance(value, bool):
        return {"BOOL": value}
    if isinstance(value, str):
        return {"S": value}
    if isinstance(value, (int, float)):
        # Infinity and NaN are stdlib-JSON extensions, so `request.get_json()`
        # accepts them and they reach here as real floats. DynamoDB refuses
        # `{"N": "inf"}`, and refusing it here turns a 500 from the real
        # store — which the memory store would never have reproduced — into a
        # failure at the boundary. It is also not JSON: a response echoing
        # `Infinity` cannot be parsed by any strict client.
        if isinstance(value, float) and not isfinite(value):
            raise TypeError(f"cannot store a non-finite number: {value!r}")
        # str() of a float is the shortest form that reads back as the same
        # float, which is what makes the round trip below exact.
        return {"N": str(value)}
    if isinstance(value, Mapping):
        return {"M": {str(key): _to_value(member) for key, member in value.items()}}
    if isinstance(value, Sequence) and not isinstance(value, (bytes, bytearray)):
        return {"L": [_to_value(member) for member in value]}
    # Binary (B) is deliberately absent: nothing in the case data model stores
    # bytes, and bytes IS a Sequence, so leaving it to fall through would
    # quietly write it as a list of integers rather than refusing it.
    raise TypeError(f"cannot store a {type(value).__name__} in DynamoDB")


def _from_value(value: Mapping[str, Any]) -> Any:
    if "S" in value:
        return value["S"]
    if "N" in value:
        return _number(value["N"])
    if "BOOL" in value:
        return bool(value["BOOL"])
    if "NULL" in value:
        return None
    if "M" in value:
        return {key: _from_value(member) for key, member in value["M"].items()}
    if "L" in value:
        return [_from_value(member) for member in value["L"]]
    # A set (SS/NS/BS) or a binary is something this service never wrote, so
    # the row came from somewhere else. Failing here beats handing a caller a
    # record with an attribute silently missing.
    raise ValueError(f"unsupported DynamoDB attribute: {', '.join(value)}")


def _number(raw: str) -> int | float:
    """DynamoDB has one number type; Python has two, and which one comes back
    matters — `case_from_item` wants an int chapter and a provenance
    confidence is a float. The split is exact rather than heuristic: `str()` of
    a float always carries a `.` or an exponent, so no float that `_to_value`
    wrote can parse as an int here."""
    try:
        return int(raw)
    except ValueError:
        return float(raw)
