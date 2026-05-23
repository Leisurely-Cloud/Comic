"""包子漫画适配器。"""
from __future__ import annotations

from typing import List, Tuple

from .downcomic import (
    download_chapter_images as baozimh_download_chapter_images,
    fetch_search_manga_cards as baozimh_fetch_search_manga_cards,
    fetch_section_manga_cards as baozimh_fetch_section_manga_cards,
    get_all_chapters as baozimh_get_all_chapters,
    get_manga_info_from_url as baozimh_get_manga_info_from_url,
    safe_request as baozimh_safe_request,
)

from .base import (
    BaseSiteAdapter,
    MangaDetail,
    extract_cover_url_from_html,
    find_start_chapter_title,
)


class BaozimhAdapter(BaseSiteAdapter):
    def __init__(self):
        super().__init__(
            key="baozimh",
            display_name="包子漫画",
            supported_domains=("baozimh.org",),
            supports_discovery=True,
            supports_search=True,
            supports_download=True,
            discovery_sections={
                "人气排行": "rank",
                "近期更新": "recent",
                "热门更新": "hot-update",
                "最新上架": "new",
            },
            status_hint="已启用首页浏览、搜索和下载。",
        )

    def fetch_section_cards(self, section: str, page: int = 1, theme: str = "") -> List:
        return baozimh_fetch_section_manga_cards(section, page=page)

    def fetch_search_cards(self, keyword: str, page: int = 1) -> List:
        return baozimh_fetch_search_manga_cards(keyword, page=page)

    def get_manga_info_from_url(self, url: str, stop_event=None):
        return baozimh_get_manga_info_from_url(url, stop_event=stop_event)

    def get_all_chapters(self, manga_id):
        return baozimh_get_all_chapters(manga_id)

    def get_manga_cache_key(self, url: str) -> str:
        _, manga_slug, _ = self.get_manga_info_from_url(url)
        if manga_slug:
            return f"{self.key}:{manga_slug}"
        return super().get_manga_cache_key(url)

    def fetch_manga_detail(self, url: str):
        manga_id, manga_slug, start_slug = self.get_manga_info_from_url(url)
        if not manga_id or not manga_slug:
            raise RuntimeError(f"{self.display_name} 无法识别该漫画链接")

        manga_title, chapters = self.get_all_chapters(manga_id)
        detail_url = f"https://{self.supported_domains[0]}/chapterlist/{manga_slug}"
        cover_url = ""

        try:
            response = baozimh_safe_request(detail_url, retries=1)
            if response is not None:
                cover_url = extract_cover_url_from_html(response.content, detail_url)
        except Exception:
            cover_url = ""

        latest = chapters[-1] if chapters else {}
        start_chapter_title = find_start_chapter_title(chapters, start_slug)
        chapter_count = len(chapters)
        detail_parts = [f"共 {chapter_count} 章"] if chapter_count else ["未解析到章节列表"]
        if start_chapter_title:
            detail_parts.append(f"当前链接定位到 {start_chapter_title}")

        return MangaDetail(
            title=manga_title or manga_slug,
            manga_url=(url or "").strip(),
            section="手动链接",
            cover_url=cover_url,
            latest_chapter=latest.get("title") or "-",
            update_time=latest.get("updated_at") or "-",
            detail_hint="，".join(detail_parts),
            detail_section_label=f"站点: {self.display_name}",
            chapter_count=chapter_count,
            start_chapter_title=start_chapter_title,
        )

    def build_chapter_url_template(self, manga_slug: str) -> str:
        return f"https://baozimh.org/manga/{manga_slug}/{{slug}}"

    def download_chapter_images(
        self,
        chapter_slug,
        base_url_template,
        root_dir,
        max_concurrent_images=5,
        stop_event=None,
        show_progress=True,
    ):
        return baozimh_download_chapter_images(
            chapter_slug,
            base_url_template,
            root_dir,
            max_concurrent_images=max_concurrent_images,
            stop_event=stop_event,
            show_progress=show_progress,
        )
