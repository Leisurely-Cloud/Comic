from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Optional


@dataclass(frozen=True)
class MangaDetailRequest:
    url: str
    fallback_site_key: Optional[str] = None


@dataclass
class MangaDetailResult:
    adapter: Any
    detail: Any
    used_fallback: bool = False
    fallback_source: str = ""
