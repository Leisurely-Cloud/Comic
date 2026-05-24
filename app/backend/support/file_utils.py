"""文件名和 URL 工具函数。"""
from __future__ import annotations

import os
import re
from urllib.parse import urljoin, urlparse


def sanitize_filename(name: str) -> str:
    """清理文件名，移除非法字符。"""
    if not name:
        return "unnamed"
    # 移除或替换非法字符
    name = re.sub(r'[<>:"/\\|?*]', '_', name)
    # 移除控制字符
    name = re.sub(r'[\x00-\x1f\x7f-\x9f]', '', name)
    # 移除首尾空格和点
    name = name.strip('. ')
    # 限制长度
    if len(name) > 200:
        name = name[:200]
    return name or "unnamed"


def build_absolute_url(url: str, base_url: str = "") -> str:
    """将相对 URL 转换为绝对 URL。"""
    if not url:
        return ""
    if url.startswith(("http://", "https://")):
        return url
    if base_url:
        return urljoin(base_url, url)
    return url


def normalize_chapterlist_url(manga_url: str) -> str:
    """规范化漫画章节列表 URL。"""
    if not manga_url:
        return ""
    parsed = urlparse(manga_url)
    # 移除查询参数和片段
    normalized = f"{parsed.scheme}://{parsed.netloc}{parsed.path}"
    # 确保以 / 结尾
    if not normalized.endswith("/"):
        normalized += "/"
    return normalized


def unwrap_cover_url(cover_url: str) -> str:
    """解包封面 URL，处理重定向等情况。"""
    if not cover_url:
        return ""
    # 如果是相对 URL，返回空（需要 base_url）
    if cover_url.startswith("//"):
        return "https:" + cover_url
    if cover_url.startswith(("http://", "https://")):
        return cover_url
    return cover_url


def coerce_html_attr_to_str(value) -> str:
    """将 HTML 属性值转换为字符串。"""
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    if isinstance(value, (list, tuple)):
        return " ".join(str(v) for v in value)
    return str(value)
