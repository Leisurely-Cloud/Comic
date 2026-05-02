"""本地漫画库的纯工具：路径、扫描、记录构造、状态文本。

这一层是无状态纯函数：不读 GUI 实例、不持有任何 Tk 资源，只依赖磁盘路径和传进来的记录字典。
GUI 层的 saved_manga_detail_cache / current_adapter 等状态留在 ComicDownloaderGUI，
通过把需要的片段（章节列表、已知封面 URL 等）显式传参的方式调用这些工具。
"""
from __future__ import annotations

import json
import os
from datetime import datetime
from typing import Any, Callable, Dict, List, Optional

from .archive import list_exportable_image_files
from .chapter_naming import is_final_chapter_dir_name, looks_like_manga_download_dir
from .downcomic import sanitize_filename
from .resume_state import normalize_failed_retry_records, parse_resume_timestamp
from .site_adapters import get_adapter


LIBRARY_METADATA_FILE_NAME = "元数据.json"
IMAGE_EXTENSIONS = (".jpg", ".jpeg", ".png", ".webp")


# 库扫描时要跳过的仓库/构建目录。加过多目录的代价是扫不到合法下载目录，加少的代价是误入 .git 翻遍。
_LIBRARY_SCAN_EXCLUDED_DIRS = frozenset({
    "__pycache__", ".git", ".venv", "build", "build_pyinstaller",
    "dist", "dist_build", "release",
})


def get_library_scan_excluded_dirs() -> set:
    return set(_LIBRARY_SCAN_EXCLUDED_DIRS)


def get_manga_metadata_path(root_dir: str) -> str:
    return os.path.join(root_dir, LIBRARY_METADATA_FILE_NAME)


def count_image_files_in_dir(directory_path: str) -> int:
    count = 0
    try:
        for entry in os.scandir(directory_path):
            if entry.is_file() and entry.name.lower().endswith(IMAGE_EXTENSIONS):
                count += 1
    except Exception:
        return 0
    return count


def build_library_title_key(title: Any) -> str:
    """用于按标题在缓存里找一个漫画身份。优先用 sanitize_filename，确保跨站点一致。"""
    normalized = sanitize_filename(str(title or "")).strip().lower()
    if normalized:
        return normalized
    return str(title or "").strip().lower()


def extract_site_key_from_cache_key(cache_key: Any) -> str:
    """cache_key 形如 "baozimh:https://...", 只取冒号前的站点键。"""
    normalized = str(cache_key or "").strip()
    if ":" not in normalized:
        return ""
    return normalized.split(":", 1)[0].strip()


def infer_site_key_from_chapter_dirs(chapter_dirs) -> str:
    """从章节目录名（"001_xxx"）推测源站：
    - 漫画柜用 6+ 位数字前缀
    - 包子漫画存在 order=0 的前言章节
    其余返回空串，由调用方兜底。
    """
    numeric_prefixes = []
    for dir_name in chapter_dirs or []:
        prefix, _, _ = str(dir_name or "").partition("_")
        if not prefix.isdigit():
            continue
        numeric_prefixes.append(prefix)

    if any(len(prefix) >= 6 for prefix in numeric_prefixes):
        return "manhuagui"
    if any(int(prefix) == 0 for prefix in numeric_prefixes):
        return "baozimh"
    return ""


def find_local_library_cover_path(root_dir: str) -> str:
    """取最早那一章的第一张图作为库卡片封面。没有图返回空串。"""
    resolved_root = (root_dir or "").strip()
    if not resolved_root or not os.path.isdir(resolved_root):
        return ""

    chapter_dirs = []
    try:
        for entry in os.scandir(resolved_root):
            if entry.is_dir() and is_final_chapter_dir_name(entry.name):
                chapter_dirs.append((entry.name, entry.path))
    except Exception:
        return ""

    chapter_dirs.sort(key=lambda item: item[0])
    for _, chapter_dir in chapter_dirs:
        image_files = list_exportable_image_files(chapter_dir)
        if image_files:
            return image_files[0]
    return ""


def compact_chapter_info(chapter: Dict[str, Any]) -> Dict[str, Any]:
    """把完整的章节 dict 压缩成断点续传/元数据需要的最小字段。"""
    return {
        "order": chapter.get("order"),
        "slug": str(chapter.get("slug") or ""),
        "title": str(chapter.get("title") or ""),
        "updated_at": str(chapter.get("updated_at") or ""),
    }


