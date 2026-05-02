"""断点续传 & 失败章节数据变换。

下载失败的章节会记录成一个 dict：{order, display_order, slug, title, reason}。
这里集中维护：
- 记录的规范化（类型修正、display_order 推算、reason 清洗）
- 以章节序号或标题展示失败清单
- 把失败记录重新匹配回当前抓到的章节列表（供"重试失败章节"使用）
- 断点续传时间戳的解析

所有函数都是纯数据变换，不依赖 GUI 状态。
"""
from __future__ import annotations

from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple

import re


_WEB_URL_PATTERN = re.compile(r"https?://\S+")
_ANSI_PATTERN = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
_HANGING_COMMA_PATTERN = re.compile(r"\(\s*,\s*")
_EMPTY_PARENS_PATTERN = re.compile(r"\(\s*\)")


def normalize_log_message(message: str) -> str:
    text = _ANSI_PATTERN.sub("", message or "")
    text = _WEB_URL_PATTERN.sub("", text)
    text = _HANGING_COMMA_PATTERN.sub("，", text)
    text = _EMPTY_PARENS_PATTERN.sub("", text)
    text = " ".join(text.split())
    return text.strip(" :-")


def _safe_int(value: Any) -> Optional[int]:
    if value is None:
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def normalize_failed_retry_records(failed_records: Any) -> List[Dict[str, Any]]:
    """把任意来源（用户旧存档、内存状态）的失败记录规范为统一格式。"""
    normalized: List[Dict[str, Any]] = []
    for record in failed_records or []:
        if not isinstance(record, dict):
            continue

        order = _safe_int(record.get("order"))
        display_order = _safe_int(record.get("display_order"))
        if display_order is None and isinstance(order, int) and order >= 0:
            display_order = order + 1

        normalized.append({
            "order": order,
            "display_order": display_order,
            "slug": str(record.get("slug") or ""),
            "title": str(record.get("title") or ""),
            "reason": normalize_log_message(str(record.get("reason") or "")),
        })
    return normalized


def get_failed_chapter_numbers_text(failed_records: Any) -> str:
    """优先用 display_order 组装 "1、3、7" 这种人类友好的章节号；
    没有序号的记录回退到标题或 slug。"""
    numbers: List[str] = []
    fallback_labels: List[str] = []

    for record in normalize_failed_retry_records(failed_records):
        display_order = record.get("display_order")
        if isinstance(display_order, int) and display_order > 0:
            numbers.append(str(display_order))
            continue

        title = (record.get("title") or "").strip()
        slug = (record.get("slug") or "").strip()
        if title:
            fallback_labels.append(title)
        elif slug:
            fallback_labels.append(slug)

    if numbers:
        return "、".join(numbers)
    return "、".join(fallback_labels)


def format_failed_chapter_list_text(failed_records: Any) -> str:
    """UI 文案用：有序号时变成 "第 1、3、7 章"，否则回退为标题列表。"""
    numbers_text = get_failed_chapter_numbers_text(failed_records)
    if not numbers_text:
        return "未知章节"

    normalized = normalize_failed_retry_records(failed_records)
    if normalized and all(
        isinstance(record.get("display_order"), int) and record.get("display_order") > 0
        for record in normalized
    ):
        return f"第 {numbers_text} 章"
    return numbers_text


def build_failed_chapter_record(chapter: Dict[str, Any], reason: str = "") -> Dict[str, Any]:
    """从抓到的章节 dict 构造一条失败记录。"""
    order = _safe_int(chapter.get("order"))
    display_order = order + 1 if isinstance(order, int) and order >= 0 else None
    return {
        "order": order,
        "display_order": display_order,
        "slug": str(chapter.get("slug") or chapter.get("uuid") or ""),
        "title": str(chapter.get("title") or ""),
        "reason": normalize_log_message(str(reason or "")),
    }


def match_retry_chapters(
    all_chapters: List[Dict[str, Any]],
    failed_records: Any,
) -> Tuple[List[Dict[str, Any]], List[Dict[str, Any]]]:
    """把失败记录映射回当前抓到的章节列表。

    匹配优先级：order → slug → title。
    返回 (matched_chapters, missing_records)。
    重复命中同一章节会去重，保证重试列表不会重复下载。
    """
    matched_chapters: List[Dict[str, Any]] = []
    missing_records: List[Dict[str, Any]] = []
    seen_keys = set()

    for record in normalize_failed_retry_records(failed_records):
        match = None
        target_order = record.get("order")
        target_slug = (record.get("slug") or "").strip()
        target_title = (record.get("title") or "").strip()

        if isinstance(target_order, int):
            match = next(
                (chapter for chapter in all_chapters if chapter.get("order") == target_order),
                None,
            )

        if match is None and target_slug:
            match = next(
                (chapter for chapter in all_chapters if str(chapter.get("slug") or "") == target_slug),
                None,
            )

        if match is None and target_title:
            match = next(
                (chapter for chapter in all_chapters if str(chapter.get("title") or "").strip() == target_title),
                None,
            )

        if match is None:
            missing_records.append(record)
            continue

        match_key = (
            match.get("order"),
            str(match.get("slug") or ""),
            str(match.get("title") or ""),
        )
        if match_key in seen_keys:
            continue

        seen_keys.add(match_key)
        matched_chapters.append(match)

    return matched_chapters, missing_records


def parse_resume_timestamp(value: Any) -> Optional[datetime]:
    """解析断点续传存档里的 "YYYY-MM-DD HH:MM:SS"。非法值返回 None。"""
    if not value:
        return None
    try:
        return datetime.strptime(str(value), "%Y-%m-%d %H:%M:%S")
    except Exception:
        return None
