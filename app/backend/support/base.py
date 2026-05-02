"""站点适配器基类、公共数据类型与工具函数。"""
from __future__ import annotations

import time
from dataclasses import asdict, dataclass
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import urljoin, urlparse

import requests
from bs4 import BeautifulSoup

from .downcomic import (
    OperationCancelledError,
    proxy_pool,
    unwrap_cover_url,
)


@dataclass(frozen=True)
class BaseSiteAdapter:
    key: str
    display_name: str
    supported_domains: Tuple[str, ...]
    supports_discovery: bool = False
    supports_search: bool = False
    supports_download: bool = False
    discovery_sections: Optional[Dict[str, str]] = None
    discovery_placeholder: str = "暂不支持"
    status_hint: str = ""

    def matches_url(self, url: str) -> bool:
        host = urlparse((url or "").strip()).netloc.lower()
        if not host:
            return False
        return any(host == domain or host.endswith(f".{domain}") for domain in self.supported_domains)

    def get_section_options(self) -> Dict[str, str]:
        return dict(self.discovery_sections or {})

    def is_single_page_section(self, section: str) -> bool:
        return False

    def get_theme_options(self) -> Dict[str, str]:
        return {}

    def supports_theme_filter(self, section: str = "") -> bool:
        return False

    def fetch_section_cards(self, section: str, page: int = 1, theme: str = "") -> List:
        raise NotImplementedError(f"{self.display_name} 暂未实现分区浏览")

    def fetch_search_cards(self, keyword: str, page: int = 1) -> List:
        raise NotImplementedError(f"{self.display_name} 暂未实现站内搜索")

    def get_manga_info_from_url(self, url: str):
        raise NotImplementedError(f"{self.display_name} 暂未实现 URL 解析")

    def get_all_chapters(self, manga_id):
        raise NotImplementedError(f"{self.display_name} 暂未实现章节获取")

    def build_chapter_url_template(self, manga_slug: str) -> str:
        raise NotImplementedError(f"{self.display_name} 暂未实现章节 URL 模板")

    def download_chapter_images(
        self,
        chapter_slug,
        base_url_template,
        root_dir,
        max_concurrent_images=5,
        stop_event=None,
        show_progress=True,
    ):
        raise NotImplementedError(f"{self.display_name} 暂未实现章节下载")

    def adjust_download_settings(self, chapter_concurrency: int, image_concurrency: int) -> Tuple[int, int, str]:
        return chapter_concurrency, image_concurrency, ""

    def get_chapter_retry_limit(self) -> int:
        return 0

    def get_retry_delay_seconds(self, retry_count: int) -> float:
        return 0.0

    def should_retry_download_error(self, error: Exception) -> bool:
        return False

    def fetch_manga_detail(self, url: str):
        raise NotImplementedError(f"{self.display_name} 暂未实现漫画详情获取")

    def should_use_env_for_http(self) -> bool:
        return False

    def supports_manual_proxy(self) -> bool:
        return False

    def set_manual_proxy(self, proxy_url: str):
        return None

    def get_manual_proxy_url(self) -> str:
        return ""

    def has_manual_proxy(self) -> bool:
        return False

    def configure_requests_session(self, session: requests.Session, for_image: bool = False):
        if self.is_proxy_pool_enabled():
            session.trust_env = False
            session.proxies.clear()
            return
        session.trust_env = self.should_use_env_for_http()

    def supports_proxy_pool(self) -> bool:
        return True

    def is_proxy_pool_enabled(self) -> bool:
        return self.supports_proxy_pool() and bool(getattr(proxy_pool, "enabled", False))

    def ensure_proxy_pool_ready(self, stop_event=None):
        if self.is_proxy_pool_enabled() and not proxy_pool.proxies:
            proxy_pool.fetch_proxies(stop_event=stop_event)

    def request_with_session(
        self,
        session: requests.Session,
        method: str,
        url: str,
        *,
        use_proxy_pool: Optional[bool] = None,
        proxy_attempts: int = 2,
        proxy_retry_delay: float = 0.35,
        stop_event=None,
        **kwargs,
    ):
        if stop_event is not None and stop_event.is_set():
            raise OperationCancelledError("已停止连通性测试")
        use_proxy_pool = self.is_proxy_pool_enabled() if use_proxy_pool is None else bool(use_proxy_pool)
        if not use_proxy_pool:
            response = session.request(method, url, **kwargs)
            if stop_event is not None and stop_event.is_set():
                response.close()
                raise OperationCancelledError("已停止连通性测试")
            return response

        self.ensure_proxy_pool_ready(stop_event=stop_event)
        if not proxy_pool.proxies:
            raise RuntimeError("内置代理池当前没有可用节点，请稍后重试或改用手动代理。")

        available_proxy_count = len(proxy_pool.proxies)
        attempts = max(int(proxy_attempts or 1), 1)
        attempts = min(max(attempts, min(available_proxy_count, 8)), max(available_proxy_count, 1))
        last_error = None

        for attempt in range(attempts):
            if stop_event is not None and stop_event.is_set():
                raise OperationCancelledError("已停止连通性测试")
            proxy = proxy_pool.get_proxy()
            if not proxy:
                last_error = RuntimeError("内置代理池当前没有可用节点，请稍后重试或改用手动代理。")
                break
            request_kwargs = dict(kwargs)
            request_kwargs["proxies"] = proxy
            try:
                response = session.request(method, url, **request_kwargs)
                if stop_event is not None and stop_event.is_set():
                    response.close()
                    raise OperationCancelledError("已停止连通性测试")
                return response
            except Exception as exc:
                last_error = exc
                proxy_pool.remove_proxy(proxy)
                if attempt < attempts - 1:
                    if stop_event is not None and stop_event.is_set():
                        raise OperationCancelledError("已停止连通性测试")
                    time.sleep(proxy_retry_delay * (attempt + 1))

        if last_error is not None:
            raise last_error

        raise RuntimeError("内置代理池请求失败，未拿到可用代理节点。")

    def probe_connection(self, target_url: str, stop_event=None) -> Tuple[int, str]:
        session = requests.Session()
        self.configure_requests_session(session, for_image=False)
        headers = {
            "Referer": f"https://{self.supported_domains[0]}/",
            "Cache-Control": "no-cache",
            "Pragma": "no-cache",
        }
        with self.request_with_session(
            session,
            "GET",
            target_url,
            headers=headers,
            timeout=(8, 12),
            allow_redirects=True,
            stream=True,
            proxy_attempts=3,
            stop_event=stop_event,
        ) as response:
            return response.status_code, response.url

    def get_manga_cache_key(self, url: str) -> str:
        normalized = (url or "").strip()
        return f"{self.key}:{normalized}"