def build_downloaded_chapter_record(
    chapter: Optional[Dict[str, Any]],
    dir_name: str,
    image_count: int = 0,
) -> Dict[str, Any]:
    """把一个磁盘上的章节目录 + 已知章节元信息 合并成一条 downloaded_chapters 记录。"""
    title = ""
    prefix = ""
    if "_" in dir_name:
        prefix, title = dir_name.split("_", 1)
    else:
        title = dir_name
        prefix = dir_name

    title = title or (chapter.get("title") if chapter else "") or dir_name
    slug = str(chapter.get("slug") or "") if chapter else (prefix if prefix.isdigit() else "")
    order = chapter.get("order") if chapter else None
    display_order = order + 1 if isinstance(order, int) else None

    return {
        "order": display_order,
        "slug": slug,
        "title": title,
        "updated_at": str((chapter or {}).get("updated_at") or ""),
        "dir_name": dir_name,
        "image_count": int(image_count or 0),
    }


def build_downloaded_chapter_records_from_disk(
    root_dir: str,
    known_chapters: Optional[List[Dict[str, Any]]] = None,
) -> List[Dict[str, Any]]:
    """扫描漫画根目录下的最终态章节目录，尽力匹配已抓到的 known_chapters 元数据。

    匹配顺序：章节目录名前缀的数字序号 → known_chapters 里的 slug 数字 → 标题归一化。
    匹配到的 chapter 会标记 used，保证不会重复消费同一条章节元信息。
    """
    records: List[Dict[str, Any]] = []
    final_dirs = []
    try:
        for entry in os.scandir(root_dir):
            if entry.is_dir() and is_final_chapter_dir_name(entry.name):
                final_dirs.append((entry.name, entry.path, count_image_files_in_dir(entry.path)))
    except Exception:
        return records

    final_dirs.sort(key=lambda item: item[0])
    normalized_known = list(known_chapters or [])
    used_indices: set = set()

    for dir_name, _dir_path, image_count in final_dirs:
        prefix, _, title_part = dir_name.partition("_")
        safe_dir_title = sanitize_filename(title_part or dir_name)
        matched_index = None
        matched_chapter = None

        if prefix.isdigit():
            prefix_value = int(prefix)
            for index, chapter in enumerate(normalized_known):
                if index in used_indices:
                    continue
                order = chapter.get("order")
                slug = str(chapter.get("slug") or "")
                if isinstance(order, int) and order + 1 == prefix_value:
                    matched_index = index
                    matched_chapter = chapter
                    break
                if slug.isdigit() and int(slug) == prefix_value:
                    matched_index = index
                    matched_chapter = chapter
                    break

        if matched_chapter is None and safe_dir_title:
            for index, chapter in enumerate(normalized_known):
                if index in used_indices:
                    continue
                if sanitize_filename(str(chapter.get("title") or "")) == safe_dir_title:
                    matched_index = index
                    matched_chapter = chapter
                    break

        if matched_index is not None:
            used_indices.add(matched_index)

        records.append(build_downloaded_chapter_record(matched_chapter, dir_name, image_count))

    records.sort(key=lambda item: (
        item.get("order") is None,
        item.get("order") if item.get("order") is not None else item.get("dir_name", ""),
    ))
    return records


def format_local_library_status(entry: Dict[str, Any]) -> str:
    """给详情页展示的"本地状态: ..."一行。"""
    downloaded_count = int(entry.get("downloaded_chapter_count") or 0)
    total_chapters = int(entry.get("total_chapters") or 0)
    completed = bool(entry.get("completed"))

    if downloaded_count <= 0:
        return "本地状态: 未发现已下载章节"
    if total_chapters > 0:
        if completed and downloaded_count >= total_chapters:
            return f"本地状态: 已下载完成（{downloaded_count}/{total_chapters} 章）"
        return f"本地状态: 已下载 {downloaded_count}/{total_chapters} 章"
    if completed:
        return f"本地状态: 已下载完成（{downloaded_count} 章）"
    return f"本地状态: 已下载 {downloaded_count} 章"


def get_library_update_status_lines(entry: Dict[str, Any], include_error: bool = False) -> List[str]:
    """库卡片/详情页右下角要展示的更新检查相关文本。"""
    status = str(entry.get("update_check_status") or "").strip()
    checked_at = str(entry.get("update_last_checked_at") or "").strip()
    error = str(entry.get("update_last_error") or "").strip()
    lines: List[str] = []
    if status:
        lines.append(f"更新检查: {status}")
    if checked_at:
        lines.append(f"检查时间: {checked_at}")
    if include_error and error:
        lines.append(f"错误信息: {error}")
    return lines


