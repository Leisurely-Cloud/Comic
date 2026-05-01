"""详情页右侧卡片面板：本地状态显示、打开目录、检查更新、导出 CBZ。

和 ProxyPanel 一样，这是个有状态的 UI 子系统：
- 自己拥有 detail_local_status_var / detail_local_path_var 两个 tk 变量
- 自己维护当前详情漫画的 root_dir / library_entry / title / url
- 自己维护 is_exporting_cbz / is_checking_library_updates 两个忙碌态
- 用 register_widgets 挂 open_dir / export_cbz / check_updates 三个按钮
- 通过 DetailCardPanelDeps 拿 GUI 提供的上下文（当前 adapter、saved_detail_cache、rank_cards 等）

GUI 侧保留一组 1 行 delegate：
- reset_detail_local_state / refresh_detail_local_action_buttons / update_detail_local_state 用于外部在合适时机触发刷新
- open_current_detail_root_dir / check_local_library_updates / export_current_detail_to_cbz 用于按钮 command= 绑定
"""
from __future__ import annotations

import os
import subprocess
import threading
import time
import tkinter as tk
from dataclasses import dataclass
from tkinter import messagebox
from typing import Any, Callable, Dict, List, Optional

from local_library import (
    build_local_library_entry_from_fallback,
    compute_update_available_count,
    enrich_local_library_entry_identity,
    format_local_library_status,
    get_library_update_status_lines,
    iter_local_library_entries,
    load_manga_library_metadata,
    save_library_entry_metadata,
)
from chapter_naming import looks_like_manga_download_dir
from site_adapters import resolve_adapter_from_url


@dataclass
class DetailCardPanelDeps:
    # 只读上下文
    get_current_adapter: Callable[[], Any]
    get_saved_detail_cache: Callable[[], Dict[str, Any]]
    get_rank_cards: Callable[[], List[Any]]
    is_in_local_library_section: Callable[[], bool]
    get_library_search_roots: Callable[[], List[str]]
    # GUI 端动作
    find_legacy_manga_root_dir: Callable[[Any, str], Optional[str]]  # manhuagui 遗留目录查找
    cache_manga_detail: Callable[[Any, str, Any], None]
    apply_manual_proxy_settings: Callable[[], bool]  # 等价于 show_feedback=False 调用
    set_ranking_buttons_state: Callable[[bool], None]
    refresh_rankings: Callable[[], None]
    # 基础设施
    log: Callable[[str, str], None]
    status: Callable[[str], None]
    run_on_ui_thread: Callable[..., None]
    is_closing: Callable[[], bool] = lambda: False


