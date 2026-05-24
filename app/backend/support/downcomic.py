"""下载漫画的主入口（保持向后兼容）。

此文件已重构为多个模块：
- proxy_pool.py: 代理池管理
- http_helpers.py: HTTP 请求工具
- file_utils.py: 文件名和 URL 工具
- homepage_scraper.py: 首页卡片抓取
- image_downloader.py: 图片下载
- manga_info.py: 漫画信息获取

此文件保留所有原有导出，确保现有代码无需修改。
"""
from __future__ import annotations

# 从新模块导入所有内容，保持向后兼容
from .proxy_pool import ProxyPool, proxy_pool
from .http_helpers import (
    OperationCancelledError,
    USER_AGENTS,
    _safe_print,
    print,
    print_lock,
    should_stop,
    get_session,
    safe_request,
    _api_fetch_json,
)
from .file_utils import (
    sanitize_filename,
    build_absolute_url,
    normalize_chapterlist_url,
    unwrap_cover_url,
    coerce_html_attr_to_str,
    BASE_SITE_URL,
)
from .homepage_scraper import (
    HomepageMangaCard,
    _extract_standard_card_section,
    _extract_recent_update_section,
    fetch_homepage_manga_cards,
    fetch_section_manga_cards,
    fetch_search_manga_cards,
    filter_homepage_cards,
    print_homepage_cards,
    homepage_cards_to_dict,
)
from .image_downloader import (
    download_single_image,
    download_chapter_images,
)
from .manga_info import (
    get_manga_info_from_url,
    get_all_chapters,
)

# BASE_SITE_URL 已从 file_utils 导入

__all__ = [
    # ProxyPool
    "ProxyPool",
    "proxy_pool",
    # HTTP helpers
    "OperationCancelledError",
    "USER_AGENTS",
    "_safe_print",
    "print",
    "print_lock",
    "should_stop",
    "get_session",
    "safe_request",
    "_api_fetch_json",
    # File utils
    "sanitize_filename",
    "build_absolute_url",
    "normalize_chapterlist_url",
    "unwrap_cover_url",
    "coerce_html_attr_to_str",
    # Homepage scraper
    "HomepageMangaCard",
    "_extract_standard_card_section",
    "_extract_recent_update_section",
    "fetch_homepage_manga_cards",
    "fetch_section_manga_cards",
    "fetch_search_manga_cards",
    "filter_homepage_cards",
    "print_homepage_cards",
    "homepage_cards_to_dict",
    # Image downloader
    "download_single_image",
    "download_chapter_images",
    # Manga info
    "get_manga_info_from_url",
    "get_all_chapters",
    # Constants
    "BASE_SITE_URL",
]