def compute_update_available_count(entry: Dict[str, Any], online_chapter_count: Any) -> int:
    """线上总章节 - 本地已下载进度 = 待更新章节数，保底 0。"""
    try:
        normalized_total = max(int(online_chapter_count or 0), 0)
    except Exception:
        normalized_total = 0

    try:
        last_downloaded_order = int(entry.get("last_downloaded_chapter_order") or 0)
    except Exception:
        last_downloaded_order = 0

    try:
        downloaded_count = int(entry.get("downloaded_chapter_count") or 0)
    except Exception:
        downloaded_count = 0

    known_progress = last_downloaded_order if last_downloaded_order > 0 else downloaded_count
    return max(normalized_total - max(known_progress, 0), 0)


# --- 元数据 I/O ---

def load_manga_library_metadata(root_dir: str) -> Optional[Dict[str, Any]]:
    """读取一个漫画目录下的 元数据.json，顺便把字段类型归一化。"""
    metadata_path = get_manga_metadata_path(root_dir)
    if not os.path.exists(metadata_path):
        return None
    try:
        with open(metadata_path, "r", encoding="utf-8") as file_obj:
            payload = json.load(file_obj)
    except Exception:
        return None

    if not isinstance(payload, dict):
        return None

    payload["root_dir"] = root_dir
    payload["downloaded_chapters"] = list(payload.get("downloaded_chapters") or [])
    payload["downloaded_chapter_count"] = int(
        payload.get("downloaded_chapter_count") or len(payload["downloaded_chapters"]) or 0
    )
    payload["total_chapters"] = int(
        payload.get("total_chapters") or payload["downloaded_chapter_count"] or 0
    )
    payload["update_check_status"] = str(payload.get("update_check_status") or "")
    payload["update_available_count"] = int(payload.get("update_available_count") or 0)
    payload["update_last_checked_at"] = str(payload.get("update_last_checked_at") or "")
    payload["update_last_error"] = str(payload.get("update_last_error") or "")
    payload["last_failed_chapter_records"] = normalize_failed_retry_records(
        payload.get("last_failed_chapter_records") or []
    )
    payload["last_failed_chapter_count"] = int(
        payload.get("last_failed_chapter_count")
        or len(payload["last_failed_chapter_records"])
        or 0
    )
    payload["last_failed_chapter_numbers_text"] = str(payload.get("last_failed_chapter_numbers_text") or "")
    payload["last_download_final_state"] = str(payload.get("last_download_final_state") or "")
    return payload


def save_library_entry_metadata(
    entry: Dict[str, Any],
    *,
    on_error: Optional[Callable[[BaseException], None]] = None,
) -> bool:
    """把一条库 entry 写回对应目录的 元数据.json。
    - root_dir 与 `_*` 私有字段不会写入
    - 写失败时调用 on_error（如果提供），返回 False
    """
    if not isinstance(entry, dict):
        return False

    root_dir = (entry.get("root_dir") or "").strip()
    if not root_dir:
        return False

    try:
        os.makedirs(root_dir, exist_ok=True)
    except Exception:
        return False

    payload = {}
    for key, value in entry.items():
        if key == "root_dir" or str(key).startswith("_"):
            continue
        payload[key] = value

    if "schema_version" not in payload:
        payload["schema_version"] = 1

    try:
        with open(get_manga_metadata_path(root_dir), "w", encoding="utf-8") as file_obj:
            json.dump(payload, file_obj, ensure_ascii=False, indent=2)
        return True
    except Exception as exc:
        if on_error is not None:
            try:
                on_error(exc)
            except Exception:
                pass
        return False


# --- 身份识别 ---

def find_cached_library_identity_by_title(
    manga_title: str,
    saved_detail_cache: Optional[Dict[str, Any]] = None,
    preferred_site_key: str = "",
) -> Optional[Dict[str, Any]]:
    """在漫画详情缓存里按标题找身份信息（来自哪个站、URL、封面）。

    匹配规则：标题归一化后相等；打分排序考虑是否有封面/URL、章节数，
    偏好 preferred_site_key。多站匹配且无偏好时返回 None（避免跨站错配）。
    """
    target_title_key = build_library_title_key(manga_title)
    if not target_title_key:
        return None

    matches = []
    for cache_key, payload in (saved_detail_cache or {}).items():
        if not isinstance(payload, dict):
            continue
        cached_title_key = build_library_title_key(payload.get("title"))
        if cached_title_key != target_title_key:
            continue

        site_key = extract_site_key_from_cache_key(cache_key)
        site_name = get_adapter(site_key).display_name if site_key else ""
        matches.append({
            "site_key": site_key,
            "site_name": site_name,
            "manga_url": str(payload.get("manga_url") or "").strip(),
            "cover_url": str(payload.get("cover_url") or "").strip(),
            "latest_chapter": str(payload.get("latest_chapter") or "").strip(),
            "chapter_count": int(payload.get("chapter_count") or 0),
        })

    if not matches:
        return None

    def sort_key(item):
        return (
            0 if item.get("cover_url") else 1,
            0 if item.get("manga_url") else 1,
            -int(item.get("chapter_count") or 0),
        )

    matches.sort(key=sort_key)
    preferred_site_key = (preferred_site_key or "").strip()
    if preferred_site_key:
        preferred_matches = [item for item in matches if item.get("site_key") == preferred_site_key]
        if preferred_matches:
            return preferred_matches[0]

    unique_site_keys = {item.get("site_key") for item in matches if item.get("site_key")}
    if len(matches) == 1 or len(unique_site_keys) <= 1:
        return matches[0]
    return None


