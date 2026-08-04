class ApiError(Exception):
    """Base class for expected Insolvia API failures."""


class ValidationError(ApiError):
    """A caller or the environment supplied an invalid value."""


class FieldValidationError(ValidationError):
    """Per-field validation failures, keyed by the request's JSON field names.

    The API layer maps this to a 400 with an {"error", "fields"} body so the
    marketing site's action can surface each message next to its input.
    """

    def __init__(self, fields: dict[str, str]) -> None:
        super().__init__("validation failed: " + ", ".join(sorted(fields)))
        self.fields = fields


class NotFoundError(ApiError):
    """The requested resource does not exist, or does not belong to the caller.

    Deliberately one error for both. A case that exists but belongs to someone
    else answers 404, not 403: a 403 would confirm the id is real, which turns
    this endpoint into an oracle for enumerating other firms' case ids.
    """
