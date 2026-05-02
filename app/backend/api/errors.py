"""API error types and helpers."""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Optional


@dataclass
class ApiError:
    """Standard error envelope returned by the API."""
    code: str
    message: str
    details: Optional[Any] = None

    def to_dict(self) -> dict:
        d = {"code": self.code, "message": self.message}
        if self.details is not None:
            d["details"] = self.details
        return d


def make_error(code: str, message: str, details: Any = None) -> ApiError:
    return ApiError(code=code, message=message, details=details)
