"""Shared value-level validation checks used by the bridge server and material preview loader."""

import math


class ValidationError(ValueError):
    """Raised when a value fails a shared structural check; callers translate this into their own error type."""


def validate_string(
    value, message: str, *, max_length: int, allow_none: bool = False, require_non_empty: bool = False
) -> str:
    if value is None and allow_none:
        return ""
    if not isinstance(value, str) or len(value) > max_length or (require_non_empty and not value):
        raise ValidationError(message)
    return value


def validate_integer(value, message: str, *, minimum: int = 0, maximum: int = 0xFFFFFFFF) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value <= maximum:
        raise ValidationError(message)
    return value


def validate_bounded_list(value, message: str, *, maximum: int) -> list:
    if not isinstance(value, list) or len(value) > maximum:
        raise ValidationError(message)
    return value


def validate_number(value, message: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValidationError(message)
    return float(value)
