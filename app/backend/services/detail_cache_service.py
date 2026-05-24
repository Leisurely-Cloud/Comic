from __future__ import annotations

import json
import os
from typing import Any, Callable, Dict, Optional


class DetailCacheService:
    """漫画详情缓存：读写 JSON 文件，按 source_url 做 key。"""

    def __init__(
        self,
        *,
        cache_file: str,
        legacy_cache_file: str = "",
        on_warning: Optional[Callable[[str], None]] = None,
    ):
        self._cache_file = cache_file
        self._legacy_cache_file = legacy_cache_file or ""
        self._on_warning = on_warning

    def _warn(self, message: str) -> None:
        if self._on_warning is not None:
            try:
                self._on_warning(message)
            except Exception:
                pass

    def load(self) -> Dict[str, Any]:
        for candidate in [self._cache_file, self._legacy_cache_file]:
            if not candidate or not os.path.exists(candidate):
                continue
            try:
                with open(candidate, "r", encoding="utf-8") as f:
                    data = json.load(f)
                if isinstance(data, dict):
                    return data
            except Exception as exc:
                self._warn(f"读取漫画详情缓存失败 ({candidate}): {exc}")
        return {}

    def save(self, cache: Dict[str, Any]) -> None:
        try:
            os.makedirs(os.path.dirname(self._cache_file) or ".", exist_ok=True)
            tmp_path = self._cache_file + ".tmp"
            with open(tmp_path, "w", encoding="utf-8") as f:
                json.dump(cache, f, ensure_ascii=False, indent=2)
            os.replace(tmp_path, self._cache_file)
        except Exception as exc:
            self._warn(f"保存漫画详情缓存失败: {exc}")

    def _make_cache_key(self, adapter: Any, source_url: str) -> str:
        get_key = getattr(adapter, "get_manga_cache_key", None)
        if callable(get_key):
            return get_key(source_url)
        return (source_url or "").strip()

    def cache_detail(
        self,
        cache: Dict[str, Any],
        adapter: Any,
        source_url: str,
        detail: Any,
    ) -> Dict[str, Any]:
        cache_key = self._make_cache_key(adapter, source_url)
        if not cache_key:
            return cache

        from backend.support.site_adapters import MangaDetail  # noqa: lazy import

        if not isinstance(detail, MangaDetail):
            return cache

        payload: Dict[str, Any] = {
            "site_key": getattr(adapter, "key", ""),
            "site_name": getattr(adapter, "display_name", ""),
            "title": detail.title or "",
            "manga_url": detail.manga_url or source_url,
            "cover_url": detail.cover_url or "",
            "latest_chapter": detail.latest_chapter or "",
            "update_time": detail.update_time or "",
            "detail_hint": detail.detail_hint or "",
            "detail_section_label": detail.detail_section_label or "",
            "chapter_count": int(detail.chapter_count or 0),
            "start_chapter_title": detail.start_chapter_title or "",
        }
        updated = dict(cache)
        updated[cache_key] = payload
        self.save(updated)
        return updated

    def get_cached_detail(
        self,
        cache: Dict[str, Any],
        adapter: Any,
        source_url: str,
    ) -> Optional[Any]:
        cache_key = self._make_cache_key(adapter, source_url)
        payload = cache.get(cache_key)
        if not isinstance(payload, dict):
            return None

        from backend.support.site_adapters import MangaDetail  # noqa: lazy import

        return MangaDetail(
            title=payload.get("title", ""),
            manga_url=payload.get("manga_url", ""),
            section=payload.get("section", ""),
            cover_url=payload.get("cover_url", ""),
            latest_chapter=payload.get("latest_chapter", ""),
            update_time=payload.get("update_time", ""),
            detail_hint=payload.get("detail_hint", ""),
            detail_section_label=payload.get("detail_section_label", ""),
            chapter_count=int(payload.get("chapter_count") or 0),
            start_chapter_title=payload.get("start_chapter_title", ""),
        )