@dataclass
class MangaDetail:
    title: str
    manga_url: str
    section: str
    cover_url: str = ""
    latest_chapter: str = ""
    update_time: str = "-"
    detail_hint: str = ""
    detail_section_label: str = ""
    chapter_count: int = 0
    start_chapter_title: str = ""

    def to_cache_dict(self) -> Dict:
        return asdict(self)


def coerce_html_attr_to_str(value: Any) -> str:
    if isinstance(value, str):
        return value
    if isinstance(value, (list, tuple)):
        for item in value:
            if isinstance(item, str) and item.strip():
                return item
        return ""
    return "" if value is None else str(value)


def resolve_media_url(base_url: str, raw_url: Any) -> str:
    candidate = unwrap_cover_url(coerce_html_attr_to_str(raw_url).strip())
    if not candidate:
        return ""
    if candidate.startswith("//"):
        return f"https:{candidate}"
    return urljoin(base_url, candidate)


def extract_cover_url_from_html(html: str | bytes, base_url: str) -> str:
    soup = BeautifulSoup(html or "", "html.parser")
    candidates = [
        ('meta[property="og:image"]', "content"),
        ('meta[name="twitter:image"]', "content"),
        ('meta[itemprop="image"]', "content"),
        ('link[rel="image_src"]', "href"),
        ('.comicParticulars-left-img img', "data-src"),
        ('.comicParticulars-left-img img', "src"),
        ('.comicParticulars-title-left img', "data-src"),
        ('.comicParticulars-title-left img', "src"),
        ('.book-cover img', "src"),
        ('.comic-cover img', "src"),
        ('.manga-cover img', "src"),
        ('.detail-main img', "src"),
        ('img[class*="cover"]', "src"),
        ('img[data-src]', "data-src"),
        ('img[src]', "src"),
    ]

    for selector, attr in candidates:
        node = soup.select_one(selector)
        if not node:
            continue
        value = node.get(attr) or node.get("data-src") or node.get("src")
        resolved = resolve_media_url(base_url, value)
        if resolved:
            return resolved
    return ""


def extract_cover_url_from_data(data, base_url: str = "") -> str:
    priority_keys = ("cover", "cover_url", "comic_cover", "img", "image", "pic", "poster")

    if isinstance(data, dict):
        for key in priority_keys:
            value = data.get(key)
            if isinstance(value, str) and value.strip():
                resolved = resolve_media_url(base_url, value)
                if resolved:
                    return resolved

        for value in data.values():
            resolved = extract_cover_url_from_data(value, base_url=base_url)
            if resolved:
                return resolved

    if isinstance(data, list):
        for value in data:
            resolved = extract_cover_url_from_data(value, base_url=base_url)
            if resolved:
                return resolved

    return ""


def find_start_chapter_title(chapters: List[Dict], start_slug: Optional[str]) -> str:
    if not start_slug:
        return ""
    matched = next(
        (
            chapter for chapter in chapters
            if chapter.get("slug") == start_slug or chapter.get("uuid") == start_slug
        ),
        None,
    )
    if not matched:
        return ""
    return matched.get("title") or matched.get("slug") or ""
