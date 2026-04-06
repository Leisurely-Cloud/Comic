from __future__ import annotations

import ast
import html as html_lib
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
import threading
from dataclasses import asdict, dataclass
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import quote, unquote, urljoin, urlparse

import requests
from bs4 import BeautifulSoup
from concurrent.futures import ThreadPoolExecutor, as_completed
from tqdm import tqdm

from downcomic import (
    HomepageMangaCard,
    download_chapter_images as baozimh_download_chapter_images,
    fetch_search_manga_cards as baozimh_fetch_search_manga_cards,
    fetch_section_manga_cards as baozimh_fetch_section_manga_cards,
    get_all_chapters as baozimh_get_all_chapters,
    get_manga_info_from_url as baozimh_get_manga_info_from_url,
    print_lock,
    safe_request as baozimh_safe_request,
    sanitize_filename,
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
        session.trust_env = self.should_use_env_for_http()

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


def extract_cover_url_from_html(html: str, base_url: str) -> str:
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

    def get_manga_info_from_url(self, url: str):
        return baozimh_get_manga_info_from_url(url)

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
                response.encoding = "utf-8"
                cover_url = extract_cover_url_from_html(response.text, detail_url)
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


class MangaCopyAdapter(BaseSiteAdapter):
    def __init__(self):
        super().__init__(
            key="mangacopy",
            display_name="拷贝漫画",
            supported_domains=("mangacopy.com",),
            supports_discovery=True,
            supports_search=True,
            supports_download=True,
            discovery_sections={
                "编辑推荐": "recommend",
                "全新上架": "newest",
                "发现更新": "discover-latest",
                "发现热门": "discover-popular",
                "男频日榜": "rank-day-male",
                "女频日榜": "rank-day-female",
                "男频周榜": "rank-week-male",
                "男频月榜": "rank-month-male",
                "男频总榜": "rank-total-male",
            },
            status_hint="已启用编辑推荐、全新上架、发现列表、排行榜、站内搜索和手动 URL 下载。",
        )
        self._comic_cache: Dict[str, Dict[str, str]] = {}
        self._manual_proxy_url = ""
        self._manual_proxy_dict = None
        self._session = self._build_session()

    def _build_session(self) -> requests.Session:
        session = requests.Session()
        session.trust_env = False
        session.headers.update({
            "User-Agent": (
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/137.0.0.0 Safari/537.36"
            ),
            "Accept": "application/json, text/plain, */*",
        })
        if self._manual_proxy_dict:
            session.proxies.update(self._manual_proxy_dict)
        return session

    def _normalize_manual_proxy(self, proxy_url: str) -> Tuple[str, Optional[Dict[str, str]]]:
        normalized = (proxy_url or "").strip()
        if not normalized:
            return "", None

        if "://" not in normalized:
            normalized = f"http://{normalized}"

        parsed = urlparse(normalized)
        if not parsed.scheme or not parsed.netloc:
            raise ValueError("代理地址格式不正确，请使用 host:port 或 http://host:port")

        supported_schemes = {"http", "https", "socks5", "socks5h"}
        if parsed.scheme.lower() not in supported_schemes:
            raise ValueError("当前仅支持 http/https/socks5/socks5h 代理地址")

        proxy_dict = {
            "http": normalized,
            "https": normalized,
        }
        return normalized, proxy_dict

    def supports_manual_proxy(self) -> bool:
        return True

    def set_manual_proxy(self, proxy_url: str):
        normalized, proxy_dict = self._normalize_manual_proxy(proxy_url)
        self._manual_proxy_url = normalized
        self._manual_proxy_dict = proxy_dict
        self._session = self._build_session()

    def get_manual_proxy_url(self) -> str:
        return self._manual_proxy_url

    def has_manual_proxy(self) -> bool:
        return bool(self._manual_proxy_dict)

    def configure_requests_session(self, session: requests.Session, for_image: bool = False):
        session.trust_env = False
        session.headers.update({
            "User-Agent": (
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/137.0.0.0 Safari/537.36"
            ),
            "Accept": "application/json, text/plain, */*",
        })
        session.proxies.clear()
        if self._manual_proxy_dict:
            session.proxies.update(self._manual_proxy_dict)

    def _get_page_host(self, manga_path_word: Optional[str] = None) -> str:
        if manga_path_word:
            cached = self._comic_cache.get(manga_path_word, {})
            if cached.get("page_host"):
                return cached["page_host"]
        return self.supported_domains[0]

    def _build_api_hosts(self, page_host: str) -> List[str]:
        normalized_host = (page_host or self.supported_domains[0]).lower()
        if normalized_host.startswith("www."):
            normalized_host = normalized_host[4:]

        candidates = [
            f"api.{normalized_host}",
            "api.mangacopy.com",
            "api.copymanga.org",
        ]

        deduped = []
        for host in candidates:
            if host not in deduped:
                deduped.append(host)
        return deduped

    def _request_json(self, path: str, manga_path_word: Optional[str] = None, referer: Optional[str] = None):
        page_host = self._get_page_host(manga_path_word)
        last_error = None

        for api_host in self._build_api_hosts(page_host):
            url = f"https://{api_host}{path}"
            headers = {}
            if referer:
                headers["Referer"] = referer

            for attempt in range(2):
                try:
                    response = self._session.get(url, headers=headers, timeout=20)
                    response.raise_for_status()
                    payload = response.json()
                    if payload.get("code") == 200:
                        return payload, api_host

                    message = payload.get("message") or payload.get("results", {}).get("detail") or "请求失败"
                    last_error = RuntimeError(f"{api_host} 返回错误: {message}")
                    if payload.get("code") == 210:
                        raise RuntimeError(
                            f"{api_host} 暂时拒绝当前网络环境访问: {message}"
                        )
                except Exception as exc:
                    if isinstance(exc, RuntimeError) and "暂时拒绝当前网络环境访问" in str(exc):
                        raise exc
                    last_error = exc
                    time.sleep(0.5)
                    continue

        raise RuntimeError(f"MangaCopy API 请求失败: {last_error}")

    def _build_detail_url(self, manga_path_word: str) -> str:
        page_host = self._get_page_host(manga_path_word)
        return f"https://{page_host}/comic/{manga_path_word}"

    def _build_site_url(self, path: str) -> str:
        normalized_path = path if str(path).startswith("/") else f"/{path}"
        return f"https://{self._get_page_host()}{normalized_path}"

    def _request_html_page(self, url: str, referer: Optional[str] = None) -> Tuple[str, str]:
        response = self._session.get(
            url,
            headers={
                "Referer": referer or f"https://{self._get_page_host()}/",
                "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            },
            timeout=20,
        )
        response.raise_for_status()
        response.encoding = response.apparent_encoding or response.encoding or "utf-8"
        return response.text, (response.url or url)

    def _request_detail_page_html(self, manga_path_word: str) -> Tuple[str, str]:
        detail_url = self._build_detail_url(manga_path_word)
        html, final_url = self._request_html_page(
            detail_url,
            referer=f"https://{self._get_page_host(manga_path_word)}/",
        )
        final_host = urlparse(final_url).netloc
        if final_host:
            self._comic_cache.setdefault(manga_path_word, {})["page_host"] = final_host
        return html, final_url

    def _extract_detail_page_title(self, html: str, manga_id: str) -> str:
        soup = BeautifulSoup(html or "", "html.parser")

        title_node = soup.select_one(".comicParticulars-title-right h6") or soup.find("h1")
        if title_node is not None:
            title = title_node.get_text(" ", strip=True)
            if title:
                return title

        title_tag = soup.find("title")
        if title_tag is not None:
            title = title_tag.get_text(" ", strip=True)
            if title:
                title = re.sub(r"\s*-\s*拷[貝贝]漫畫.*$", "", title)
                title = re.sub(r"漫畫.*$", "", title)
                title = title.strip(" -")
                if title:
                    return title

        return manga_id

    def _fetch_detail_page_snapshot(self, manga_id: str) -> Tuple[str, str, str]:
        html, final_url = self._request_detail_page_html(manga_id)
        title = self._extract_detail_page_title(html, manga_id)
        cover_url = extract_cover_url_from_html(html, final_url)
        return title, cover_url, final_url

    def _extract_group_path_word(self, groups) -> str:
        group_items = list(groups.values()) if isinstance(groups, dict) else list(groups or [])
        if group_items:
            default_group = next((item for item in group_items if item.get("path_word") == "default"), group_items[0])
            return default_group.get("path_word") or "default"
        return "default"

    def _fetch_comic_overview(self, manga_id: str) -> Tuple[str, Dict[str, Any], str, str]:
        referer = self._build_detail_url(manga_id)
        detail_payload, api_host = self._request_json(
            f"/api/v3/comic2/{manga_id}?platform=1&_update=true",
            manga_path_word=manga_id,
            referer=referer,
        )
        results = detail_payload.get("results", {})
        comic = results.get("comic") or {}
        group_path_word = self._extract_group_path_word(results.get("groups") or {})
        manga_title = comic.get("name") or results.get("name") or manga_id

        self._comic_cache.setdefault(manga_id, {}).update({
            "api_host": api_host,
            "group_path_word": group_path_word,
            "title": manga_title,
        })
        return referer, comic, manga_title, group_path_word

    def _parse_chapter_list(self, chapter_list, manga_id: str) -> List[Dict]:
        chapters = []
        for fallback_order, chapter in enumerate(chapter_list or []):
            chapters.append({
                "slug": chapter.get("uuid"),
                "uuid": chapter.get("uuid"),
                "order": chapter.get("index", fallback_order),
                "title": chapter.get("name") or chapter.get("title") or chapter.get("uuid"),
                "updated_at": chapter.get("datetime_updated") or chapter.get("updated_at"),
                "comic_path_word": chapter.get("comic_path_word") or manga_id,
            })

        chapters.sort(key=lambda item: item.get("order", 0))
        return chapters

    def _fetch_chapter_list(self, manga_id: str, group_path_word: str, referer: str) -> List[Dict]:
        chapter_payload, _ = self._request_json(
            f"/api/v3/comic/{manga_id}/group/{group_path_word}/chapters?limit=500&offset=0&platform=1",
            manga_path_word=manga_id,
            referer=referer,
        )
        chapter_list = chapter_payload.get("results", {}).get("list") or []
        return self._parse_chapter_list(chapter_list, manga_id)

    def _format_popularity(self, value: Any) -> str:
        try:
            numeric = float(value)
        except (TypeError, ValueError):
            return str(value or "")
        if numeric >= 100000000:
            return f"{numeric / 100000000:.1f}亿".rstrip("0").rstrip(".")
        if numeric >= 10000:
            return f"{numeric / 10000:.1f}W".rstrip("0").rstrip(".")
        if numeric.is_integer():
            return str(int(numeric))
        return f"{numeric:.1f}".rstrip("0").rstrip(".")

    def _join_author_names_from_data(self, authors: Any) -> str:
        names: List[str] = []
        if isinstance(authors, list):
            for author in authors:
                if isinstance(author, dict):
                    name = str(author.get("name") or author.get("alias") or "").strip()
                else:
                    name = str(author or "").strip()
                if name and name not in names:
                    names.append(name)
        return " / ".join(names)

    def _join_author_names_from_html(self, item: BeautifulSoup) -> str:
        names: List[str] = []
        for node in item.select(".exemptComicItem-txt-span a, .oneLines a"):
            name = node.get_text(" ", strip=True)
            if name and name not in names:
                names.append(name)

        if names:
            return " / ".join(names)

        author_text = ""
        author_node = item.select_one(".exemptComicItem-txt-span") or item.select_one(".oneLines")
        if author_node is not None:
            author_text = author_node.get_text(" ", strip=True)
        author_text = re.sub(r"^作者[:：]\s*", "", author_text).strip()
        return author_text

    def _build_search_cards_from_payload(self, items, keyword: str) -> List[HomepageMangaCard]:
        cards: List[HomepageMangaCard] = []
        for item in items or []:
            manga_path_word = str(item.get("path_word") or "").strip()
            title = str(item.get("name") or item.get("alias") or manga_path_word).strip()
            if not manga_path_word or not title:
                continue

            manga_url = self._build_detail_url(manga_path_word)
            cover_url = resolve_media_url(manga_url, item.get("cover") or "")
            author_text = self._join_author_names_from_data(item.get("author") or [])
            popularity_text = self._format_popularity(item.get("popular"))

            card = HomepageMangaCard(
                section="搜索结果",
                title=title,
                manga_url=manga_url,
                chapterlist_url=manga_url,
                cover_url=cover_url,
                latest_chapter="-",
                update_time="-",
            )

            detail_parts = []
            if author_text:
                detail_parts.append(f"作者: {author_text}")
            if popularity_text:
                detail_parts.append(f"热度: {popularity_text}")
            setattr(card, "detail_hint", "，".join(detail_parts))
            setattr(card, "detail_section_label", f"搜索: {keyword}")
            cards.append(card)

        return cards

    def get_theme_options(self) -> Dict[str, str]:
        return {
            "全部题材": "",
            "爱情": "aiqing",
            "欢乐向": "huanlexiang",
            "冒险": "maoxian",
            "奇幻": "qihuan",
            "百合": "baihe",
            "校园": "xiaoyuan",
            "科幻": "kehuan",
            "东方": "dongfang",
            "耽美": "danmei",
            "生活": "shenghuo",
            "格斗": "gedou",
            "轻小说": "qingxiaoshuo",
            "其他": "qita",
            "悬疑": "xuanyi",
            "TL": "teenslove",
            "萌系": "mengxi",
            "神鬼": "shengui",
            "职场": "zhichang",
            "治愈": "zhiyu",
            "节操": "jiecao",
            "四格": "sige",
            "长条": "changtiao",
            "舰娘": "jianniang",
            "搞笑": "gaoxiao",
            "竞技": "jingji",
            "伪娘": "weiniang",
            "魔幻": "mohuan",
            "热血": "rexue",
            "性转换": "xingzhuanhuan",
            "美食": "meishi",
            "励志": "lizhi",
            "彩色": "COLOR",
            "后宫": "hougong",
            "侦探": "zhentan",
            "惊悚": "jingsong",
            "AA": "aa",
            "音乐舞蹈": "yinyuewudao",
            "异世界": "yishijie",
            "战争": "zhanzheng",
            "历史": "lishi",
            "机战": "jizhan",
            "都市": "dushi",
            "穿越": "chuanyue",
            "C102": "comiket102",
            "重生": "chongsheng",
            "恐怖": "kongbu",
            "C103": "comiket103",
            "生存": "shengcun",
            "C100": "comiket100",
            "C104": "comiket104",
            "C101": "comiket101",
            "C99": "comiket99",
            "C97": "comiket97",
            "武侠": "wuxia",
            "宅系": "zhaixi",
            "C96": "comiket96",
            "C105": "comiket105",
            "C98": "C98",
            "C95": "comiket95",
            "转生": "zhuansheng",
            "FATE": "fate",
            "无修正": "Uncensored",
            "仙侠": "xianxia",
            "LoveLive": "loveLive",
            "杂志附赠写真集": "zazhifuzengxiezhenji",
        }

    def supports_theme_filter(self, section: str = "") -> bool:
        return str(section or "").startswith("discover-")

    def _get_theme_display_name(self, theme: str) -> str:
        for label, value in self.get_theme_options().items():
            if value == theme:
                return label
        return ""

    def _build_discovery_page_url(self, section: str, page: int, theme: str = "") -> Tuple[str, str, str]:
        page = max(int(page or 1), 1)
        section_map = {
            "recommend": ("/recommend", "编辑推荐", 60, "html-grid"),
            "newest": ("/newest", "全新上架", 60, "html-grid"),
            "discover-latest": ("/comics?ordering=-datetime_updated", "发现更新", 50, "comics-feed"),
            "discover-popular": ("/comics?ordering=-popular", "发现热门", 50, "comics-feed"),
            "rank-day-male": ("/rank?type=male&table=day", "男频日榜", 0, "rank"),
            "rank-day-female": ("/rank?type=female&table=day", "女频日榜", 0, "rank"),
            "rank-week-male": ("/rank?type=male&table=week", "男频周榜", 0, "rank"),
            "rank-month-male": ("/rank?type=male&table=month", "男频月榜", 0, "rank"),
            "rank-total-male": ("/rank?type=male&table=total", "男频总榜", 0, "rank"),
        }
        path, section_label, page_size, parser_mode = section_map.get(
            section,
            ("/recommend", "编辑推荐", 60, "html-grid"),
        )
        if parser_mode == "rank":
            page_url = self._build_site_url(path)
        else:
            offset = (page - 1) * page_size
            base_path = path
            if parser_mode == "comics-feed" and theme:
                base_path = f"{base_path}&theme={quote(theme, safe='')}"
            separator = "&" if "?" in path else "?"
            page_url = self._build_site_url(f"{base_path}{separator}offset={offset}&limit={page_size}")
        return page_url, section_label, parser_mode

    def _parse_discovery_cards_from_html(self, html: str, page_url: str, section_label: str) -> List[HomepageMangaCard]:
        soup = BeautifulSoup(html or "", "html.parser")
        cards: List[HomepageMangaCard] = []

        for item in soup.select(".exemptComic_Item"):
            link = item.select_one(".exemptComic_Item-img a[href]") or item.select_one(".exemptComicItem-txt a[href]")
            title_node = item.select_one("p[title]") or item.select_one("p")
            cover_node = item.select_one("img")
            if link is None or title_node is None:
                continue

            href = coerce_html_attr_to_str(link.get("href", "")).strip()
            title = title_node.get_text(" ", strip=True)
            if not href or not title:
                continue

            manga_url = urljoin(page_url, href)
            cover_url = resolve_media_url(
                page_url,
                coerce_html_attr_to_str(cover_node.get("data-src") if cover_node is not None else "")
                or coerce_html_attr_to_str(cover_node.get("src") if cover_node is not None else ""),
            )
            author_text = self._join_author_names_from_html(item)

            card = HomepageMangaCard(
                section=section_label,
                title=title,
                manga_url=manga_url,
                chapterlist_url=manga_url,
                cover_url=cover_url,
                latest_chapter="-",
                update_time="-",
            )

            if author_text:
                setattr(card, "detail_hint", f"作者: {author_text}")
            setattr(card, "detail_section_label", f"分区: {section_label}")
            cards.append(card)

        return cards

    def _parse_rank_cards_from_html(self, html: str, page_url: str, section_label: str) -> List[HomepageMangaCard]:
        soup = BeautifulSoup(html or "", "html.parser")
        cards: List[HomepageMangaCard] = []

        for item in soup.select(".ranking-all.row > li"):
            link = item.select_one(".ranking-all-topThree > a[href]") or item.select_one(".ranking-all-topThree-txt > a[href]")
            title_node = item.select_one("p[title]") or item.select_one("p")
            cover_node = item.select_one("img")
            rank_node = item.select_one(".ranking-all-icon")
            heat_node = item.select_one(".update > span")
            if link is None or title_node is None:
                continue

            href = coerce_html_attr_to_str(link.get("href", "")).strip()
            title = title_node.get_text(" ", strip=True)
            if not href or not title:
                continue

            manga_url = urljoin(page_url, href)
            cover_url = resolve_media_url(
                page_url,
                coerce_html_attr_to_str(cover_node.get("data-src") if cover_node is not None else "")
                or coerce_html_attr_to_str(cover_node.get("src") if cover_node is not None else ""),
            )
            author_text = self._join_author_names_from_html(item)
            heat_text = heat_node.get_text(" ", strip=True).replace("\xa0", " ") if heat_node is not None else ""
            rank_text = rank_node.get_text(" ", strip=True) if rank_node is not None else ""

            card = HomepageMangaCard(
                section=section_label,
                title=title,
                manga_url=manga_url,
                chapterlist_url=manga_url,
                cover_url=cover_url,
                latest_chapter="-",
                update_time="-",
            )

            detail_parts = []
            if author_text:
                detail_parts.append(f"作者: {author_text}")
            if heat_text:
                detail_parts.append(f"热度: {heat_text}")
            setattr(card, "detail_hint", "，".join(detail_parts))
            if rank_text:
                setattr(card, "detail_section_label", f"{section_label} · 第 {rank_text} 名")
            else:
                setattr(card, "detail_section_label", f"分区: {section_label}")
            cards.append(card)

        return cards

    def _extract_comics_feed_items_from_html(self, html: str) -> List[Dict[str, Any]]:
        soup = BeautifulSoup(html or "", "html.parser")
        container = soup.select_one(".exemptComic-box")
        raw_list = coerce_html_attr_to_str(container.get("list") if container is not None else "").strip()
        if not raw_list:
            return []

        try:
            data = ast.literal_eval(html_lib.unescape(raw_list))
        except Exception:
            return []

        return data if isinstance(data, list) else []

    def _build_comics_feed_cards_from_data(
        self,
        items: List[Dict[str, Any]],
        section_label: str,
    ) -> List[HomepageMangaCard]:
        status_map = {
            0: "连载中",
            1: "已完结",
            2: "短篇",
        }
        cards: List[HomepageMangaCard] = []

        for item in items:
            manga_path_word = str(item.get("path_word") or "").strip()
            title = str(item.get("name") or manga_path_word).strip()
            if not manga_path_word or not title:
                continue

            manga_url = self._build_detail_url(manga_path_word)
            cover_url = resolve_media_url(manga_url, item.get("cover") or "")
            author_text = self._join_author_names_from_data(item.get("author") or [])
            status_text = status_map.get(item.get("status"), "")

            card = HomepageMangaCard(
                section=section_label,
                title=title,
                manga_url=manga_url,
                chapterlist_url=manga_url,
                cover_url=cover_url,
                latest_chapter="-",
                update_time="-",
            )

            detail_parts = []
            if author_text:
                detail_parts.append(f"作者: {author_text}")
            if status_text:
                detail_parts.append(f"状态: {status_text}")
            setattr(card, "detail_hint", "，".join(detail_parts))
            setattr(card, "detail_section_label", f"分区: {section_label}")
            cards.append(card)

        return cards

    def is_single_page_section(self, section: str) -> bool:
        return str(section or "").startswith("rank-")

    def fetch_section_cards(self, section: str, page: int = 1, theme: str = "") -> List[HomepageMangaCard]:
        theme = (theme or "").strip()
        page_url, section_label, parser_mode = self._build_discovery_page_url(section, page, theme=theme)
        if parser_mode == "comics-feed" and theme:
            theme_label = self._get_theme_display_name(theme)
            if theme_label:
                section_label = f"{section_label} · {theme_label}"
        html, final_url = self._request_html_page(
            page_url,
            referer=self._build_site_url("/"),
        )
        if parser_mode == "rank":
            return self._parse_rank_cards_from_html(html, final_url, section_label)
        if parser_mode == "comics-feed":
            items = self._extract_comics_feed_items_from_html(html)
            return self._build_comics_feed_cards_from_data(items, section_label)
        return self._parse_discovery_cards_from_html(html, final_url, section_label)

    def fetch_search_cards(self, keyword: str, page: int = 1) -> List[HomepageMangaCard]:
        keyword = (keyword or "").strip()
        if not keyword:
            return []

        page = max(int(page or 1), 1)
        offset = (page - 1) * 12
        search_url = self._build_site_url(
            f"/api/kb/web/searchch/comics?offset={offset}&platform=2&limit=12&q={quote(keyword)}&q_type="
        )
        response = self._session.get(
            search_url,
            headers={
                "Referer": self._build_site_url(f"/search?q={quote(keyword)}"),
                "Accept": "application/json, text/plain, */*",
            },
            timeout=20,
        )
        response.raise_for_status()
        payload = response.json()
        if payload.get("code") != 200:
            message = payload.get("message") or payload.get("results", {}).get("detail") or "搜索请求失败"
            raise RuntimeError(f"{self.display_name} 搜索失败: {message}")

        items = payload.get("results", {}).get("list") or []
        return self._build_search_cards_from_payload(items, keyword)

    def _download_image(self, image_url: str, dest_path: str, referer: str, stop_event=None):
        if stop_event is not None and stop_event.is_set():
            return False
        if os.path.exists(dest_path) and os.path.getsize(dest_path) > 0:
            return True

        try:
            with self._session.get(
                image_url,
                headers={"Referer": referer, "Accept": "image/avif,image/webp,image/*,*/*;q=0.8"},
                timeout=30,
                stream=True,
            ) as response:
                response.raise_for_status()
                with open(dest_path, "wb") as file_obj:
                    for chunk in response.iter_content(8192):
                        if stop_event is not None and stop_event.is_set():
                            return False
                        if chunk:
                            file_obj.write(chunk)
            return True
        except Exception:
            try:
                if os.path.exists(dest_path):
                    os.remove(dest_path)
            except OSError:
                pass
            return False

    def get_manga_info_from_url(self, url: str):
        parsed = urlparse((url or "").strip())
        path_parts = [part for part in parsed.path.strip("/").split("/") if part]

        manga_path_word = None
        chapter_uuid = None

        if len(path_parts) >= 2 and path_parts[0] == "comic":
            manga_path_word = path_parts[1]
            if len(path_parts) >= 4 and path_parts[2] == "chapter":
                chapter_uuid = path_parts[3]

        if not manga_path_word:
            return None, None, None

        self._comic_cache.setdefault(manga_path_word, {})["page_host"] = parsed.netloc or self.supported_domains[0]
        return manga_path_word, manga_path_word, chapter_uuid

    def get_all_chapters(self, manga_id):
        referer, _, manga_title, group_path_word = self._fetch_comic_overview(manga_id)
        chapters = self._fetch_chapter_list(manga_id, group_path_word, referer)
        return manga_title, chapters

    def get_manga_cache_key(self, url: str) -> str:
        manga_id, _, _ = self.get_manga_info_from_url(url)
        if manga_id:
            return f"{self.key}:{manga_id}"
        return super().get_manga_cache_key(url)

    def fetch_manga_detail(self, url: str):
        manga_id, _, start_slug = self.get_manga_info_from_url(url)
        if not manga_id:
            raise RuntimeError(f"{self.display_name} 无法识别该漫画链接")

        detail_url = self._build_detail_url(manga_id)
        html_title = ""
        html_cover_url = ""
        try:
            html_title, html_cover_url, detail_url = self._fetch_detail_page_snapshot(manga_id)
        except Exception:
            pass

        comic = {}
        manga_title = ""
        chapters = []
        api_error = None
        try:
            referer, comic, manga_title, group_path_word = self._fetch_comic_overview(manga_id)
            chapters = self._fetch_chapter_list(manga_id, group_path_word, referer)
        except Exception as exc:
            api_error = exc

        latest = chapters[-1] if chapters else {}
        cover_url = extract_cover_url_from_data(comic, base_url=detail_url) or html_cover_url
        manga_title = manga_title or html_title or comic.get("name") or manga_id

        if not chapters and api_error is not None:
            if not (html_title or html_cover_url):
                raise api_error

            detail_parts = ["已通过详情页获取基础信息"]
            if cover_url:
                detail_parts.append("封面已同步到预览区")
            detail_parts.append("章节接口当前不可用")
            if start_slug:
                detail_parts.append(f"当前链接章节标识: {start_slug}")

            return MangaDetail(
                title=manga_title,
                manga_url=(url or "").strip(),
                section="手动链接",
                cover_url=cover_url,
                latest_chapter="-",
                update_time="-",
                detail_hint="，".join(detail_parts),
                detail_section_label=f"站点: {self.display_name}",
                chapter_count=0,
                start_chapter_title="",
            )

        start_chapter_title = find_start_chapter_title(chapters, start_slug)
        chapter_count = len(chapters)
        detail_parts = [f"共 {chapter_count} 章"] if chapter_count else ["未解析到章节列表"]
        if start_chapter_title:
            detail_parts.append(f"当前链接定位到 {start_chapter_title}")

        return MangaDetail(
            title=manga_title or comic.get("name") or manga_id,
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
        page_host = self._get_page_host(manga_slug)
        return f"https://{page_host}/comic/{manga_slug}/chapter/{{slug}}"

    def download_chapter_images(
        self,
        chapter_slug,
        base_url_template,
        root_dir,
        max_concurrent_images=5,
        stop_event=None,
        show_progress=True,
    ):
        chapter_url = base_url_template.format(slug=chapter_slug)
        parsed = urlparse(chapter_url)
        path_parts = [part for part in parsed.path.strip("/").split("/") if part]
        if len(path_parts) < 4:
            return 0, None, None

        manga_path_word = path_parts[1]
        referer = chapter_url
        payload, _ = self._request_json(
            f"/api/v3/comic/{manga_path_word}/chapter2/{chapter_slug}?platform=1",
            manga_path_word=manga_path_word,
            referer=referer,
        )

        results = payload.get("results", {})
        chapter = results.get("chapter") or {}
        comic = results.get("comic") or {}
        contents = chapter.get("contents") or []
        words = chapter.get("words") or list(range(len(contents)))

        chapter_name = chapter.get("name") or chapter_slug
        chapter_index = chapter.get("index")
        chapter_prefix = int(chapter_index) + 1 if isinstance(chapter_index, int) else 0
        chapter_dir_name = f"{chapter_prefix:03d}_{sanitize_filename(str(chapter_name))}" if chapter_prefix else sanitize_filename(str(chapter_name))
        chapter_dir = os.path.join(root_dir, chapter_dir_name)
        os.makedirs(chapter_dir, exist_ok=True)

        if not contents:
            with print_lock:
                print(f"[警告] MangaCopy 章节无图片数据: {chapter_url}")
            return 0, None, {"slug": chapter_slug}

        download_tasks = []
        for idx, image_info in enumerate(contents, 1):
            image_url = image_info.get("url")
            if not image_url:
                continue
            filename = f"{idx:03d}.jpg"
            if idx - 1 < len(words):
                try:
                    filename = f"{int(words[idx - 1]) + 1:03d}.jpg"
                except Exception:
                    pass
            download_tasks.append((image_url, os.path.join(chapter_dir, filename)))

        local_files = [
            name for name in os.listdir(chapter_dir)
            if name.lower().endswith((".jpg", ".jpeg", ".png", ".webp"))
        ]
        if len(local_files) >= len(download_tasks) and download_tasks:
            with print_lock:
                print(f"[跳过] MangaCopy 章节 {chapter_dir_name}: 已完整下载")
            return len(download_tasks), None, {"slug": chapter_slug}

        progress = tqdm(
            total=len(download_tasks),
            desc=f"📖 {chapter_dir_name[:30]}",
            unit="img",
            leave=False,
            dynamic_ncols=True,
            disable=not show_progress,
        )

        success_count = 0
        with progress:
            with ThreadPoolExecutor(max_workers=max_concurrent_images) as executor:
                future_map = {
                    executor.submit(self._download_image, image_url, dest_path, referer, stop_event): (image_url, dest_path)
                    for image_url, dest_path in download_tasks
                }
                for future in as_completed(future_map):
                    if stop_event is not None and stop_event.is_set():
                        break
                    if future.result():
                        success_count += 1
                    progress.update(1)

        manga_name = comic.get("name") or manga_path_word
        with print_lock:
            print(f"[完成] MangaCopy 章节下载完成: {manga_name} / {chapter_dir_name} ({success_count}/{len(download_tasks)})")

        return success_count, None, {"slug": chapter_slug}


MANHUAGUI_LZJS = r"""var LZString=(function(){var f=String.fromCharCode;var keyStrBase64="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";var baseReverseDic={};function getBaseValue(alphabet,character){if(!baseReverseDic[alphabet]){baseReverseDic[alphabet]={};for(var i=0;i<alphabet.length;i++){baseReverseDic[alphabet][alphabet.charAt(i)]=i}}return baseReverseDic[alphabet][character]}var LZString={decompressFromBase64:function(input){if(input==null)return"";if(input=="")return null;return LZString._0(input.length,32,function(index){return getBaseValue(keyStrBase64,input.charAt(index))})},_0:function(length,resetValue,getNextValue){var dictionary=[],next,enlargeIn=4,dictSize=4,numBits=3,entry="",result=[],i,w,bits,resb,maxpower,power,c,data={val:getNextValue(0),position:resetValue,index:1};for(i=0;i<3;i+=1){dictionary[i]=i}bits=0;maxpower=Math.pow(2,2);power=1;while(power!=maxpower){resb=data.val&data.position;data.position>>=1;if(data.position==0){data.position=resetValue;data.val=getNextValue(data.index++)}bits|=(resb>0?1:0)*power;power<<=1}switch(next=bits){case 0:bits=0;maxpower=Math.pow(2,8);power=1;while(power!=maxpower){resb=data.val&data.position;data.position>>=1;if(data.position==0){data.position=resetValue;data.val=getNextValue(data.index++)}bits|=(resb>0?1:0)*power;power<<=1}c=f(bits);break;case 1:bits=0;maxpower=Math.pow(2,16);power=1;while(power!=maxpower){resb=data.val&data.position;data.position>>=1;if(data.position==0){data.position=resetValue;data.val=getNextValue(data.index++)}bits|=(resb>0?1:0)*power;power<<=1}c=f(bits);break;case 2:return""}dictionary[3]=c;w=c;result.push(c);while(true){if(data.index>length){return""}bits=0;maxpower=Math.pow(2,numBits);power=1;while(power!=maxpower){resb=data.val&data.position;data.position>>=1;if(data.position==0){data.position=resetValue;data.val=getNextValue(data.index++)}bits|=(resb>0?1:0)*power;power<<=1}switch(c=bits){case 0:bits=0;maxpower=Math.pow(2,8);power=1;while(power!=maxpower){resb=data.val&data.position;data.position>>=1;if(data.position==0){data.position=resetValue;data.val=getNextValue(data.index++)}bits|=(resb>0?1:0)*power;power<<=1}dictionary[dictSize++]=f(bits);c=dictSize-1;enlargeIn--;break;case 1:bits=0;maxpower=Math.pow(2,16);power=1;while(power!=maxpower){resb=data.val&data.position;data.position>>=1;if(data.position==0){data.position=resetValue;data.val=getNextValue(data.index++)}bits|=(resb>0?1:0)*power;power<<=1}dictionary[dictSize++]=f(bits);c=dictSize-1;enlargeIn--;break;case 2:return result.join('')}if(enlargeIn==0){enlargeIn=Math.pow(2,numBits);numBits++}if(dictionary[c]){entry=dictionary[c]}else{if(c===dictSize){entry=w+w.charAt(0)}else{return null}}result.push(entry);dictionary[dictSize++]=w+entry.charAt(0);enlargeIn--;w=entry;if(enlargeIn==0){enlargeIn=Math.pow(2,numBits);numBits++}}}};return LZString})();String.prototype.splic=function(f){return LZString.decompressFromBase64(this).split(f)};"""
MANHUAGUI_BASE64_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/="
MANHUAGUI_BASE64_LOOKUP = {
    char: index for index, char in enumerate(MANHUAGUI_BASE64_ALPHABET)
}


def manhuagui_lz_decompress_from_base64(value: str) -> Optional[str]:
    if value is None:
        return ""
    if value == "":
        return None

    def get_next_value(index: int) -> int:
        if index >= len(value):
            return 0
        return MANHUAGUI_BASE64_LOOKUP.get(value[index], 0)

    return manhuagui_lz_decompress(len(value), 32, get_next_value)


def manhuagui_lz_decompress(length: int, reset_value: int, get_next_value) -> Optional[str]:
    dictionary: Dict[int, Any] = {0: 0, 1: 1, 2: 2}
    enlarge_in = 4
    dict_size = 4
    num_bits = 3
    data = {
        "val": get_next_value(0),
        "position": reset_value,
        "index": 1,
    }

    def read_bits(bit_count: int) -> int:
        bits = 0
        max_power = 1 << bit_count
        power = 1
        while power != max_power:
            resb = data["val"] & data["position"]
            data["position"] >>= 1
            if data["position"] == 0:
                data["position"] = reset_value
                data["val"] = get_next_value(data["index"])
                data["index"] += 1
            bits |= (1 if resb > 0 else 0) * power
            power <<= 1
        return bits

    next_value = read_bits(2)
    if next_value == 0:
        c = chr(read_bits(8))
    elif next_value == 1:
        c = chr(read_bits(16))
    elif next_value == 2:
        return ""
    else:
        return None

    dictionary[3] = c
    result = [c]
    w = c

    while True:
        if data["index"] > length:
            return ""

        c = read_bits(num_bits)
        if c == 0:
            dictionary[dict_size] = chr(read_bits(8))
            dict_size += 1
            c = dict_size - 1
            enlarge_in -= 1
        elif c == 1:
            dictionary[dict_size] = chr(read_bits(16))
            dict_size += 1
            c = dict_size - 1
            enlarge_in -= 1
        elif c == 2:
            return "".join(result)

        if enlarge_in == 0:
            enlarge_in = 1 << num_bits
            num_bits += 1

        entry = dictionary.get(c)
        if entry is None:
            if c == dict_size:
                entry = w + w[0]
            else:
                return None

        result.append(entry)
        dictionary[dict_size] = w + entry[0]
        dict_size += 1
        enlarge_in -= 1
        w = entry

        if enlarge_in == 0:
            enlarge_in = 1 << num_bits
            num_bits += 1


def manhuagui_unpack_packed_js(payload: str, alphabet_size: int, word_count: int, key_data: str) -> Optional[str]:
    decoded_keys = manhuagui_lz_decompress_from_base64(key_data)
    key_parts = decoded_keys.split("|") if isinstance(decoded_keys, str) else []

    def encode_number(value: int) -> str:
        if value < alphabet_size:
            prefix = ""
        else:
            prefix = encode_number(value // alphabet_size)
        remainder = value % alphabet_size
        if remainder > 35:
            return prefix + chr(remainder + 29)
        return prefix + "0123456789abcdefghijklmnopqrstuvwxyz"[remainder]

    replacements = {}
    for index in range(word_count):
        word = encode_number(index)
        replacement = key_parts[index] if index < len(key_parts) else ""
        replacements[word] = replacement or word

    return re.sub(r"\b(\w+)\b", lambda match: replacements.get(match.group(1), match.group(1)), payload)


def fix_manhuagui_json_text(js_text: str) -> str:
    js_text = re.sub(r'(:\s*),', r': null,', js_text)

    empty_keys = re.findall(r'""\s*:', js_text)
    for index in range(len(empty_keys)):
        js_text = js_text.replace('"":', f'"e{index}":', 1)

    js_text = re.sub(r',\s*(?=[}\]])', '', js_text)
    return js_text


class ManhuaguiAdapter(BaseSiteAdapter):
    IMAGE_SERVERS = (
        "i.hamreus.com",
        "us2.hamreus.com",
        "us.hamreus.com",
        "dx.hamreus.com",
        "eu.hamreus.com",
        "lt.hamreus.com",
    )
    TEMP_CHAPTER_PREFIX = ".下载中_"

    def __init__(self):
        super().__init__(
            key="manhuagui",
            display_name="漫画柜",
            supported_domains=("www.manhuagui.com", "manhuagui.com"),
            supports_discovery=False,
            supports_search=True,
            supports_download=True,
            discovery_placeholder="站内搜索",
            status_hint="已启用手动 URL 下载和站内搜索；首页发现暂未接入。",
        )
        self._session_headers = {
            "DNT": "1",
            "Connection": "keep-alive",
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
            "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
            "Cache-Control": "no-cache",
            "Pragma": "no-cache",
            "Upgrade-Insecure-Requests": "1",
            "User-Agent": (
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/137.0.0.0 Safari/537.36"
            ),
        }
        self._thread_local = threading.local()
        self._prefer_env_html_session = self._has_proxy_env()
        self._prefer_env_image_session = self._has_proxy_env()
        self._manual_proxy_url = ""
        self._manual_proxy_dict = None

    def _build_session(self, trust_env: bool, proxy_dict: Optional[Dict[str, str]] = None) -> requests.Session:
        session = requests.Session()
        session.trust_env = trust_env
        session.headers.update(self._session_headers)
        if proxy_dict:
            session.proxies.update(proxy_dict)
        return session

    def _get_session(self, mode: str) -> requests.Session:
        attr_name = f"_{mode}_session"
        session = getattr(self._thread_local, attr_name, None)
        if session is None:
            if mode == "manual":
                session = self._build_session(trust_env=False, proxy_dict=self._manual_proxy_dict)
            elif mode == "env":
                session = self._build_session(trust_env=True)
            else:
                session = self._build_session(trust_env=False)
            setattr(self._thread_local, attr_name, session)
        return session

    def _normalize_manual_proxy(self, proxy_url: str) -> Tuple[str, Optional[Dict[str, str]]]:
        normalized = (proxy_url or "").strip()
        if not normalized:
            return "", None

        if "://" not in normalized:
            normalized = f"http://{normalized}"

        parsed = urlparse(normalized)
        if not parsed.scheme or not parsed.netloc:
            raise ValueError("代理地址格式不正确，请使用 host:port 或 http://host:port")

        supported_schemes = {"http", "https", "socks5", "socks5h"}
        if parsed.scheme.lower() not in supported_schemes:
            raise ValueError("当前仅支持 http/https/socks5/socks5h 代理地址")

        proxy_dict = {
            "http": normalized,
            "https": normalized,
        }
        return normalized, proxy_dict

    def supports_manual_proxy(self) -> bool:
        return True

    def set_manual_proxy(self, proxy_url: str):
        normalized, proxy_dict = self._normalize_manual_proxy(proxy_url)
        self._manual_proxy_url = normalized
        self._manual_proxy_dict = proxy_dict
        self._thread_local = threading.local()
        self._prefer_env_html_session = self._has_proxy_env() and not proxy_dict
        self._prefer_env_image_session = self._has_proxy_env() and not proxy_dict

    def get_manual_proxy_url(self) -> str:
        return self._manual_proxy_url

    def has_manual_proxy(self) -> bool:
        return bool(self._manual_proxy_dict)

    def configure_requests_session(self, session: requests.Session, for_image: bool = False):
        session.headers.update(self._session_headers)
        if self._manual_proxy_dict:
            session.trust_env = False
            session.proxies.clear()
            session.proxies.update(self._manual_proxy_dict)
            return
        session.trust_env = self.should_use_env_for_http()
        session.proxies.clear()

    def _has_proxy_env(self) -> bool:
        proxy_env_names = (
            "HTTP_PROXY",
            "HTTPS_PROXY",
            "ALL_PROXY",
            "http_proxy",
            "https_proxy",
            "all_proxy",
        )
        return any(os.environ.get(name) for name in proxy_env_names)

    def _iter_request_sessions(self, prefer_env: Optional[bool] = None):
        direct_session = self._get_session("direct")
        if self._manual_proxy_dict:
            manual_session = self._get_session("manual")
            return (("manual", manual_session), ("direct", direct_session))

        has_proxy_env = self._has_proxy_env()
        if not has_proxy_env:
            return (("direct", direct_session),)

        env_session = self._get_session("env")
        prefer_env = self._prefer_env_html_session if prefer_env is None else prefer_env
        primary = ("env", env_session) if prefer_env else ("direct", direct_session)
        secondary = ("direct", direct_session) if prefer_env else ("env", env_session)
        return (primary, secondary)

    def _request_html(
        self,
        url: str,
        referer: Optional[str] = None,
        timeout: Tuple[int, int] = (8, 15),
        retry_rounds: int = 3,
        cooldown_seconds: float = 1.6,
        prefer_env: Optional[bool] = None,
    ) -> str:
        errors = []
        headers = {}
        if referer:
            headers["Referer"] = referer

        for attempt in range(retry_rounds):
            current_prefer_env = self._has_proxy_env() if prefer_env is True else prefer_env
            for mode, session in self._iter_request_sessions(prefer_env=current_prefer_env):
                try:
                    response = session.get(url, headers=headers, timeout=timeout)
                    response.raise_for_status()
                    self._prefer_env_html_session = (mode == "env")
                    return response.text
                except Exception as exc:
                    errors.append(f"{mode} -> {exc}")
            if attempt < retry_rounds - 1:
                time.sleep(cooldown_seconds + attempt * 0.8)

        detail = " | ".join(errors[-6:]) if errors else "未知网络错误"
        raise RuntimeError(f"Manhuagui 页面请求失败: {detail}")

    def _request_html_interactive(self, url: str, referer: Optional[str] = None) -> str:
        using_proxy = self.has_manual_proxy() or self._has_proxy_env()
        timeout = (5, 10) if using_proxy else (4, 7)
        retry_rounds = 2 if using_proxy else 1
        cooldown_seconds = 0.5 if using_proxy else 0.0
        return self._request_html(
            url,
            referer=referer,
            timeout=timeout,
            retry_rounds=retry_rounds,
            cooldown_seconds=cooldown_seconds,
            prefer_env=self._has_proxy_env(),
        )

    def adjust_download_settings(self, chapter_concurrency: int, image_concurrency: int) -> Tuple[int, int, str]:
        adjusted_chapter = min(chapter_concurrency, 1)
        adjusted_image = min(image_concurrency, 3)
        if adjusted_chapter != chapter_concurrency or adjusted_image != image_concurrency:
            return (
                adjusted_chapter,
                adjusted_image,
                f"{self.display_name} 当前容易触发超时，已自动调整为章节并发 {adjusted_chapter}、图片并发 {adjusted_image} 以提升稳定性。",
            )
        return adjusted_chapter, adjusted_image, ""

    def get_chapter_retry_limit(self) -> int:
        return 4

    def get_retry_delay_seconds(self, retry_count: int) -> float:
        return min(10 + (retry_count - 1) * 8, 36)

    def should_retry_download_error(self, error: Exception) -> bool:
        message = str(error or "")
        transient_markers = (
            "Read timed out",
            "ConnectTimeout",
            "Connection to",
            "RemoteDisconnected",
            "ChunkedEncodingError",
            "ProxyError",
            "页面请求失败",
            "图片下载不完整",
        )
        return any(marker in message for marker in transient_markers)

    def should_use_env_for_http(self) -> bool:
        if self._manual_proxy_dict:
            return False
        return self._prefer_env_html_session or self._prefer_env_image_session or self._has_proxy_env()

    def _find_chapter_script_text(self, html: str) -> str:
        soup = BeautifulSoup(html or "", "html.parser")
        for script in soup.find_all("script"):
            script_text = script.get_text() or ""
            if not script_text:
                continue
            if (
                r'["\x65\x76\x61\x6c"]' in script_text
                or "return p;}" in script_text
                or "SMH.imgData(" in script_text
            ):
                return script_text
        return ""

    def _parse_chapter_payload_text(self, payload_text: str) -> Dict:
        normalized = fix_manhuagui_json_text((payload_text or "").strip())
        if not normalized:
            raise RuntimeError("Manhuagui 章节图片数据为空")
        try:
            return json.loads(normalized)
        except json.JSONDecodeError as exc:
            raise RuntimeError("Manhuagui 章节图片数据解析失败") from exc

    def _normalize_image_file_name(self, file_name: Any) -> str:
        normalized = str(file_name or "").strip()
        if normalized.lower().endswith(".webp") and "." in normalized[:-5]:
            return normalized[:-5]
        return normalized

    def _build_single_image_url(
        self,
        path: str,
        file_name: str,
        cid: Any,
        md5: str,
        e_value: Any,
        m_value: Any,
    ) -> str:
        encoded_path = quote(path or "", safe="/")
        encoded_file_name = quote(str(file_name or ""), safe="._-")
        if cid and md5:
            return f"https://{self.IMAGE_SERVERS[0]}{encoded_path}{encoded_file_name}?cid={cid}&md5={md5}"
        if e_value is not None and m_value is not None:
            return f"https://{self.IMAGE_SERVERS[0]}{encoded_path}{encoded_file_name}?e={e_value}&m={m_value}"
        return f"https://{self.IMAGE_SERVERS[0]}{encoded_path}{encoded_file_name}"

    def _build_image_url_variants(self, comic_data: Dict, file_name: Any) -> List[str]:
        path = comic_data.get("path") or ""
        cid = comic_data.get("cid")
        sl_data = comic_data.get("sl") or {}
        md5 = sl_data.get("md5", "")
        e_value = sl_data.get("e")
        m_value = sl_data.get("m")

        raw_file_name = str(file_name or "").strip()
        preferred_file_name = self._normalize_image_file_name(raw_file_name)

        urls: List[str] = []
        for candidate_name in (preferred_file_name, raw_file_name):
            if not candidate_name:
                continue
            image_url = self._build_single_image_url(path, candidate_name, cid, md5, e_value, m_value)
            if image_url not in urls:
                urls.append(image_url)
        return urls

    def _candidate_image_extensions(self, file_name: Any) -> List[str]:
        raw_name = str(file_name or "").strip()
        preferred_name = self._normalize_image_file_name(raw_name)
        extensions: List[str] = []
        for candidate_name in (preferred_name, raw_name):
            ext = os.path.splitext(candidate_name)[1].lower()
            if ext and ext not in extensions:
                extensions.append(ext)
        if not extensions:
            extensions.append(".jpg")
        return extensions

    def _build_image_entries(self, comic_data: Dict) -> List[Dict[str, Any]]:
        files = comic_data.get("files") or []
        entries: List[Dict[str, Any]] = []
        for index, file_name in enumerate(files, 1):
            image_urls = self._build_image_url_variants(comic_data, file_name)
            if not image_urls:
                continue
            entries.append({
                "index": index,
                "urls": image_urls,
                "extensions": self._candidate_image_extensions(file_name),
            })
        return entries

    def _build_download_tasks_for_dir(self, image_entries: List[Dict[str, Any]], chapter_dir: str) -> List[Dict[str, Any]]:
        tasks: List[Dict[str, Any]] = []
        for entry in image_entries:
            dest_stem = os.path.join(chapter_dir, f"{int(entry['index']):03d}")
            candidate_paths: List[str] = []
            for ext in entry.get("extensions") or [".jpg"]:
                normalized_ext = ext.lower()
                if not normalized_ext.startswith("."):
                    normalized_ext = f".{normalized_ext}"
                candidate_path = f"{dest_stem}{normalized_ext}"
                if candidate_path not in candidate_paths:
                    candidate_paths.append(candidate_path)
            tasks.append({
                "index": entry["index"],
                "urls": list(entry.get("urls") or []),
                "dest_stem": dest_stem,
                "candidate_paths": candidate_paths,
            })
        return tasks

    def _remove_file_quietly(self, path: str):
        if not path:
            return
        try:
            if os.path.exists(path):
                os.remove(path)
        except OSError:
            pass

    def _find_existing_image_path(self, candidate_paths: List[str]) -> Optional[str]:
        for candidate_path in candidate_paths:
            self._remove_file_quietly(f"{candidate_path}.part")
            if not os.path.exists(candidate_path):
                continue
            try:
                if os.path.getsize(candidate_path) > 0:
                    return candidate_path
            except OSError:
                pass
            self._remove_file_quietly(candidate_path)
        return None

    def _is_chapter_complete(self, download_tasks: List[Dict[str, Any]]) -> bool:
        return bool(download_tasks) and all(
            self._find_existing_image_path(task.get("candidate_paths") or [])
            for task in download_tasks
        )

    def _prepare_chapter_download_dir(self, chapter_dir: str, image_entries: List[Dict[str, Any]]) -> Tuple[str, List[Dict[str, Any]], bool]:
        existing_tasks = self._build_download_tasks_for_dir(image_entries, chapter_dir)
        if self._is_chapter_complete(existing_tasks):
            return chapter_dir, existing_tasks, True

        chapter_root = os.path.dirname(chapter_dir)
        chapter_dir_name = os.path.basename(chapter_dir)
        temp_chapter_dir = os.path.join(chapter_root, f"{self.TEMP_CHAPTER_PREFIX}{chapter_dir_name}")

        active_dir = temp_chapter_dir
        if os.path.isdir(temp_chapter_dir):
            active_dir = temp_chapter_dir
        elif os.path.isdir(chapter_dir):
            try:
                os.replace(chapter_dir, temp_chapter_dir)
                active_dir = temp_chapter_dir
            except OSError:
                active_dir = chapter_dir

        os.makedirs(active_dir, exist_ok=True)
        return active_dir, self._build_download_tasks_for_dir(image_entries, active_dir), False

    def _commit_chapter_download_dir(self, active_dir: str, final_dir: str):
        if os.path.abspath(active_dir) == os.path.abspath(final_dir):
            return
        if os.path.isdir(final_dir):
            shutil.rmtree(final_dir)
        os.replace(active_dir, final_dir)

    def _iter_image_request_urls(self, image_url: str) -> List[str]:
        parsed = urlparse(image_url)
        scheme = parsed.scheme or "https"
        request_path = parsed.path + (f"?{parsed.query}" if parsed.query else "")
        hosts: List[str] = []
        if parsed.netloc:
            hosts.append(parsed.netloc)
        for server in self.IMAGE_SERVERS:
            if server not in hosts:
                hosts.append(server)
        return [f"{scheme}://{host}{request_path}" for host in hosts]

    def _looks_like_html_bytes(self, chunk: bytes) -> bool:
        sample = (chunk or b"").lstrip()[:64].lower()
        return (
            sample.startswith(b"<!doctype html")
            or sample.startswith(b"<html")
            or sample.startswith(b"<body")
            or sample.startswith(b"<?xml")
        )

    def _select_dest_path_for_url(self, dest_stem: str, image_url: str, candidate_paths: List[str]) -> str:
        path = urlparse(image_url).path
        ext = os.path.splitext(path)[1].lower()
        if ext:
            resolved = f"{dest_stem}{ext}"
            if resolved in candidate_paths or not candidate_paths:
                return resolved
        return candidate_paths[0] if candidate_paths else f"{dest_stem}.jpg"

    def _extract_payload_text_from_script(self, script_text: str) -> str:
        start_token = "SMH.imgData("
        start_index = script_text.find(start_token)
        if start_index < 0:
            return ""
        start_index += len(start_token)

        end_index = script_text.find(").preInit()", start_index)
        if end_index < 0:
            end_index = script_text.find(").preInit(", start_index)
        if end_index < 0:
            return ""
        return script_text[start_index:end_index].strip()

    def _extract_chapter_payload_python(self, html: str) -> Dict:
        script_text = self._find_chapter_script_text(html)
        if not script_text:
            raise RuntimeError("Manhuagui 章节脚本结构已变化，未找到脚本段")

        direct_payload = self._extract_payload_text_from_script(script_text)
        if direct_payload:
            return self._parse_chapter_payload_text(direct_payload)

        packed_patterns = (
            re.compile(r"return p;}\('(.*?)',(\d+),(\d+),'(.*?)'\[", re.S),
            re.compile(r"return p;}\('(.*?)',(\d+),(\d+),'(.*?)'\.split\('\|'\)", re.S),
        )

        unpacked_js = None
        for pattern in packed_patterns:
            match = pattern.search(script_text)
            if not match:
                continue
            unpacked_js = manhuagui_unpack_packed_js(
                match.group(1),
                int(match.group(2)),
                int(match.group(3)),
                match.group(4),
            )
            if unpacked_js:
                break

        if not unpacked_js:
            raise RuntimeError("Manhuagui 章节脚本结构已变化，未找到可解包数据")

        payload_text = self._extract_payload_text_from_script(unpacked_js)
        if not payload_text:
            raise RuntimeError("Manhuagui 章节图片数据结构已变化，未找到 SMH.imgData")

        return self._parse_chapter_payload_text(payload_text)

    def _extract_chapter_payload_cscript(self, html: str) -> Dict:
        script_match = re.search(r'\["\\x65\\x76\\x61\\x6c"\](.*?)</script>', html, re.S)
        if not script_match:
            raise RuntimeError("Manhuagui 章节脚本结构已变化，未找到加密数据段")

        js_payload = script_match.group(1).strip()
        script = "\n".join([
            MANHUAGUI_LZJS,
            f"var __decoded = {js_payload};",
            "var __start = __decoded.indexOf('SMH.imgData(');",
            "var __end = __decoded.indexOf(').preInit();', __start);",
            "if (__start < 0 || __end < 0) { WScript.Echo(''); }",
            "else { WScript.Echo(encodeURIComponent(__decoded.substring(__start + 12, __end))); }",
        ])

        temp_path = None
        try:
            with tempfile.NamedTemporaryFile("w", suffix=".js", delete=False, encoding="utf-8") as temp_file:
                temp_file.write(script)
                temp_path = temp_file.name

            result = subprocess.run(
                ["cscript.exe", "//Nologo", temp_path],
                capture_output=True,
                text=True,
                timeout=20,
            )
            if result.returncode != 0:
                raise RuntimeError(result.stderr.strip() or "cscript 执行失败")

            payload_text = unquote((result.stdout or "").strip())
            if not payload_text:
                raise RuntimeError("Manhuagui 章节图片数据结构已变化，未找到 SMH.imgData")
            return json.loads(payload_text)
        except subprocess.TimeoutExpired as exc:
            raise RuntimeError("Manhuagui 章节脚本解析超时") from exc
        except json.JSONDecodeError as exc:
            raise RuntimeError("Manhuagui 章节图片数据解析失败") from exc
        finally:
            if temp_path:
                try:
                    os.remove(temp_path)
                except OSError:
                    pass

    def _extract_chapter_payload(self, html: str) -> Dict:
        errors = []

        try:
            payload = self._extract_chapter_payload_python(html)
            if payload:
                return payload
        except Exception as exc:
            errors.append(f"python -> {exc}")

        try:
            payload = self._extract_chapter_payload_cscript(html)
            if payload:
                return payload
        except Exception as exc:
            errors.append(f"cscript -> {exc}")

        detail = " | ".join(errors) if errors else "未知错误"
        raise RuntimeError(f"Manhuagui 章节图片数据解析失败: {detail}")

    def _decode_viewstate_html(self, soup: BeautifulSoup) -> Optional[BeautifulSoup]:
        warning_node = soup.find("div", class_="warning-bar")
        viewstate_node = soup.select_one("input#__VIEWSTATE")
        if warning_node is None or viewstate_node is None:
            return None

        encoded_html = coerce_html_attr_to_str(viewstate_node.get("value")).strip()
        if not encoded_html:
            return None

        decoded_html = manhuagui_lz_decompress_from_base64(encoded_html)
        if not decoded_html:
            return None
        return BeautifulSoup(decoded_html, "html.parser")

    def _extract_chapters_from_list_soup(self, chapter_soup: BeautifulSoup, manga_id: str) -> List[Dict]:
        chapter_pattern = re.compile(rf"/comic/{re.escape(str(manga_id))}/(\d+)\.html$")
        chapters = []
        seen = set()

        list_groups = chapter_soup.select("div.chapter-list")
        for group in list_groups:
            for part in group.select("ul"):
                part_chapters = []
                for li in part.select("li"):
                    link = li.find("a", href=True)
                    if not link:
                        continue
                    href = coerce_html_attr_to_str(link.get("href", "")).strip()
                    match = chapter_pattern.search(href)
                    if not match:
                        continue
                    chapter_id = match.group(1)
                    span = li.find("span")
                    chapter_title = ""
                    if span is not None:
                        chapter_title = (span.find(string=True, recursive=False) or "").strip()
                    if not chapter_title:
                        chapter_title = coerce_html_attr_to_str(link.get("title", "")).strip() or link.get_text(" ", strip=True) or chapter_id
                    part_chapters.append((chapter_id, chapter_title))

                for chapter_id, chapter_title in reversed(part_chapters):
                    if chapter_id in seen:
                        continue
                    seen.add(chapter_id)
                    chapters.append({
                        "slug": chapter_id,
                        "order": len(chapters),
                        "title": chapter_title,
                        "updated_at": "",
                    })

        return chapters

    def _extract_chapters_from_link_scan(self, chapter_soup: BeautifulSoup, manga_id: str) -> List[Dict]:
        chapter_pattern = re.compile(rf"/comic/{re.escape(str(manga_id))}/(\d+)\.html$")
        chapters = []
        seen = set()

        for link in chapter_soup.find_all("a", href=True):
            match = chapter_pattern.search(coerce_html_attr_to_str(link.get("href", "")).strip())
            if not match:
                continue
            chapter_id = match.group(1)
            if chapter_id in seen:
                continue
            seen.add(chapter_id)
            chapter_title = (
                coerce_html_attr_to_str(link.get("title", "")).strip()
                or link.get_text(" ", strip=True)
                or chapter_id
            )
            chapters.append({
                "slug": chapter_id,
                "order": len(chapters),
                "title": chapter_title,
                "updated_at": "",
            })

        chapters.reverse()
        for index, chapter in enumerate(chapters):
            chapter["order"] = index
        return chapters

    def _build_image_urls(self, comic_data: Dict) -> List[str]:
        image_urls = []
        for file_name in comic_data.get("files") or []:
            variants = self._build_image_url_variants(comic_data, file_name)
            if variants:
                image_urls.append(variants[0])
        return image_urls

    def _download_image(
        self,
        image_urls: List[str],
        dest_stem: str,
        candidate_paths: List[str],
        referer: str,
        stop_event=None,
    ) -> bool:
        if stop_event is not None and stop_event.is_set():
            return False
        if self._find_existing_image_path(candidate_paths):
            return True

        for image_url in image_urls:
            final_path = self._select_dest_path_for_url(dest_stem, image_url, candidate_paths)
            temp_path = f"{final_path}.part"
            for attempt_url in self._iter_image_request_urls(image_url):
                for mode, session in self._iter_request_sessions(prefer_env=self._prefer_env_image_session):
                    if stop_event is not None and stop_event.is_set():
                        self._remove_file_quietly(temp_path)
                        return False
                    try:
                        with session.get(
                            attempt_url,
                            headers={"Referer": referer, "Accept": "image/avif,image/webp,image/*,*/*;q=0.8"},
                            timeout=30,
                            stream=True,
                        ) as response:
                            if response.status_code != 200:
                                continue

                            content_type = (response.headers.get("Content-Type") or "").lower()
                            if content_type and not content_type.startswith("image/"):
                                continue

                            os.makedirs(os.path.dirname(final_path), exist_ok=True)
                            bytes_written = 0
                            first_chunk = True
                            with open(temp_path, "wb") as file_obj:
                                for chunk in response.iter_content(8192):
                                    if stop_event is not None and stop_event.is_set():
                                        raise InterruptedError
                                    if not chunk:
                                        continue
                                    if first_chunk:
                                        first_chunk = False
                                        if self._looks_like_html_bytes(chunk) and not content_type.startswith("image/"):
                                            raise ValueError("图片响应不是图片")
                                    file_obj.write(chunk)
                                    bytes_written += len(chunk)

                            if bytes_written <= 0:
                                self._remove_file_quietly(temp_path)
                                continue

                            os.replace(temp_path, final_path)
                            self._prefer_env_image_session = (mode == "env")
                            return True
                    except InterruptedError:
                        self._remove_file_quietly(temp_path)
                        return False
                    except Exception:
                        self._remove_file_quietly(temp_path)
                        continue

        for candidate_path in candidate_paths:
            self._remove_file_quietly(candidate_path)
            self._remove_file_quietly(f"{candidate_path}.part")
        return False

    def get_manga_info_from_url(self, url: str):
        parsed = urlparse((url or "").strip())
        path_parts = [part for part in parsed.path.strip("/").split("/") if part]

        manga_id = None
        chapter_id = None

        if len(path_parts) >= 2 and path_parts[0] == "comic":
            manga_id = path_parts[1]
            if len(path_parts) >= 3:
                chapter_match = re.match(r"(\d+)\.html$", path_parts[2])
                if chapter_match:
                    chapter_id = chapter_match.group(1)

        if not manga_id:
            return None, None, None
        return manga_id, manga_id, chapter_id

    def get_manga_cache_key(self, url: str) -> str:
        manga_id, _, _ = self.get_manga_info_from_url(url)
        if manga_id:
            return f"{self.key}:{manga_id}"
        return super().get_manga_cache_key(url)

    def fetch_search_cards(self, keyword: str, page: int = 1) -> List[HomepageMangaCard]:
        keyword = (keyword or "").strip()
        if not keyword:
            return []

        page = max(int(page or 1), 1)
        page_suffix = "" if page == 1 else f"_p{page}"
        encoded_keyword = quote(keyword, safe="")
        search_url = f"https://{self.supported_domains[0]}/s/{encoded_keyword}{page_suffix}.html"
        html = self._request_html_interactive(
            search_url,
            referer=f"https://{self.supported_domains[0]}/",
        )
        soup = BeautifulSoup(html, "html.parser")

        cards: List[HomepageMangaCard] = []
        for item in soup.select(".book-result > ul > li"):
            title_link = item.select_one(".book-detail > dl > dt > a[href]")
            if not title_link:
                continue

            title = title_link.get_text(strip=True)
            href = coerce_html_attr_to_str(title_link.get("href", "")).strip()
            if not title or not href:
                continue

            manga_url = urljoin(search_url, href)
            cover_node = item.select_one(".book-cover > a > img")
            cover_url = ""
            if cover_node is not None:
                cover_url = resolve_media_url(
                    search_url,
                    coerce_html_attr_to_str(cover_node.get("data-src") or cover_node.get("src") or ""),
                )

            status_node = item.select_one(".book-detail > dl > dd:nth-child(2) span span")
            year_node = item.select_one(".book-detail > dl > dd:nth-child(3) span a")
            author_node = item.select_one(".book-detail > dl > dd:nth-child(4)")

            status_text = status_node.get_text(strip=True) if status_node else ""
            year_text = year_node.get_text(strip=True) if year_node else ""
            author_text = ""
            if author_node:
                author_text = author_node.get_text(" ", strip=True)
                author_text = re.sub(r"^作者[:：]\s*", "", author_text)

            card = HomepageMangaCard(
                section="搜索结果",
                title=title,
                manga_url=manga_url,
                chapterlist_url=manga_url,
                cover_url=cover_url,
            )

            detail_parts = []
            if author_text:
                detail_parts.append(f"作者: {author_text}")
            if year_text:
                detail_parts.append(f"年份: {year_text}")
            setattr(card, "detail_hint", "，".join(detail_parts))
            setattr(card, "detail_section_label", f"状态: {status_text or '未知'}")

            cards.append(card)

        return cards

    def _parse_detail_page(self, html: str, manga_id: str, detail_url: str):
        soup = BeautifulSoup(html, "html.parser")
        chapter_soup = self._decode_viewstate_html(soup) or soup

        title = ""
        title_node = soup.find("h1")
        if title_node:
            title = title_node.get_text(strip=True)
        if not title:
            title_tag = soup.find("title")
            if title_tag:
                title = title_tag.get_text(strip=True).split("漫画_")[0].strip(" -")
        if not title:
            title = f"Comic_{manga_id}"

        cover_url = extract_cover_url_from_html(html, detail_url)
        chapters = self._extract_chapters_from_list_soup(chapter_soup, manga_id)
        if not chapters:
            chapters = self._extract_chapters_from_link_scan(chapter_soup, manga_id)

        return title, cover_url, chapters

    def get_all_chapters(self, manga_id):
        detail_url = f"https://{self.supported_domains[0]}/comic/{manga_id}/"
        html = self._request_html_interactive(
            detail_url,
        )
        title, _, chapters = self._parse_detail_page(html, manga_id, detail_url)
        return title, chapters

    def fetch_manga_detail(self, url: str):
        manga_id, _, start_slug = self.get_manga_info_from_url(url)
        if not manga_id:
            raise RuntimeError(f"{self.display_name} 无法识别该漫画链接")

        detail_url = f"https://{self.supported_domains[0]}/comic/{manga_id}/"
        html = self._request_html_interactive(
            detail_url,
        )
        title, cover_url, chapters = self._parse_detail_page(html, manga_id, detail_url)
        latest = chapters[-1] if chapters else {}
        start_chapter_title = find_start_chapter_title(chapters, start_slug)
        chapter_count = len(chapters)
        detail_parts = [f"共 {chapter_count} 章"] if chapter_count else ["未解析到章节列表"]
        if start_chapter_title:
            detail_parts.append(f"当前链接定位到 {start_chapter_title}")

        return MangaDetail(
            title=title,
            manga_url=(url or "").strip(),
            section="手动链接",
            cover_url=cover_url,
            latest_chapter=latest.get("title") or "-",
            update_time="-",
            detail_hint="，".join(detail_parts),
            detail_section_label=f"站点: {self.display_name}",
            chapter_count=chapter_count,
            start_chapter_title=start_chapter_title,
        )

    def build_chapter_url_template(self, manga_slug: str) -> str:
        return f"https://{self.supported_domains[0]}/comic/{manga_slug}/{{slug}}.html"

    def download_chapter_images(
        self,
        chapter_slug,
        base_url_template,
        root_dir,
        max_concurrent_images=5,
        stop_event=None,
        show_progress=True,
    ):
        chapter_url = base_url_template.format(slug=chapter_slug)
        html = self._request_html(
            chapter_url,
            referer=f"https://{self.supported_domains[0]}/",
            timeout=(9, 16),
            retry_rounds=3,
            cooldown_seconds=1.8,
            prefer_env=self._has_proxy_env(),
        )
        comic_data = self._extract_chapter_payload(html)

        manga_name = comic_data.get("bname") or "Manhuagui"
        chapter_name = comic_data.get("cname") or chapter_slug
        chapter_dir_name = f"{str(chapter_slug).zfill(6)}_{sanitize_filename(str(chapter_name))}"
        chapter_dir = os.path.join(root_dir, chapter_dir_name)

        image_entries = self._build_image_entries(comic_data)
        if not image_entries:
            with print_lock:
                print(f"[警告] Manhuagui 章节无图片数据: {chapter_url}")
            return 0, None, {"slug": chapter_slug}

        active_chapter_dir, download_tasks, is_already_complete = self._prepare_chapter_download_dir(chapter_dir, image_entries)
        if is_already_complete:
            with print_lock:
                print(f"[跳过] Manhuagui 章节 {chapter_dir_name}: 已完整下载")
            return len(download_tasks), None, {"slug": chapter_slug}

        existing_count = 0
        pending_tasks = []
        for task in download_tasks:
            if self._find_existing_image_path(task.get("candidate_paths") or []):
                existing_count += 1
            else:
                pending_tasks.append(task)

        if existing_count >= len(download_tasks) and download_tasks:
            self._commit_chapter_download_dir(active_chapter_dir, chapter_dir)
            with print_lock:
                print(f"[跳过] Manhuagui 章节 {chapter_dir_name}: 已完整下载")
            return len(download_tasks), None, {"slug": chapter_slug}

        progress = tqdm(
            total=len(download_tasks),
            desc=f"📖 {chapter_dir_name[:30]}",
            unit="img",
            leave=False,
            dynamic_ncols=True,
            disable=not show_progress,
            initial=existing_count,
        )

        success_count = existing_count
        with progress:
            if pending_tasks:
                with ThreadPoolExecutor(max_workers=max_concurrent_images) as executor:
                    future_map = {
                        executor.submit(
                            self._download_image,
                            task["urls"],
                            task["dest_stem"],
                            task["candidate_paths"],
                            chapter_url,
                            stop_event,
                        ): task
                        for task in pending_tasks
                    }
                    for future in as_completed(future_map):
                        if stop_event is not None and stop_event.is_set():
                            break
                        if future.result():
                            success_count += 1
                        progress.update(1)

        if success_count >= len(download_tasks) and download_tasks:
            self._commit_chapter_download_dir(active_chapter_dir, chapter_dir)

        with print_lock:
            print(f"[完成] Manhuagui 章节下载完成: {manga_name} / {chapter_dir_name} ({success_count}/{len(download_tasks)})")

        if success_count < len(download_tasks) and not (stop_event is not None and stop_event.is_set()):
            raise RuntimeError(f"Manhuagui 图片下载不完整: {success_count}/{len(download_tasks)}")

        next_id = comic_data.get("nextId")
        next_slug = str(next_id) if next_id else None
        return success_count, next_slug, {"slug": next_slug} if next_slug else {"slug": chapter_slug}


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
