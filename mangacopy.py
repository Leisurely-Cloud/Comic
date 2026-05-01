"""拷贝漫画适配器。"""
from __future__ import annotations

import ast
import html as html_lib
import os
import re
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import quote, urljoin, urlparse

import requests
from bs4 import BeautifulSoup
from concurrent.futures import ThreadPoolExecutor, as_completed
from tqdm import tqdm

from downcomic import (
    HomepageMangaCard,
    OperationCancelledError,
    print_lock,
    sanitize_filename,
)

from base import (
    BaseSiteAdapter,
    MangaDetail,
    coerce_html_attr_to_str,
    extract_cover_url_from_data,
    extract_cover_url_from_html,
    find_start_chapter_title,
    resolve_media_url,
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
        if self.is_proxy_pool_enabled():
            return
        if self._manual_proxy_dict:
            session.proxies.update(self._manual_proxy_dict)

    def _request(self, method: str, url: str, *, proxy_attempts: int = 3, stop_event=None, **kwargs):
        return self.request_with_session(
            self._session,
            method,
            url,
            use_proxy_pool=self.is_proxy_pool_enabled(),
            proxy_attempts=proxy_attempts,
            stop_event=stop_event,
            **kwargs,
        )

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

    def _request_json(
        self,
        path: str,
        manga_path_word: Optional[str] = None,
        referer: Optional[str] = None,
        stop_event=None,
    ):
        page_host = self._get_page_host(manga_path_word)
        last_error = None
        attempt_limit = 6 if self.is_proxy_pool_enabled() else 2
        request_target = "章节接口" if "/chapters?" in path else "漫画概览接口" if "/comic2/" in path else "JSON 接口"

        for api_host in self._build_api_hosts(page_host):
            url = f"https://{api_host}{path}"
            headers = {}
            if referer:
                headers["Referer"] = referer

            for attempt in range(attempt_limit):
                if stop_event is not None and stop_event.is_set():
                    raise OperationCancelledError("已停止连通性测试")
                print(
                    f"[拷贝漫画] 正在请求{request_target}: host={api_host}, 第 {attempt + 1}/{attempt_limit} 次"
                )
                try:
                    response = self._request("GET", url, headers=headers, timeout=20, stop_event=stop_event)
                    response.raise_for_status()
                    payload = response.json()
                    if payload.get("code") == 200:
                        print(f"[拷贝漫画] {request_target}请求成功: host={api_host}")
                        return payload, api_host

                    message = payload.get("message") or payload.get("results", {}).get("detail") or "请求失败"
                    last_error = RuntimeError(f"{api_host} 返回错误: {message}")
                    if payload.get("code") == 210:
                        blocked_error = RuntimeError(
                            f"{api_host} 暂时拒绝当前网络环境访问: {message}"
                        )
                        if self.is_proxy_pool_enabled():
                            print(
                                f"[拷贝漫画] {request_target}被站点拒绝，准备切换节点重试: host={api_host}, 第 {attempt + 1}/{attempt_limit} 次, 原因: {message}"
                            )
                            last_error = blocked_error
                            if stop_event is not None and stop_event.is_set():
                                raise OperationCancelledError("已停止连通性测试")
                            time.sleep(min(0.5 + attempt * 0.2, 1.5))
                            continue
                        raise blocked_error
                except Exception as exc:
                    if isinstance(exc, OperationCancelledError):
                        raise
                    if isinstance(exc, RuntimeError) and "暂时拒绝当前网络环境访问" in str(exc):
                        raise exc
                    print(
                        f"[拷贝漫画] {request_target}请求失败，准备重试: host={api_host}, 第 {attempt + 1}/{attempt_limit} 次, 错误: {exc}"
                    )
                    last_error = exc
                    if stop_event is not None and stop_event.is_set():
                        raise OperationCancelledError("已停止连通性测试")
                    time.sleep(0.5)
                    continue

        if self.is_proxy_pool_enabled() and isinstance(last_error, RuntimeError) and "暂时拒绝当前网络环境访问" in str(last_error):
            raise RuntimeError(
                f"{last_error}。当前代理池里的公开代理节点很可能也被拷贝漫画识别或限制了，建议改用你自己的稳定代理节点。"
            )

        raise RuntimeError(f"MangaCopy API 请求失败: {last_error}")

    def _build_detail_url(self, manga_path_word: str) -> str:
        page_host = self._get_page_host(manga_path_word)
        return f"https://{page_host}/comic/{manga_path_word}"

    def _build_site_url(self, path: str) -> str:
        normalized_path = path if str(path).startswith("/") else f"/{path}"
        return f"https://{self._get_page_host()}{normalized_path}"

    def _request_html_page(self, url: str, referer: Optional[str] = None) -> Tuple[str, str]:
        response = self._request(
            "GET",
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
        print(f"[拷贝漫画] 正在获取漫画概览: {manga_id}")
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
        print(f"[拷贝漫画] 漫画概览获取完成: {manga_title}, 分组={group_path_word}, host={api_host}")
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
        print(f"[拷贝漫画] 正在获取章节列表: {manga_id}, 分组={group_path_word}")
        chapter_payload, _ = self._request_json(
            f"/api/v3/comic/{manga_id}/group/{group_path_word}/chapters?limit=500&offset=0&platform=1",
            manga_path_word=manga_id,
            referer=referer,
        )
        chapter_list = chapter_payload.get("results", {}).get("list") or []
        chapters = self._parse_chapter_list(chapter_list, manga_id)
        print(f"[拷贝漫画] 章节列表获取完成: 共 {len(chapters)} 章")
        return chapters

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
        response = self._request(
            "GET",
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
            with self._request(
                "GET",
                image_url,
                headers={"Referer": referer, "Accept": "image/avif,image/webp,image/*,*/*;q=0.8"},
                timeout=30,
                stream=True,
            ) as response:
                response.raise_for_status()
                with open(dest_path, "wb") as file_obj:
                    for chunk in response.iter_content(65536):
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

    def probe_connection(self, target_url: str, stop_event=None) -> Tuple[int, str]:
        manga_id, _, _ = self.get_manga_info_from_url(target_url)
        if not manga_id:
            return super().probe_connection(target_url, stop_event=stop_event)

        referer = self._build_detail_url(manga_id)
        _, api_host = self._request_json(
            f"/api/v3/comic2/{manga_id}?platform=1&_update=true",
            manga_path_word=manga_id,
            referer=referer,
            stop_event=stop_event,
        )
        return 200, f"https://{api_host}/api/v3/comic2/{manga_id}?platform=1&_update=true"

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

