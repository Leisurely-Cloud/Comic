"""章节目录命名约定。

下载中章节会写到 `.下载中_001_章节名` 临时目录，下载完成后原子重命名为 `001_章节名`。
这里集中放 1) 识别最终/临时章节目录名的正则 2) 扫描某个目录是否像漫画根目录的判定。
其他模块应当优先使用这些函数，避免同一套正则在各处漂移。
"""
from __future__ import annotations

import os
import re


_FINAL_CHAPTER_DIR_PATTERN = re.compile(r"^\d+_.+")
_TEMP_CHAPTER_DIR_PATTERN = re.compile(r"^\.下载中_\d+_.+")


def is_final_chapter_dir_name(name: str) -> bool:
    return bool(_FINAL_CHAPTER_DIR_PATTERN.match(name or ""))


def is_temp_chapter_dir_name(name: str) -> bool:
    return bool(_TEMP_CHAPTER_DIR_PATTERN.match(name or ""))


def looks_like_manga_download_dir(directory_path: str) -> bool:
    """目录下包含任一章节子目录（最终态或下载中）即判定为漫画根目录。"""
    try:
        for entry in os.scandir(directory_path):
            if entry.is_dir() and (
                is_final_chapter_dir_name(entry.name)
                or is_temp_chapter_dir_name(entry.name)
            ):
                return True
    except Exception:
        return False
    return False
