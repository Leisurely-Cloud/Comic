"""漫画信息获取：从 URL 提取 ID、获取章节列表。"""
from __future__ import annotations

from typing import List, Optional, Tuple
from urllib.parse import urlparse

from bs4 import BeautifulSoup

from .http_helpers import print, print_lock, safe_request, _api_fetch_json


def get_manga_info_from_url(url: str, stop_event=None) -> Tuple[Optional[str], Optional[str], Optional[str]]:
    """
    从 URL 中提取漫画 ID 和 slug
    :param url: 漫画目录页或章节页 URL
    :param stop_event: 可选的停止事件
    :return: (manga_id, manga_slug, start_slug)
    """
    parsed = urlparse(url)
    path_parts = parsed.path.strip("/").split("/")

    manga_slug = None
    start_slug = None
    manga_id = None

    if "chapterlist" in path_parts:
        try:
            idx = path_parts.index("chapterlist")
            if idx + 1 < len(path_parts):
                manga_slug = path_parts[idx + 1]
        except ValueError:
            pass
    elif "manga" in path_parts:
        try:
            idx = path_parts.index("manga")
            if idx + 1 < len(path_parts):
                manga_slug = path_parts[idx + 1]
            if idx + 2 < len(path_parts):
                start_slug = path_parts[idx + 2]
        except ValueError:
            pass

    if not manga_slug:
        with print_lock:
            print("❌ Could not extract manga slug from URL.")
        return None, None, None

    with print_lock:
        print(f"🔍 Analyzing URL: {url} (Slug: {manga_slug})")

    resp = safe_request(url, retries=1, stop_event=stop_event)
    if not resp:
        return None, None, None

    soup = BeautifulSoup(resp.content, "html.parser")

    # 尝试从目录页提取 data-mid
    all_chapters_div = soup.find("div", id="allchapters")
    if not all_chapters_div:
        all_chapters_div = soup.find("div", id="mangachapters")

    if all_chapters_div:
        manga_id = all_chapters_div.get("data-mid")

    # 尝试从章节页提取 data-ms
    if not manga_id:
        content_div = soup.find("div", id="chapterContent")
        if content_div:
            manga_id = content_div.get("data-ms")

    if not manga_id:
        with print_lock:
            print(f"❌ Could not find manga ID (data-mid or data-ms) in page: {url}")
            print(f"   Page title: {soup.title.string if soup.title else '(no title)'}")
        return None, None, None

    with print_lock:
        print(f"✅ Found Manga ID: {manga_id}")

    return manga_id, manga_slug, start_slug


def get_all_chapters(manga_id: str) -> Tuple[Optional[str], List[dict]]:
    """
    获取所有章节列表
    :param manga_id: 漫画 ID (例如 878)
    :return: (manga_title, chapters_list)
    """
    api_url = f"https://api-get-v3.mgsearcher.com/api/manga/get?mid={manga_id}&mode=all"
    with print_lock:
        print(f"🔍 Fetching chapter list from API: {api_url}")

    data = _api_fetch_json(api_url)
    if not data:
        return None, []

    try:
        if not data.get("status") or not data.get("data") or not data["data"].get("chapters"):
            with print_lock:
                print("⚠️ Invalid chapter list API response")
            return None, []

        manga_data = data["data"]
        manga_title = manga_data.get("title", f"Manga_{manga_id}")
        chapters_data = manga_data["chapters"]

        chapters = []
        for item in chapters_data:
            attr = item.get("attributes", {})
            chapters.append({
                "slug": attr.get("slug"),
                "order": attr.get("order"),
                "title": attr.get("title"),
                "updated_at": attr.get("updatedAt")
            })

        # 按 order 排序 (从小到大)
        chapters.sort(key=lambda x: x["order"])

        with print_lock:
            print(f"✅ Found manga: {manga_title}, {len(chapters)} chapters.")

        return manga_title, chapters

    except Exception as e:
        with print_lock:
            print(f"⚠️ Failed to parse chapter list: {e}")
        return None, []