class DetailCardPanel:
    def __init__(self, *, deps: DetailCardPanelDeps):
        self._deps = deps

        # tk 变量：GUI 用别名（self.detail_local_status_var = panel.detail_local_status_var）
        # 绑到现有 Label 的 textvariable=
        self.detail_local_status_var = tk.StringVar(value="本地状态: 未检测")
        self.detail_local_path_var = tk.StringVar(value="")

        # 当前详情卡片的本地联动状态
        self.current_detail_root_dir = ""
        self.current_detail_library_entry: Optional[Dict[str, Any]] = None
        self.current_detail_title = ""
        self.current_detail_url = ""

        # 忙碌态
        self.is_exporting_cbz = False
        self.is_checking_library_updates = False

        # 按钮注册
        self._widgets: Dict[str, Any] = {}

    # --- widget 注册 ---
    def register_widgets(self, **widgets):
        self._widgets.update({k: v for k, v in widgets.items() if v is not None})

    def _widget(self, name):
        return self._widgets.get(name)

    # --- 对外：设置当前详情上下文 ---
    def set_current_detail_context(self, title: str, url: str):
        """GUI 的 update_ranking_detail 选中某卡片时同步调用。"""
        self.current_detail_title = str(title or "")
        self.current_detail_url = str(url or "")

    # --- UI 刷新 ---
    def reset(self, status_text: str = "本地状态: 未检测", path_text: str = ""):
        self.current_detail_root_dir = ""
        self.current_detail_library_entry = None

        def apply():
            if self._deps.is_closing():
                return
            self.detail_local_status_var.set(status_text)
            self.detail_local_path_var.set(path_text)
            self.refresh_action_buttons()

        self._deps.run_on_ui_thread(apply)

    def refresh_action_buttons(self):
        if self._deps.is_closing():
            return
        has_root_dir = bool(self.current_detail_root_dir and os.path.isdir(self.current_detail_root_dir))

        open_btn = self._widget("open_dir_btn")
        if open_btn is not None:
            open_btn.config(state=tk.NORMAL if has_root_dir else tk.DISABLED)

        export_btn = self._widget("export_cbz_btn")
        if export_btn is not None:
            export_btn.config(
                state=tk.DISABLED if (not has_root_dir or self.is_exporting_cbz) else tk.NORMAL,
                text="导出中..." if self.is_exporting_cbz else "导出 CBZ",
            )

    def update_for_card(self, card):
        if not card:
            self.reset(status_text="本地状态: 未检测", path_text="")
            return

        entry = self._get_entry_for_card(card)
        if not entry:
            self.reset(status_text="本地状态: 未发现本地下载", path_text="")
            return

        root_dir = (entry.get("root_dir") or "").strip()
        saved_at = str(entry.get("saved_at") or entry.get("created_at") or "").strip()
        path_lines = get_library_update_status_lines(entry, include_error=True)
        if root_dir:
            path_lines.append(f"本地目录: {root_dir}")
        if saved_at:
            path_lines.append(f"最近保存: {saved_at}")

        self.current_detail_root_dir = root_dir if os.path.isdir(root_dir) else ""
        self.current_detail_library_entry = dict(entry)

        def apply():
            if self._deps.is_closing():
                return
            self.detail_local_status_var.set(format_local_library_status(entry))
            self.detail_local_path_var.set("\n".join(path_lines))
            self.refresh_action_buttons()

        self._deps.run_on_ui_thread(apply)

    # --- 本地 entry 查找（GUI 也复用，通过 delegate 暴露）---
    def get_entry_by_root(self, root_dir: str, site_key: str = "") -> Optional[Dict[str, Any]]:
        resolved_root_dir = (root_dir or "").strip()
        if not resolved_root_dir or not os.path.isdir(resolved_root_dir):
            return None

        if not looks_like_manga_download_dir(resolved_root_dir):
            return None

        cache = self._deps.get_saved_detail_cache()
        current_adapter = self._deps.get_current_adapter()
        default_site_name = current_adapter.display_name if current_adapter else ""

        metadata = load_manga_library_metadata(resolved_root_dir)
        metadata_site_key = (metadata.get("site_key") or "").strip() if metadata else ""
        if site_key and metadata_site_key and metadata_site_key != site_key:
            return None

        fallback_site_key = metadata_site_key or site_key
        fallback = build_local_library_entry_from_fallback(
            resolved_root_dir, saved_detail_cache=cache, site_key=fallback_site_key,
        )
        if metadata:
            if fallback is not None:
                metadata["site_key"] = metadata_site_key or fallback.get("site_key") or ""
                metadata["site_name"] = metadata.get("site_name") or fallback.get("site_name") or default_site_name
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
                fallback_for_site = build_local_library_entry_from_fallback(
                    resolved_root_dir, saved_detail_cache=cache, site_key=site_key,
                )
                if fallback_for_site is None:
                    return None
                metadata["site_key"] = fallback_for_site.get("site_key") or metadata.get("site_key") or ""
                metadata["site_name"] = fallback_for_site.get("site_name") or metadata.get("site_name") or default_site_name
            return enrich_local_library_entry_identity(metadata, saved_detail_cache=cache, preferred_site_key=site_key)

        return enrich_local_library_entry_identity(fallback, saved_detail_cache=cache, preferred_site_key=site_key)

    def find_entry_by_source_url(self, adapter, source_url: str) -> Optional[Dict[str, Any]]:
        normalized_url = (source_url or "").strip()
        if not normalized_url:
            return None

        normalized_url_no_slash = normalized_url.rstrip("/")
        target_cache_keys = {
            adapter.get_manga_cache_key(normalized_url),
            adapter.get_manga_cache_key(normalized_url_no_slash),
        }

        cache = self._deps.get_saved_detail_cache()
        current_adapter = self._deps.get_current_adapter()
        default_site_name = current_adapter.display_name if current_adapter else ""
        library_entries = iter_local_library_entries(
            library_search_roots=self._deps.get_library_search_roots(),
            saved_detail_cache=cache,
            site_key=adapter.key,
            default_site_display_name=default_site_name,
        )
        for entry in library_entries:
            entry_url = (entry.get("manga_url") or "").strip()
            if not entry_url:
                continue
            entry_url_no_slash = entry_url.rstrip("/")
            entry_cache_keys = {
                adapter.get_manga_cache_key(entry_url),
                adapter.get_manga_cache_key(entry_url_no_slash),
            }
            if target_cache_keys & entry_cache_keys:
                return entry

        root_dir = self._deps.find_legacy_manga_root_dir(adapter, normalized_url)
        if root_dir:
            return self.get_entry_by_root(root_dir, site_key=adapter.key)
        return None

    def _get_entry_for_card(self, card) -> Optional[Dict[str, Any]]:
        if not card:
            return None

        inline_entry = getattr(card, "local_library_entry", None)
        if isinstance(inline_entry, dict) and inline_entry.get("root_dir"):
            return dict(inline_entry)

        local_root_dir = (getattr(card, "local_root_dir", "") or "").strip()
        local_site_key = (getattr(card, "local_site_key", "") or "").strip()
        if local_root_dir:
            entry = self.get_entry_by_root(local_root_dir, site_key=local_site_key)
            if entry is not None:
                return entry

        source_url = (getattr(card, "manga_url", "") or "").strip()
        if not source_url:
            return None

        current_adapter = self._deps.get_current_adapter()
        fallback_key = current_adapter.key if current_adapter else None
        adapter = resolve_adapter_from_url(source_url, fallback_key=fallback_key)
        return self.find_entry_by_source_url(adapter, source_url)

    # --- 按钮：打开本地目录 ---
    def open_dir(self):
        root_dir = (self.current_detail_root_dir or "").strip()
        if not root_dir or not os.path.isdir(root_dir):
            self.reset(status_text="本地状态: 目录不可用", path_text="")
            self._deps.run_on_ui_thread(messagebox.showwarning, "提示", "当前没有可打开的本地目录。")
            return

        try:
            if hasattr(os, "startfile"):
                os.startfile(root_dir)
            else:
                subprocess.Popen(["explorer", root_dir])
            self._deps.log(f"📂 已打开本地目录: {root_dir}", "info")
            self._deps.status("已打开本地目录")
        except Exception as exc:
            self._deps.log(f"打开本地目录失败: {str(exc)}", "warning")
            self._deps.run_on_ui_thread(messagebox.showwarning, "打开失败", str(exc))

    # --- 按钮：检查本地库更新 ---
    @staticmethod
    def _build_checked_entry(entry: Dict[str, Any], adapter, detail) -> Dict[str, Any]:
        now_text = time.strftime("%Y-%m-%d %H:%M:%S")
        updated_entry = dict(entry or {})
        online_total = int(getattr(detail, "chapter_count", 0) or updated_entry.get("total_chapters") or 0)
        update_available_count = compute_update_available_count(updated_entry, online_total)

        updated_entry["schema_version"] = max(int(updated_entry.get("schema_version") or 1), 1)
        updated_entry["site_key"] = adapter.key
        updated_entry["site_name"] = adapter.display_name
        updated_entry["manga_title"] = str(getattr(detail, "title", "") or updated_entry.get("manga_title") or "本地漫画")
        updated_entry["manga_url"] = (updated_entry.get("manga_url") or getattr(detail, "manga_url", "") or "").strip()
        updated_entry["cover_url"] = str(getattr(detail, "cover_url", "") or updated_entry.get("cover_url") or "")
        updated_entry["total_chapters"] = max(online_total, int(updated_entry.get("downloaded_chapter_count") or 0))
        updated_entry["latest_known_chapter_title"] = str(
            getattr(detail, "latest_chapter", "") or updated_entry.get("latest_known_chapter_title") or updated_entry.get("last_downloaded_chapter_title") or "-"
        )
        updated_entry["latest_known_update_time"] = str(
            getattr(detail, "update_time", "") or updated_entry.get("latest_known_update_time") or "-"
        )
        updated_entry["update_available_count"] = update_available_count
        updated_entry["update_last_checked_at"] = now_text
        updated_entry["update_last_error"] = ""
        if online_total > 0 and update_available_count > 0:
            updated_entry["update_check_status"] = f"发现 {update_available_count} 章可更新"
        elif online_total > 0:
            updated_entry["update_check_status"] = "已是最新"
        else:
            updated_entry["update_check_status"] = "检查完成，但未获取到有效章节数"
        return updated_entry

    @staticmethod
    def _build_failed_entry(entry: Dict[str, Any], status_text: str, error_message: str = "") -> Dict[str, Any]:
        now_text = time.strftime("%Y-%m-%d %H:%M:%S")
        updated_entry = dict(entry or {})
        updated_entry["schema_version"] = max(int(updated_entry.get("schema_version") or 1), 1)
        updated_entry["update_check_status"] = str(status_text or "检查失败")
        updated_entry["update_available_count"] = 0
        updated_entry["update_last_checked_at"] = now_text
        updated_entry["update_last_error"] = str(error_message or "")
        return updated_entry

    def _save_entry(self, entry: Dict[str, Any]):
        save_library_entry_metadata(
            entry,
            on_error=lambda exc: self._deps.log(f"保存本地漫画元数据失败: {str(exc)}", "warning"),
        )

    def check_updates(self):
        if self.is_checking_library_updates:
            return

        if not self._deps.is_in_local_library_section():
            self._deps.run_on_ui_thread(messagebox.showwarning, "提示", "请先切到“本地已下载”分区。")
            return

        if not self._deps.apply_manual_proxy_settings():
            return

        cards = list(self._deps.get_rank_cards())
        if not cards:
            self._deps.run_on_ui_thread(messagebox.showwarning, "提示", "当前页没有可检查更新的本地漫画。")
            return

        entries = []
        seen_root_dirs = set()
        for card in cards:
            entry = self._get_entry_for_card(card)
            if not entry:
                continue
            root_dir = (entry.get("root_dir") or "").strip()
            if not root_dir or root_dir in seen_root_dirs:
                continue
            seen_root_dirs.add(root_dir)
            entries.append(entry)

        if not entries:
            self._deps.run_on_ui_thread(messagebox.showwarning, "提示", "当前页没有可检查更新的本地漫画。")
            return

        self.is_checking_library_updates = True
        self._deps.run_on_ui_thread(self._deps.set_ranking_buttons_state, False)
        self._deps.log(f"🔄 正在检查本地漫画更新: 当前页共 {len(entries)} 部", "info")
        self._deps.status("正在检查本地漫画更新...")

        current_adapter = self._deps.get_current_adapter()

        def worker():
            updatable_count = 0
            latest_count = 0
            skipped_count = 0
            failed_count = 0

            try:
                for index, entry in enumerate(entries, 1):
                    manga_title = str(entry.get("manga_title") or f"本地漫画{index}")
                    manga_url = (entry.get("manga_url") or "").strip()
                    site_key = (entry.get("site_key") or (current_adapter.key if current_adapter else "")).strip()

                    if not manga_url:
                        skipped_count += 1
                        skipped_entry = self._build_failed_entry(entry, "缺少原始链接，无法检查更新")
                        self._save_entry(skipped_entry)
                        self._deps.log(
                            f"⚠️ [{index}/{len(entries)}] {manga_title}: 缺少原始链接，已跳过",
                            "warning",
                        )
                        continue

                    try:
                        fallback_key = site_key or (current_adapter.key if current_adapter else None)
                        adapter = resolve_adapter_from_url(manga_url, fallback_key=fallback_key)
                        detail = adapter.fetch_manga_detail(manga_url)
                        self._deps.cache_manga_detail(adapter, manga_url, detail)
                        checked_entry = self._build_checked_entry(entry, adapter, detail)
                        self._save_entry(checked_entry)

                        update_available_count = int(checked_entry.get("update_available_count") or 0)
                        if update_available_count > 0:
                            updatable_count += 1
                            self._deps.log(
                                f"🆕 [{index}/{len(entries)}] {manga_title}: 发现 {update_available_count} 章可更新",
                                "warning",
                            )
                        else:
                            latest_count += 1
                            self._deps.log(
                                f"✅ [{index}/{len(entries)}] {manga_title}: 已是最新",
                                "info",
                            )
                    except Exception as exc:
                        failed_count += 1
                        failed_entry = self._build_failed_entry(entry, "检查失败", str(exc))
                        self._save_entry(failed_entry)
                        self._deps.log(
                            f"❌ [{index}/{len(entries)}] {manga_title}: 检查更新失败: {str(exc)}",
                            "error",
                        )
            finally:
                self.is_checking_library_updates = False

            summary = (
                f"检查完成: 可更新 {updatable_count} 部，"
                f"已最新 {latest_count} 部，"
                f"跳过 {skipped_count} 部，"
                f"失败 {failed_count} 部"
            )
            self._deps.log(f"📚 {summary}", "info")
            self._deps.status(summary)
            self._deps.run_on_ui_thread(self._deps.set_ranking_buttons_state, False)
            self._deps.run_on_ui_thread(self._deps.refresh_rankings)

        threading.Thread(target=worker, daemon=True).start()

    # --- 按钮：导出当前详情为 CBZ ---
    def export_cbz(self):
        if self.is_exporting_cbz:
            return

        root_dir = (self.current_detail_root_dir or "").strip()
        if not root_dir or not os.path.isdir(root_dir):
            self.reset(status_text="本地状态: 目录不可用", path_text="")
            self._deps.run_on_ui_thread(messagebox.showwarning, "提示", "当前没有可导出的本地目录。")
            return

        entry = dict(self.current_detail_library_entry or {})
        manga_title = str(entry.get("manga_title") or self.current_detail_title or os.path.basename(root_dir.rstrip("\\/")) or "本地漫画")
        manga_url = str(entry.get("manga_url") or self.current_detail_url or "")

        self.is_exporting_cbz = True
        self._deps.run_on_ui_thread(self.refresh_action_buttons)
        self._deps.log(f"📚 正在导出 CBZ: {manga_title}", "info")
        self._deps.status("正在导出 CBZ...")

        # 局部 import 避免顶部 import archive 导致循环（archive 依赖 chapter_naming，chapter_naming 干净）
        from archive import export_manga_to_cbz

        def worker():
            try:
                export_dir, exported_archives, skipped_chapters = export_manga_to_cbz(
                    root_dir=root_dir,
                    manga_title=manga_title,
                    manga_url=manga_url,
                )
                total_pages = sum(image_count for _, image_count in exported_archives)
                self._deps.log(f"✅ CBZ 导出完成: {export_dir}", "success")
                self._deps.log(
                    f"📚 共导出 {len(exported_archives)} 个章节 CBZ，写入 {total_pages} 张图片",
                    "info",
                )
                if skipped_chapters:
                    self._deps.log(
                        f"⚠️ 以下章节因未发现图片而跳过: {', '.join(skipped_chapters[:5])}"
                        + (" ..." if len(skipped_chapters) > 5 else ""),
                        "warning",
                    )
                self._deps.status("CBZ 导出完成")
                self._deps.run_on_ui_thread(
                    messagebox.showinfo,
                    "导出完成",
                    f"已导出 {len(exported_archives)} 个 CBZ 文件：\n{export_dir}",
                )
            except Exception as exc:
                self._deps.log(f"❌ 导出 CBZ 失败: {str(exc)}", "error")
                self._deps.status("导出 CBZ 失败")
                self._deps.run_on_ui_thread(
                    messagebox.showwarning,
                    "导出失败",
                    str(exc),
                )
            finally:
                self.is_exporting_cbz = False
                self._deps.run_on_ui_thread(self.refresh_action_buttons)

        threading.Thread(target=worker, daemon=True).start()
