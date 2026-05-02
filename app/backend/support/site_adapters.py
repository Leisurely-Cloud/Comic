"""站点适配器注册表。

适配器实现在同目录下的 baozimh.py / mangacopy.py / manhuagui.py，
共享基础在 base.py。
"""
from __future__ import annotations

from typing import Dict, List, Optional

from .base import (
    BaseSiteAdapter,
    MangaDetail,
    coerce_html_attr_to_str,
    extract_cover_url_from_data,
    extract_cover_url_from_html,
    find_start_chapter_title,
    resolve_media_url,
)
from .baozimh import BaozimhAdapter
from .mangacopy import MangaCopyAdapter
from .manhuagui import ManhuaguiAdapter

SITE_ADAPTERS: Dict[str, BaseSiteAdapter] = {
    "baozimh": BaozimhAdapter(),
    "mangacopy": MangaCopyAdapter(),
    "manhuagui": ManhuaguiAdapter(),
}

DEFAULT_SITE_KEY = "baozimh"


def get_adapter(site_key: str) -> BaseSiteAdapter:
    return SITE_ADAPTERS.get(site_key, SITE_ADAPTERS[DEFAULT_SITE_KEY])


def get_adapter_by_display_name(display_name: str) -> BaseSiteAdapter:
    for adapter in SITE_ADAPTERS.values():
        if adapter.display_name == display_name:
            return adapter
    return SITE_ADAPTERS[DEFAULT_SITE_KEY]


def get_site_display_names() -> List[str]:
    return [adapter.display_name for adapter in SITE_ADAPTERS.values()]


def resolve_adapter_from_url(url: str, fallback_key: Optional[str] = None) -> BaseSiteAdapter:
    for adapter in SITE_ADAPTERS.values():
        if adapter.matches_url(url):
            return adapter
    if fallback_key:
        return get_adapter(fallback_key)
    return SITE_ADAPTERS[DEFAULT_SITE_KEY]