def enrich_local_library_entry_identity(
    entry: Dict[str, Any],
    saved_detail_cache: Optional[Dict[str, Any]] = None,
    preferred_site_key: str = "",
) -> Optional[Dict[str, Any]]:
    """给一条库 entry 补齐 site_key / site_name / manga_url / cover_url / 本地封面路径。

    返回 None 表示：指定了 preferred_site_key，但 entry 的站点既不匹配缓存也不匹配章节目录推测。
    """
    if not isinstance(entry, dict):
        return None

    enriched = dict(entry)
    preferred_site_key = (preferred_site_key or "").strip()
    current_site_key = (enriched.get("site_key") or "").strip()
    root_dir = (enriched.get("root_dir") or "").strip()
    manga_title = str(enriched.get("manga_title") or os.path.basename(root_dir.rstrip("\\/")) or "本地漫画")

    guessed_site_key = ""
    if root_dir and os.path.isdir(root_dir):
        chapter_dir_names = []
        try:
            for item in os.scandir(root_dir):
                if item.is_dir() and is_final_chapter_dir_name(item.name):
                    chapter_dir_names.append(item.name)
        except Exception:
            chapter_dir_names = []
        guessed_site_key = infer_site_key_from_chapter_dirs(chapter_dir_names)

    cached_identity = find_cached_library_identity_by_title(
        manga_title,
        saved_detail_cache=saved_detail_cache,
        preferred_site_key=current_site_key or guessed_site_key or preferred_site_key,
    )

    resolved_site_key = (cached_identity or {}).get("site_key") or guessed_site_key or current_site_key
    if preferred_site_key:
        if not resolved_site_key or resolved_site_key != preferred_site_key:
            return None

    enriched["manga_title"] = manga_title
    enriched["site_key"] = resolved_site_key
    if cached_identity:
        enriched["site_name"] = cached_identity.get("site_name") or enriched.get("site_name") or ""
        if not (enriched.get("manga_url") or "").strip():
            enriched["manga_url"] = cached_identity.get("manga_url") or ""
        if not (enriched.get("cover_url") or "").strip():
            enriched["cover_url"] = cached_identity.get("cover_url") or ""
    if not enriched.get("site_name"):
        enriched["site_name"] = get_adapter(resolved_site_key).display_name if resolved_site_key else "未知站点（旧下载）"

    enriched["_local_cover_path"] = find_local_library_cover_path(root_dir)
    return enriched


# --- 库扫描 ---

def build_local_library_entry_from_fallback(
    directory_path: str,
    saved_detail_cache: Optional[Dict[str, Any]] = None,
    site_key: str = "",
) -> Optional[Dict[str, Any]]:
    """对没有 元数据.json 的遗留下载目录，通过磁盘扫描兜底生成一条 entry。"""
    chapter_dirs = []
    try:
        for entry in os.scandir(directory_path):
            if entry.is_dir() and is_final_chapter_dir_name(entry.name):
                chapter_dirs.append(entry.name)
    except Exception:
        return None

    if not chapter_dirs:
        return None

    chapter_dirs.sort()
    latest_dir_name = chapter_dirs[-1]
    latest_title = latest_dir_name.split("_", 1)[1] if "_" in latest_dir_name else latest_dir_name
    modified_at = datetime.fromtimestamp(os.path.getmtime(directory_path)).strftime("%Y-%m-%d %H:%M:%S")

    entry = {
        "schema_version": 0,
        "site_key": "",
        "site_name": "",
        "manga_title": os.path.basename(directory_path.rstrip("\\/")) or "本地漫画",
        "manga_url": "",
        "root_dir": directory_path,
        "cover_url": "",
        "total_chapters": len(chapter_dirs),
        "downloaded_chapter_count": len(chapter_dirs),
        "last_downloaded_chapter_title": latest_title,
        "last_downloaded_chapter_order": None,
        "downloaded_chapters": [
            build_downloaded_chapter_record(None, dir_name, count_image_files_in_dir(os.path.join(directory_path, dir_name)))
            for dir_name in chapter_dirs
        ],
        "completed": True,
        "created_at": modified_at,
        "saved_at": modified_at,
    }
    return enrich_local_library_entry_identity(entry, saved_detail_cache=saved_detail_cache, preferred_site_key=site_key)


