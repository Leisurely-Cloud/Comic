"""首页和分区漫画卡片抓取。"""
from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Dict, List, Optional
from urllib.parse import quote

from bs4 import BeautifulSoup

from .file_utils import (
    build_absolute_url,
    coerce_html_attr_to_str,
    normalize_chapterlist_url,
    unwrap_cover_url,
)
from .http_helpers import print, print_lock, safe_request


@dataclass
class HomepageMangaCard:
    section: str
    title: str
    manga_url: str
    chapterlist_url: str
    cover_url: str = ""
    latest_chapter: str = ""
    update_time: str = ""


def _extract_standard_card_section(soup: BeautifulSoup, heading: str) -> List[HomepageMangaCard]:
    cards: List[HomepageMangaCard] = []
    header = next((node for node in soup.find_all("h2") if node.get_text(strip=True) == heading), None)
    if not header:
        return cards

    title_link = header.find_parent("a", class_=re.compile(r"\bhometitle\b"))
    section_wrapper = None
    if title_link:
        title_row = title_link.find_parent("div")
        if title_row:
            section_wrapper = title_row.find_parent("div")

    if not section_wrapper:
        section_wrapper = header.find_parent("div")

    if not section_wrapper:
        return cards

    cardlist = section_wrapper.find("div", class_=re.compile(r"\bcardlist\b"))
    if not cardlist:
        return cards

    for wrapper in cardlist.find_all("div", recursive=False):
        item = wrapper.find("a", href=True)
        if not item:
            continue

        href = coerce_html_attr_to_str(item.get("href", "")).strip()
        title_node = item.find("h3")
        img_node = item.find("img")
        title = title_node.get_text(strip=True) if title_node else ""

        if not href or not title:
            continue

        manga_url = build_absolute_url(href)
        cards.append(
            HomepageMangaCard(
                section=heading,
                title=title,
                manga_url=manga_url,
                chapterlist_url=normalize_chapterlist_url(manga_url),
                cover_url=unwrap_cover_url(coerce_html_attr_to_str(img_node.get("src", "")).strip()) if img_node else "",
            )
        )

    return cards


def _extract_recent_update_section(soup: BeautifulSoup) -> List[HomepageMangaCard]:
    cards: List[HomepageMangaCard] = []
    header = next((node for node in soup.find_all("h2") if node.get_text(strip=True) == "近期更新"), None)
    if not header:
        return cards

    section_wrapper = header.find_parent("div")
    if not section_wrapper:
        return cards

    for item in section_wrapper.select("a.slicarda[href]"):
        href = coerce_html_attr_to_str(item.get("href", "")).strip()
        img_node = item.find("img")
        title_node = item.find("h3", class_=re.compile(r"\bslicardtitle\b"))
        time_node = item.find("p", class_=re.compile(r"\bslicardtagp\b"))
        latest_node = item.find("p", class_=re.compile(r"\bslicardtitlep\b"))

        title = title_node.get_text(strip=True) if title_node else ""
        if not href or not title:
            continue

        manga_url = build_absolute_url(href)
        cards.append(
            HomepageMangaCard(
                section="近期更新",
                title=title,
                manga_url=manga_url,
                chapterlist_url=normalize_chapterlist_url(manga_url),
                cover_url=unwrap_cover_url(coerce_html_attr_to_str(img_node.get("src", "")).strip()) if img_node else "",
                latest_chapter=latest_node.get_text(strip=True) if latest_node else "",
                update_time=time_node.get_text(strip=True) if time_node else "",
            )
        )

    return cards


def fetch_homepage_manga_cards() -> List[HomepageMangaCard]:
    """抓取首页主要榜单漫画卡片。"""
    homepage_url = build_absolute_url("/")
    with print_lock:
        print(f"🔍 Fetching homepage: {homepage_url}")

    resp = safe_request(homepage_url, retries=1)
    if not resp:
        return []

    soup = BeautifulSoup(resp.content, "html.parser")

    cards: List[HomepageMangaCard] = []
    cards.extend(_extract_recent_update_section(soup))
    cards.extend(_extract_standard_card_section(soup, "熱門更新"))
    cards.extend(_extract_standard_card_section(soup, "人氣排行"))
    cards.extend(_extract_standard_card_section(soup, "最新上架"))

    return cards


