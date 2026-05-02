from __future__ import annotations

import os
from typing import Any, Callable, Dict, List, Optional

from backend.support.local_library import (
    build_local_library_entry_from_fallback,
    enrich_local_library_entry_identity,
    get_library_update_status_lines as _get_library_update_status_lines,
    iter_local_library_entries as _iter_local_library_entries,
    load_manga_library_metadata,
    save_library_entry_metadata as _save_library_entry_metadata,
)


class LibraryService:
    """本地漫画库的读写服务。"""

    def __init__(self, *, storage_root_dir: str, legacy_root_dir: str = ""):
        self._storage_root_dir = storage_root_dir
        self._legacy_root_dir = legacy_root_dir

    def get_search_roots(self) -> List[str]:
        roots: List[str] = []
        for candidate in [self._storage_root_dir, self._legacy_root_dir]:
            if candidate and os.path.isdir(candidate) and candidate not in roots:
                roots.append(candidate)
        return roots

    def get_library_update_status_lines(
        self, entry: Dict[str, Any], *, include_error: bool = False
    ) -> List[str]:
        return _get_library_update_status_lines(entry, include_error=include_error)

    def get_local_library_entry_by_root(
        self,
        *,
        root_dir: str,
        saved_detail_cache: Optional[Dict[str, Any]] = None,
        site_key: str = "",
        default_site_display_name: str = "",
    ) -> Optional[Dict[str, Any]]:
        metadata = load_manga_library_metadata(root_dir)
        if metadata is not None:
            return enrich_local_library_entry_identity(
                metadata, saved_detail_cache=saved_detail_cache, preferred_site_key=site_key
            )
        fallback = build_local_library_entry_from_fallback(
            root_dir, saved_detail_cache=saved_detail_cache, site_key=site_key
        )
        if fallback is not None and not fallback.get("site_name"):
            fallback["site_name"] = default_site_display_name
        return fallback

    def find_local_library_entry_by_source_url(
        self,
        *,
        adapter: Any,
        source_url: str,
        saved_detail_cache: Optional[Dict[str, Any]] = None,
        default_site_display_name: str = "",
        root_dir_finder: Optional[Callable[[Any, str], Optional[str]]] = None,
    ) -> Optional[Dict[str, Any]]:
        root_dir = None
        if root_dir_finder is not None:
            root_dir = root_dir_finder(adapter, source_url)
        if not root_dir or not os.path.isdir(root_dir):
            return None
        entry = self.get_local_library_entry_by_root(
            root_dir=root_dir,
            saved_detail_cache=saved_detail_cache,
            site_key=adapter.key if adapter else "",
            default_site_display_name=default_site_display_name,
        )
        if entry is not None and not entry.get("manga_url"):
            entry["manga_url"] = source_url or ""
        return entry

    def get_local_library_entry_for_card(
        self,
        *,
        card: Any,
        saved_detail_cache: Optional[Dict[str, Any]] = None,
        current_site_key: str = "",
        default_site_display_name: str = "",
        root_dir_finder: Optional[Callable[[Any, str], Optional[str]]] = None,
    ) -> Optional[Dict[str, Any]]:
        card_url = getattr(card, "manga_url", "") or ""
        card_site_key = getattr(card, "site_key", "") or current_site_key
        root_dir = getattr(card, "root_dir", "") or ""
        if not root_dir and root_dir_finder is not None and card_url:
            from backend.support.site_adapters import get_adapter
            adapter = get_adapter(card_site_key)
            root_dir = root_dir_finder(adapter, card_url) or ""
        if not root_dir or not os.path.isdir(root_dir):
            return None
        entry = self.get_local_library_entry_by_root(
            root_dir=root_dir,
            saved_detail_cache=saved_detail_cache,
            site_key=card_site_key,
            default_site_display_name=default_site_display_name,
        )
        if entry is not None:
            if not entry.get("manga_url") and card_url:
                entry["manga_url"] = card_url
            if not entry.get("cover_url"):
                entry["cover_url"] = getattr(card, "cover_url", "") or ""
        return entry

    def save_library_entry_metadata(
        self,
        entry: Dict[str, Any],
        *,
        on_error: Optional[Callable[[BaseException], None]] = None,
    ) -> bool:
        return _save_library_entry_metadata(entry, on_error=on_error)

    def load_library_entry_metadata(self, root_dir: str) -> Optional[Dict[str, Any]]:
        return load_manga_library_metadata(root_dir)

    def iter_library_entries(
        self,
        *,
        saved_detail_cache: Optional[Dict[str, Any]] = None,
        site_key: str = "",
        default_site_display_name: str = "",
    ) -> List[Dict[str, Any]]:
        return _iter_local_library_entries(
            library_search_roots=self.get_search_roots(),
            saved_detail_cache=saved_detail_cache,
            site_key=site_key,
            default_site_display_name=default_site_display_name,
        )

    def build_local_library_cards(
        self,
        *,
        saved_detail_cache: Optional[Dict[str, Any]] = None,
        page_size: int = 50,
        current_site_display_name: str = "",
        site_key: str = "",
        page: int = 1,
        keyword: str = "",
    ) -> Dict[str, Any]:
        entries = self.iter_library_entries(
            saved_detail_cache=saved_detail_cache,
            site_key=site_key,
            default_site_display_name=current_site_display_name,
        )
        if keyword:
            keyword_lower = keyword.lower()
            entries = [
                e for e in entries
                if keyword_lower in str(e.get("manga_title", "")).lower()
            ]

        total = len(entries)
        total_pages = max((total + page_size - 1) // page_size, 1)
        page = max(1, min(page, total_pages))
        start = (page - 1) * page_size
        page_entries = entries[start : start + page_size]

        cards = []
        for entry in page_entries:
            cards.append({
                "manga_title": entry.get("manga_title", ""),
                "manga_url": entry.get("manga_url", ""),
                "cover_url": entry.get("cover_url", ""),
                "site_key": entry.get("site_key", ""),
                "site_name": entry.get("site_name", ""),
                "root_dir": entry.get("root_dir", ""),
                "total_chapters": entry.get("total_chapters", 0),
                "downloaded_chapter_count": entry.get("downloaded_chapter_count", 0),
                "last_downloaded_chapter_title": entry.get("last_downloaded_chapter_title", ""),
                "completed": entry.get("completed", False),
                "saved_at": entry.get("saved_at", ""),
            })

        return {
            "cards": cards,
            "page": page,
            "page_size": page_size,
            "total": total,
            "total_pages": total_pages,
        }

    def build_local_manga_detail(
        self,
        *,
        adapter: Any,
        source_url: str,
        saved_detail_cache: Optional[Dict[str, Any]] = None,
        resume_state: Optional[Dict[str, Any]] = None,
        default_site_display_name: str = "",
        root_dir_finder: Optional[Callable[[Any, str], Optional[str]]] = None,
    ) -> Optional[Any]:
        entry = self.find_local_library_entry_by_source_url(
            adapter=adapter,
            source_url=source_url,
            saved_detail_cache=saved_detail_cache,
            default_site_display_name=default_site_display_name,
            root_dir_finder=root_dir_finder,
        )
        if entry is None:
            return None

        from backend.support.site_adapters import MangaDetail

        title = entry.get("manga_title", "") or "本地漫画"
        root_dir = entry.get("root_dir", "")
        downloaded_count = int(entry.get("downloaded_chapter_count") or 0)
        total = int(entry.get("total_chapters") or downloaded_count)
        last_title = entry.get("last_downloaded_chapter_title", "")
        cover_url = entry.get("cover_url", "")
        if not cover_url:
            from backend.support.local_library import find_local_library_cover_path
            cover_url = find_local_library_cover_path(root_dir)

        completed = bool(entry.get("completed"))
        if completed:
            status_text = f"已下载完成（{downloaded_count} 章）"
        else:
            status_text = f"已下载 {downloaded_count} 章"

        detail = MangaDetail(
            title=title,
            manga_url=source_url or entry.get("manga_url", ""),
            section="",
            cover_url=cover_url,
            latest_chapter=last_title,
            update_time="",
            detail_hint=status_text,
            detail_section_label="本地下载记录",
            chapter_count=total,
            start_chapter_title="",
        )
        return detail