def iter_local_library_entries(
    library_search_roots: List[str],
    saved_detail_cache: Optional[Dict[str, Any]] = None,
    site_key: str = "",
    default_site_display_name: str = "",
) -> List[Dict[str, Any]]:
    """扫描给定根目录里的漫画库，返回按最近保存时间倒序的 entries。

    每个根目录下的一级子目录被视作候选漫画目录；有 元数据.json 的优先用元数据，
    没有的走 build_local_library_entry_from_fallback 兜底。
    """
    entries: List[Dict[str, Any]] = []
    excluded_dirs = get_library_scan_excluded_dirs()
    seen_root_dirs = set()

    for base_dir in library_search_roots or []:
        try:
            dir_entries = list(os.scandir(base_dir))
        except Exception:
            continue

        for entry in dir_entries:
            if not entry.is_dir():
                continue
            if entry.name in excluded_dirs:
                continue
            if entry.name.startswith(".") and not looks_like_manga_download_dir(entry.path):
                continue

            normalized_root_dir = os.path.normcase(os.path.abspath(entry.path))
            if normalized_root_dir in seen_root_dirs:
                continue
            seen_root_dirs.add(normalized_root_dir)

            disk_has_chapter_dirs = looks_like_manga_download_dir(entry.path)
            metadata = load_manga_library_metadata(entry.path)
            if metadata:
                if not disk_has_chapter_dirs:
                    continue
                metadata_site_key = (metadata.get("site_key") or "").strip()
                if site_key and metadata_site_key and metadata_site_key != site_key:
                    continue
                fallback_site_key = metadata_site_key or site_key
                fallback = build_local_library_entry_from_fallback(
                    entry.path, saved_detail_cache=saved_detail_cache, site_key=fallback_site_key,
                )
                if fallback is not None:
                    metadata["site_key"] = metadata_site_key or fallback.get("site_key") or ""
                    metadata["site_name"] = metadata.get("site_name") or fallback.get("site_name") or default_site_display_name
                    metadata["downloaded_chapters"] = list(fallback.get("downloaded_chapters") or [])
                    metadata["downloaded_chapter_count"] = int(fallback.get("downloaded_chapter_count") or 0)
                    metadata["last_downloaded_chapter_title"] = fallback.get("last_downloaded_chapter_title") or metadata.get("last_downloaded_chapter_title") or ""
                    metadata["last_downloaded_chapter_order"] = fallback.get("last_downloaded_chapter_order")
                    metadata["saved_at"] = metadata.get("saved_at") or fallback.get("saved_at") or metadata.get("created_at") or ""
                    metadata["total_chapters"] = max(
                        int(metadata.get("total_chapters") or 0),
                        int(metadata.get("downloaded_chapter_count") or 0),
                    )

                if site_key and not metadata_site_key:
                    fallback = build_local_library_entry_from_fallback(
                        entry.path, saved_detail_cache=saved_detail_cache, site_key=site_key,
                    )
                    if fallback is not None:
                        metadata["site_key"] = fallback.get("site_key") or metadata.get("site_key") or ""
                        metadata["site_name"] = fallback.get("site_name") or metadata.get("site_name") or ""
                    else:
                        continue

                finalized_metadata = enrich_local_library_entry_identity(
                    metadata, saved_detail_cache=saved_detail_cache, preferred_site_key=site_key,
                )
                if finalized_metadata is not None:
                    entries.append(finalized_metadata)
                continue

            if not disk_has_chapter_dirs:
                continue
            fallback = build_local_library_entry_from_fallback(
                entry.path, saved_detail_cache=saved_detail_cache, site_key=site_key,
            )
            if fallback is not None:
                entries.append(fallback)

    def sort_key(item):
        saved_at = str(item.get("saved_at") or item.get("created_at") or "")
        try:
            return parse_resume_timestamp(saved_at) or datetime.fromtimestamp(0)
        except Exception:
            return datetime.fromtimestamp(0)

    entries.sort(key=sort_key, reverse=True)
    return entries
