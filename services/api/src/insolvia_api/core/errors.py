class ApiError(Exception):
    """Base class for expected Insolvia API failures."""


class ValidationError(ApiError):
    """A caller or the environment supplied an invalid value."""