def fetch_section_manga_cards(section: str, page: int = 1) -> List[HomepageMangaCard]:
    """抓取分区列表页漫画卡片。支持 rank/hot-update/new/recent。"""
    if page < 1:
        page = 1

    if section == "recent":
        homepage_resp = safe_request(build_absolute_url("/"), retries=1)
        if not homepage_resp:
            return []
        return _extract_recent_update_section(BeautifulSoup(homepage_resp.content, "html.parser"))

    section_paths = {
        "rank": "/hots",
        "hot-update": "/dayup",
        "new": "/newss",
    }
    section_titles = {
        "rank": "人氣排行",
        "hot-update": "熱門更新",
        "new": "最新上架",
    }

    path = section_paths.get(section)
    if not path:
        return []

    page_url = build_absolute_url(f"{path}/page/{page}")
    with print_lock:
        print(f"🔍 Fetching section page: {page_url}")

    resp = safe_request(page_url, retries=1)
    if not resp:
        return []

    soup = BeautifulSoup(resp.content, "html.parser")

    cards: List[HomepageMangaCard] = []
    cardlist = soup.find("div", class_=re.compile(r"\bcardlist\b"))
    if not cardlist:
        return cards

    for wrapper in cardlist.find_all("div", recursive=False):
        item = wrapper.find("a", href=True)
        if not item:
            continue

        href = coerce_html_attr_to_str(item.get("href", "")).strip()
        title_node = item.find("h3")
        img_node = item.find("img")
        title = title_node.get_text(strip=True) if title_node else ""

        if not href or not title:
            continue

        manga_url = build_absolute_url(href)
        cards.append(
            HomepageMangaCard(
                section=section_titles.get(section, section),
                title=title,
                manga_url=manga_url,
                chapterlist_url=normalize_chapterlist_url(manga_url),
                cover_url=unwrap_cover_url(coerce_html_attr_to_str(img_node.get("src", "")).strip()) if img_node else "",
            )
        )

    return cards


def fetch_search_manga_cards(keyword: str, page: int = 1) -> List[HomepageMangaCard]:
    """按关键词抓取站内搜索结果，支持模糊搜索。"""
    keyword = (keyword or "").strip()
    if not keyword:
        return []

    if page < 1:
        page = 1

    page_url = f"{build_absolute_url('/s')}?q={quote(keyword)}&page={page}"
    with print_lock:
        print(f"🔍 Fetching search page: {page_url}")

    resp = safe_request(page_url, retries=1)
    if not resp:
        return []

    soup = BeautifulSoup(resp.content, "html.parser")

    cards: List[HomepageMangaCard] = []
    cardlist = soup.find("div", class_=re.compile(r"\bcardlist\b"))
    if not cardlist:
        return cards

    for wrapper in cardlist.find_all("div", recursive=False):
        item = wrapper.find("a", href=True)
        if not item:
            continue

        href = coerce_html_attr_to_str(item.get("href", "")).strip()
        title_node = item.find("h3")
        img_node = item.find("img")
        title = title_node.get_text(strip=True) if title_node else ""

        if not href or not title:
            continue

        manga_url = build_absolute_url(href)
        cards.append(
            HomepageMangaCard(
                section="搜索结果",
                title=title,
                manga_url=manga_url,
                chapterlist_url=normalize_chapterlist_url(manga_url),
                cover_url=unwrap_cover_url(coerce_html_attr_to_str(img_node.get("src", "")).strip()) if img_node else "",
            )
        )

    return cards


def filter_homepage_cards(cards: List[HomepageMangaCard], section: Optional[str] = None,
                          limit: Optional[int] = None) -> List[HomepageMangaCard]:
    """按分区和数量筛选首页卡片。"""
    section_aliases = {
        None: None,
        "all": None,
        "recent": "近期更新",
        "近期更新": "近期更新",
        "hot-update": "熱門更新",
        "热门更新": "熱門更新",
        "熱門更新": "熱門更新",
        "rank": "人氣排行",
        "排行": "人氣排行",
        "人气排行": "人氣排行",
        "人氣排行": "人氣排行",
        "new": "最新上架",
        "最新上架": "最新上架",
    }
    resolved_section = section_aliases.get(section, section)

    filtered = [card for card in cards if resolved_section is None or card.section == resolved_section]
    if limit is not None and limit > 0:
        filtered = filtered[:limit]
    return filtered


def print_homepage_cards(cards: List[HomepageMangaCard]):
    """将首页卡片以可读格式输出。"""
    if not cards:
        print("⚠️ No homepage manga cards found.")
        return

    for idx, card in enumerate(cards, 1):
        print(f"[{idx}] {card.title}")
        print(f"    分区: {card.section}")
        print(f"    详情页: {card.manga_url}")
        print(f"    目录页: {card.chapterlist_url}")
        if card.cover_url:
            print(f"    封面: {card.cover_url}")
        if card.latest_chapter:
            print(f"    最近章节: {card.latest_chapter}")
        if card.update_time:
            print(f"    更新时间: {card.update_time}")


def homepage_cards_to_dict(cards: List[HomepageMangaCard]) -> List[Dict[str, str]]:
    """把首页卡片对象转为可序列化字典。"""
    return [
        {
            "section": card.section,
            "title": card.title,
            "manga_url": card.manga_url,
            "chapterlist_url": card.chapterlist_url,
            "cover_url": card.cover_url,
            "latest_chapter": card.latest_chapter,
            "update_time": card.update_time,
        }
        for card in cards
    ]
