"""下载完成后的归档：整本 ZIP 与按章节 CBZ。

所有函数都是纯 I/O，不依赖 GUI 状态。GUI 层应当把 root_dir / manga_title / manga_url 等
必要参数显式传进来，避免隐藏依赖。
"""
from __future__ import annotations

import os
import xml.etree.ElementTree as ET
import zipfile
from typing import List, Tuple

from .chapter_naming import is_final_chapter_dir_name, is_temp_chapter_dir_name


_EXPORTABLE_IMAGE_EXTENSIONS = (".jpg", ".jpeg", ".png", ".webp")


def build_unique_archive_path(root_dir: str) -> str:
    """在漫画根目录的同级生成一个尚未占用的 ZIP 路径。"""
    parent_dir = os.path.dirname(root_dir.rstrip("\\/"))
    base_name = os.path.basename(root_dir.rstrip("\\/")) or "漫画下载"
    archive_path = os.path.join(parent_dir, f"{base_name}.zip")
    suffix = 2
    while os.path.exists(archive_path):
        archive_path = os.path.join(parent_dir, f"{base_name}_{suffix}.zip")
        suffix += 1
    return archive_path


def create_zip_archive_for_manga(root_dir: str) -> Tuple[str, int]:
    """把整个漫画目录压成 ZIP。跳过 `.下载中_*` 的临时目录，避免把未完成章节打进去。"""
    if not root_dir or not os.path.isdir(root_dir):
        raise FileNotFoundError("下载目录不存在，暂时无法创建压缩包。")

    archive_path = build_unique_archive_path(root_dir)
    parent_dir = os.path.dirname(root_dir.rstrip("\\/"))
    file_count = 0

    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for current_dir, dir_names, file_names in os.walk(root_dir):
            dir_names[:] = [
                dir_name for dir_name in sorted(dir_names)
                if not is_temp_chapter_dir_name(dir_name)
            ]
            relative_dir = os.path.relpath(current_dir, parent_dir).replace("\\", "/")
            if not dir_names and not file_names:
                archive.writestr(f"{relative_dir}/", "")
            for file_name in sorted(file_names):
                file_path = os.path.join(current_dir, file_name)
                archive_name = os.path.relpath(file_path, parent_dir).replace("\\", "/")
                archive.write(file_path, archive_name)
                file_count += 1

    return archive_path, file_count


def build_cbz_export_dir(root_dir: str) -> str:
    resolved_root_dir = root_dir.rstrip("\\/")
    if not resolved_root_dir:
        raise FileNotFoundError("下载目录不存在，暂时无法导出 CBZ。")

    parent_dir = os.path.dirname(resolved_root_dir)
    base_name = os.path.basename(resolved_root_dir) or "漫画下载"
    export_dir = os.path.join(parent_dir, f"{base_name}_CBZ")
    os.makedirs(export_dir, exist_ok=True)
    return export_dir


def list_exportable_image_files(chapter_dir: str) -> List[str]:
    image_files: List[str] = []
    try:
        for entry in os.scandir(chapter_dir):
            if entry.is_file() and entry.name.lower().endswith(_EXPORTABLE_IMAGE_EXTENSIONS):
                image_files.append(entry.path)
    except Exception:
        return []
    image_files.sort(key=lambda item: os.path.basename(item).lower())
    return image_files


def build_cbz_comicinfo_xml(
    manga_title: str,
    chapter_title: str,
    chapter_number,
    chapter_count,
    page_count,
    manga_url: str = "",
) -> bytes:
    root = ET.Element("ComicInfo")

    def add_text_node(tag_name, value):
        if value is None:
            return
        text = str(value).strip()
        if not text:
            return
        ET.SubElement(root, tag_name).text = text

    add_text_node("Series", manga_title or "漫画")
    add_text_node("Title", chapter_title or manga_title or "章节")
    add_text_node("Number", chapter_number)
    add_text_node("Count", chapter_count)
    add_text_node("PageCount", page_count)
    add_text_node("Manga", "YesAndRightToLeft")
    add_text_node("Web", manga_url)

    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


def create_cbz_archive_for_chapter(
    export_dir: str,
    chapter_dir_name: str,
    chapter_dir_path: str,
    manga_title: str,
    manga_url: str,
    chapter_number,
    chapter_count,
) -> Tuple[str, int]:
    image_files = list_exportable_image_files(chapter_dir_path)
    if not image_files:
        return "", 0

    chapter_title = chapter_dir_name.split("_", 1)[1] if "_" in chapter_dir_name else chapter_dir_name
    archive_path = os.path.join(export_dir, f"{chapter_dir_name}.cbz")

    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for image_path in image_files:
            archive.write(image_path, os.path.basename(image_path))
        archive.writestr(
            "ComicInfo.xml",
            build_cbz_comicinfo_xml(
                manga_title=manga_title,
                chapter_title=chapter_title,
                chapter_number=chapter_number,
                chapter_count=chapter_count,
                page_count=len(image_files),
                manga_url=manga_url,
            ),
        )

    return archive_path, len(image_files)


def export_manga_to_cbz(
    root_dir: str,
    manga_title: str,
    manga_url: str = "",
    progress_callback=None,
) -> Tuple[str, List[Tuple[str, int]], List[str]]:
    """遍历漫画根目录下的最终态章节目录，每个写出一个 CBZ。

    返回 (export_dir, exported_archives, skipped_chapters)：
    - exported_archives: [(cbz_path, image_count), ...]
    - skipped_chapters: 图片为空因而未产出 CBZ 的章节名

    progress_callback(chapter_index, total_chapters, chapter_name) 在每章完成后调用。
    """
    resolved_root_dir = (root_dir or "").strip()
    if not resolved_root_dir or not os.path.isdir(resolved_root_dir):
        raise FileNotFoundError("当前没有可导出的本地目录。")

    chapter_entries: List[Tuple[str, str]] = []
    try:
        for entry in os.scandir(resolved_root_dir):
            if entry.is_dir() and is_final_chapter_dir_name(entry.name):
                chapter_entries.append((entry.name, entry.path))
    except Exception as exc:
        raise RuntimeError(f"读取章节目录失败: {str(exc)}")

    chapter_entries.sort(key=lambda item: item[0])
    if not chapter_entries:
        raise RuntimeError("当前漫画目录里没有可导出的已完成章节。")

    export_dir = build_cbz_export_dir(resolved_root_dir)
    exported_archives: List[Tuple[str, int]] = []
    skipped_chapters: List[str] = []
    total_chapters = len(chapter_entries)

    for chapter_index, (chapter_dir_name, chapter_dir_path) in enumerate(chapter_entries, 1):
        archive_path, image_count = create_cbz_archive_for_chapter(
            export_dir=export_dir,
            chapter_dir_name=chapter_dir_name,
            chapter_dir_path=chapter_dir_path,
            manga_title=manga_title,
            manga_url=manga_url,
            chapter_number=chapter_index,
            chapter_count=total_chapters,
        )
        if archive_path:
            exported_archives.append((archive_path, image_count))
        else:
            skipped_chapters.append(chapter_dir_name)

        if progress_callback is not None:
            chapter_title = chapter_dir_name.split("_", 1)[1] if "_" in chapter_dir_name else chapter_dir_name
            progress_callback(chapter_index, total_chapters, chapter_title)

    if not exported_archives:
        raise RuntimeError("没有找到可写入 CBZ 的图片文件。")

    return export_dir, exported_archives, skipped_chapters
