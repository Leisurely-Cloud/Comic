"""下载编排控制器：状态机 + 并发调度 + 持久化。

从 ComicDownloaderGUI 提取的下载核心逻辑，通过 DownloadControllerDeps 回调 GUI。
GUI 保留按钮/进度条/对话框等纯 UI 代码，通过 1 行 delegate 转发下载操作。

控制器持有：
- 下载状态机：is_downloading / is_paused / stop_event / pause_event
- 线程管理：executor / current_thread
- 活跃下载上下文：active_download_url / active_download_root_dir / active_manga_title / active_download_metadata
- 断点续传：resume_data_file / legacy_resume_data_file
- 失败重试计划：failed_retry_plan
"""
from __future__ import annotations

import json
import os
import threading
import time
from concurrent.futures import ThreadPoolExecutor, FIRST_COMPLETED, wait
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any, Callable, Dict, List, Optional

from downcomic import sanitize_filename, proxy_pool
from local_library import (
    build_downloaded_chapter_records_from_disk,
    compact_chapter_info,
    get_manga_metadata_path,
)
from resume_state import (
    build_failed_chapter_record,
    format_failed_chapter_list_text,
    get_failed_chapter_numbers_text,
    match_retry_chapters,
    normalize_failed_retry_records,
)
from site_adapters import DEFAULT_SITE_KEY, MangaDetail, get_adapter, resolve_adapter_from_url
from archive import create_zip_archive_for_manga


def format_updated_at(value: Any) -> str:
    if not value:
        return "-"
    try:
        dt = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        return dt.strftime("%Y-%m-%d %H:%M")
    except Exception:
        return str(value)


@dataclass
class DownloadControllerDeps:
    """DownloadController 需要问 GUI 的所有东西。"""
    # 日志 / 状态栏
    log: Callable[[str, str], None]
    status: Callable[[str], None]
    run_on_ui_thread: Callable[..., None]

    # 适配器 / URL / 代理
    get_selected_adapter: Callable[[], Any]
    set_active_adapter: Callable[[str], None]
    get_download_url: Callable[[], str]
    set_download_url: Callable[[str], None]
    get_selected_card_url: Callable[[], str]
    apply_proxy_settings: Callable[[bool], bool]
    refresh_proxy_controls: Callable[[], None]
    get_proxy_enabled: Callable[[], bool]

    # 封面查询
    get_known_cover_url: Callable[[Any, str], str]

    # UI 更新
    set_progress: Callable[[float], None]
    set_progress_style: Callable[[str], None]
    update_control_buttons: Callable[[bool, bool], None]
    set_failed_retry_plan: Callable[[Any], None]

    # 下载完成回调
    download_complete_ui: Callable[[Dict[str, Any]], None]

    # 窗口引用（对话框用）
    root: Any

    # 断点续传确认
    ask_resume_confirmation: Callable[[Any, Dict[str, Any]], bool]

    # 打包确认
    offer_archive: Callable[[Dict[str, Any]], None]

    # 工作目录
    get_download_workspace_dir: Callable[[], str]

    # 并发 / 起始章节
    get_start_chapter_order: Callable[[], int]
    set_start_chapter_order: Callable[[int], None]
    get_concurrent_limit: Callable[[], int]
    get_image_concurrent_limit: Callable[[], int]

    # 心跳
    start_heartbeat: Callable[..., threading.Event]
    stop_heartbeat: Callable[[threading.Event], None]

    # 缓存漫画详情
    cache_manga_detail: Callable[[Any, str, Any], None]
    get_cached_manga_detail: Callable[[Any, str], Any]
    find_library_entry_by_source_url: Callable[[Any, str], Any]

    # 站点错误处理
    is_site_access_blocked_error: Callable[[Any], bool]
    is_site_unreachable_error: Callable[[Any], bool]
    handle_site_access_blocked: Callable[[str, Any], None]
    handle_site_unreachable: Callable[[Any, Any], None]


class DownloadController:
    def __init__(self, *, deps: DownloadControllerDeps,
                 resume_data_file: str, legacy_resume_data_file: str):
        self._deps = deps
        self.resume_data_file = resume_data_file
        self.legacy_resume_data_file = legacy_resume_data_file

        # 下载状态机
        self.is_downloading = False
        self.is_paused = False
        self.stop_event = threading.Event()
        self.pause_event = threading.Event()
        self.pause_event.set()

        # 线程管理
        self.executor: Optional[ThreadPoolExecutor] = None
        self.current_thread: Optional[threading.Thread] = None

        # 活跃下载上下文
        self.active_download_url = ""
        self.active_download_root_dir = ""
        self.active_manga_title = ""
        self.active_download_metadata: Optional[Dict[str, Any]] = None
        self.download_site_key: str = DEFAULT_SITE_KEY

        # 失败重试计划
        self.failed_retry_plan: Optional[Dict[str, Any]] = None

    # --- 失败重试计划 ---
    def set_failed_retry_plan(self, plan: Optional[Dict[str, Any]]):
        if plan:
            normalized_plan = dict(plan)
            normalized_plan["failed_chapter_records"] = normalize_failed_retry_records(
                normalized_plan.get("failed_chapter_records") or []
            )
            self.failed_retry_plan = normalized_plan
        else:
            self.failed_retry_plan = None
        self._deps.set_failed_retry_plan(self.failed_retry_plan)

    def retry_failed_chapters(self):
        plan = dict(self.failed_retry_plan or {})
        failed_records = normalize_failed_retry_records(plan.get("failed_chapter_records") or [])
        retry_url = (plan.get("source_url") or plan.get("url") or "").strip()

        if not retry_url or not failed_records:
            self.set_failed_retry_plan(None)
            from tkinter import messagebox
            messagebox.showinfo("提示", "当前没有可重试的失败章节。")
            return

        self._deps.log(f"🔁 开始重试失败章节: {format_failed_chapter_list_text(failed_records)}", "info")
        self.start_download(url=retry_url, retry_plan=plan)

    # --- 下载入口 ---
    def start_download(self, url: Optional[str] = None, retry_plan: Optional[Dict[str, Any]] = None):
        from tkinter import messagebox

        retry_plan = dict(retry_plan or {})
        retry_failed_records = normalize_failed_retry_records(
            retry_plan.get("failed_chapter_records") or []
        )
        if retry_failed_records and not url:
            url = (retry_plan.get("source_url") or retry_plan.get("url") or "").strip()

        if not url:
            url = (self._deps.get_download_url() or "").strip()
            if not url:
                url = self._deps.get_selected_card_url() or ""

        if not url:
            messagebox.showwarning("警告", "请先输入漫画链接，或在首页列表中选择一部漫画")
            return

        adapter = resolve_adapter_from_url(url, fallback_key=self._deps.get_selected_adapter().key)
        if not adapter.matches_url(url):
            from site_adapters import get_site_display_names
            supported_sites = "、".join(get_site_display_names())
            messagebox.showwarning("警告", f"暂时无法识别该链接所属站点。当前已接入站点: {supported_sites}")
            return

        if not adapter.supports_download:
            messagebox.showinfo(
                "提示",
                f"{adapter.display_name} 已预留接入入口，但下载逻辑还没完成。\n\n"
                "你现在可以继续收集这个站的章节接口和图片规则，后面我们再把它补进去。"
            )
            return

        self.download_site_key = adapter.key
        self._deps.set_active_adapter(adapter.key)
        if not self._deps.apply_proxy_settings(show_feedback=False):
            self.active_download_url = ""
            return
        if retry_plan and not retry_failed_records:
            messagebox.showwarning("警告", "当前没有可重试的失败章节。")
            return
        if not retry_plan:
            self.set_failed_retry_plan(None)
        self.active_download_url = url
        self.active_download_metadata = None
        self._deps.set_download_url(url)

        self.stop_event.clear()
        self.pause_event.set()
        self.is_paused = False

        self.is_downloading = True
        self._deps.update_control_buttons(downloading=True, paused=False)
        self._deps.set_progress_style('Download.Horizontal.TProgressbar')
        self._deps.set_progress(0)
        self._deps.status("准备开始下载...")

        proxy_pool.enabled = self._deps.get_proxy_enabled()
        self._deps.refresh_proxy_controls()

        self.current_thread = threading.Thread(
            target=self.download_manga,
            args=(url, adapter.key, retry_plan),
        )
        self.current_thread.daemon = True
        self.current_thread.start()

    # --- 下载控制 ---
    def stop_download(self):
        self.is_downloading = False
        self.stop_event.set()
        self._deps.log("正在停止下载...", "info")
        if self.download_site_key == "manhuagui":
            self._deps.log(
                "漫画柜当前若正在请求网页，通常要等这次网络请求超时后才会完全停止。", "warning"
            )
        self._deps.status("正在停止...")
        self._deps.set_progress_style('Danger.Horizontal.TProgressbar')

        if self.executor:
            try:
                self.executor.shutdown(wait=False, cancel_futures=True)
                self._deps.log("已强制停止所有下载线程", "info")
            except Exception as e:
                self._deps.log(f"停止线程时出错: {str(e)}", "error")

    def pause_download(self):
        if self.is_downloading and not self.is_paused:
            self.is_paused = True
            self.pause_event.clear()
            self._deps.log("⏸️ 已暂停派发新的章节下载任务", "info")
            self._deps.log(
                "ℹ️ 已提交的章节/图片任务会继续执行，待当前运行任务完成后将不再派发新任务。", "info"
            )
            self._deps.status("已暂停派发新任务")
            self._deps.set_progress_style('Warning.Horizontal.TProgressbar')
            self._deps.update_control_buttons(downloading=True, paused=True)

    def resume_download(self):
        if self.is_downloading and self.is_paused:
            self.is_paused = False
            self.pause_event.set()
            self._deps.log("▶️ 已恢复派发新的章节下载任务", "info")
            self._deps.status("下载中...")
            self._deps.set_progress_style('Download.Horizontal.TProgressbar')
            self._deps.update_control_buttons(downloading=True, paused=False)

    # --- 断点续传状态持久化 ---
    def save_download_state(self, current_chapter_order: int, total_chapters: int):
        try:
            state_url = (self.active_download_url or "").strip()
            if not state_url:
                return
            state_data = {
                'state_version': 2,
                'site_key': self.download_site_key,
                'url': state_url,
                'current_chapter_order': current_chapter_order + 2,
                'total_chapters': total_chapters,
                'timestamp': time.strftime("%Y-%m-%d %H:%M:%S"),
                'manga_title': self.active_manga_title,
                'root_dir': self.active_download_root_dir,
            }
            with open(self.resume_data_file, 'w', encoding='utf-8') as f:
                json.dump(state_data, f, ensure_ascii=False, indent=2)
        except Exception as e:
            self._deps.log(f"保存下载状态时出错: {str(e)}", "warning")

    def load_download_state(self) -> Optional[Dict[str, Any]]:
        try:
            candidate_files = [self.resume_data_file]
            if self.legacy_resume_data_file not in candidate_files:
                candidate_files.append(self.legacy_resume_data_file)

            for candidate_file in candidate_files:
                if not os.path.exists(candidate_file):
                    continue
                with open(candidate_file, 'r', encoding='utf-8') as f:
                    state = json.load(f)
                    if isinstance(state, dict):
                        normalized = False
                        version = int(state.get("state_version") or 1)
                        current_order = state.get("current_chapter_order")
                        if version < 2 and isinstance(current_order, int) and current_order > 0:
                            state["current_chapter_order"] = current_order + 1
                            state["state_version"] = 2
                            normalized = True
                        if normalized:
                            try:
                                with open(self.resume_data_file, 'w', encoding='utf-8') as wf:
                                    json.dump(state, wf, ensure_ascii=False, indent=2)
                            except Exception:
                                pass
                        return state
        except Exception as e:
            self._deps.log(f"加载下载状态时出错: {str(e)}", "warning")
        return None

    def clear_download_state(self):
        try:
            for candidate_file in {self.resume_data_file, self.legacy_resume_data_file}:
                if os.path.exists(candidate_file):
                    os.remove(candidate_file)
        except Exception as e:
            self._deps.log(f"清除下载状态时出错: {str(e)}", "warning")

    # --- 断点续传检查 ---
    def check_resume_download_on_startup(self):
        self.check_resume_download()

    def check_resume_download(self) -> bool:
        state = self.load_download_state()
        if state:
            resume_adapter = get_adapter(state.get('site_key', DEFAULT_SITE_KEY))
            result = self._deps.ask_resume_confirmation(resume_adapter, state)
            if result:
                self.download_site_key = resume_adapter.key
                self._deps.set_active_adapter(resume_adapter.key)
                self._deps.set_download_url(state['url'])
                # start_var 通过 GUI 的 delegate 间接设置
                self._deps.log(f"已恢复下载任务，从第{state['current_chapter_order']}章开始", "info")
                return True
        return False

    # --- 活跃下载元数据 ---
    def build_active_download_metadata(self, adapter, source_url, manga_title, root_dir,
                                       all_chapters, start_order, start_chapter_title) -> Dict[str, Any]:
        known_chapters = [compact_chapter_info(chapter) for chapter in all_chapters]
        downloaded_chapters = build_downloaded_chapter_records_from_disk(root_dir, known_chapters)
        latest_known = known_chapters[-1] if known_chapters else {}
        now_text = time.strftime("%Y-%m-%d %H:%M:%S")
        cover_url = self._deps.get_known_cover_url(adapter, source_url)
        last_downloaded = downloaded_chapters[-1] if downloaded_chapters else {}

        return {
            "schema_version": 1,
            "site_key": adapter.key,
            "site_name": adapter.display_name,
            "manga_title": str(manga_title or ""),
            "manga_url": (source_url or "").strip(),
            "root_dir": root_dir,
            "cover_url": cover_url,
            "total_chapters": len(known_chapters),
            "start_chapter_order": max(int(start_order) + 1, 1),
            "start_chapter_title": start_chapter_title or "",
            "latest_known_chapter_title": latest_known.get("title") or "-",
            "latest_known_update_time": format_updated_at(latest_known.get("updated_at")),
            "downloaded_chapter_count": len(downloaded_chapters),
            "last_downloaded_chapter_title": last_downloaded.get("title") or "",
            "last_downloaded_chapter_order": last_downloaded.get("order"),
            "downloaded_chapters": downloaded_chapters,
            "completed": False,
            "last_failed_chapter_count": 0,
            "last_failed_chapter_numbers_text": "",
            "last_failed_chapter_records": [],
            "last_download_final_state": "",
            "update_check_status": "",
            "update_available_count": 0,
            "update_last_checked_at": "",
            "update_last_error": "",
            "created_at": now_text,
            "saved_at": now_text,
            "_known_chapters": known_chapters,
        }

    def save_active_download_metadata(self, mark_completed=False,
                                      failed_chapter_records=None, final_state=""):
        metadata = self.active_download_metadata
        if not metadata:
            return

        root_dir = (metadata.get("root_dir") or "").strip()
        if not root_dir:
            return

        try:
            os.makedirs(root_dir, exist_ok=True)
        except Exception:
            return

        known_chapters = metadata.get("_known_chapters") or []
        downloaded_chapters = build_downloaded_chapter_records_from_disk(root_dir, known_chapters)
        last_downloaded = downloaded_chapters[-1] if downloaded_chapters else {}

        metadata["downloaded_chapters"] = downloaded_chapters
        metadata["downloaded_chapter_count"] = len(downloaded_chapters)
        metadata["last_downloaded_chapter_title"] = last_downloaded.get("title") or ""
        metadata["last_downloaded_chapter_order"] = last_downloaded.get("order")
        metadata["saved_at"] = time.strftime("%Y-%m-%d %H:%M:%S")
        if failed_chapter_records is not None:
            normalized_failed_records = normalize_failed_retry_records(failed_chapter_records)
            metadata["last_failed_chapter_records"] = normalized_failed_records
            metadata["last_failed_chapter_count"] = len(normalized_failed_records)
            metadata["last_failed_chapter_numbers_text"] = get_failed_chapter_numbers_text(
                normalized_failed_records
            )
        if final_state:
            metadata["last_download_final_state"] = str(final_state)
            metadata["completed"] = final_state == "completed"
        else:
            metadata["completed"] = bool(mark_completed or metadata.get("completed"))

        payload = {key: value for key, value in metadata.items() if not key.startswith("_")}
        try:
            with open(get_manga_metadata_path(root_dir), "w", encoding="utf-8") as file_obj:
                json.dump(payload, file_obj, ensure_ascii=False, indent=2)
        except Exception as exc:
            self._deps.log(f"保存本地漫画元数据失败: {str(exc)}", "warning")

    # --- 本地目录查找 / 离线详情 ---
    def find_local_manga_root_dir(self, adapter, source_url):
        if adapter.key != "manhuagui":
            return None

        source_cache_key = adapter.get_manga_cache_key(source_url)
        active_url = (self.active_download_url or "").strip()
        if (
            self.active_download_root_dir
            and active_url
            and source_cache_key == adapter.get_manga_cache_key(active_url)
            and os.path.isdir(self.active_download_root_dir)
        ):
            return self.active_download_root_dir

        resume_state = self.load_download_state() or {}
        resume_url = (resume_state.get("url") or "").strip()
        resume_matches = (
            resume_state.get("site_key") == adapter.key
            and resume_url
            and adapter.get_manga_cache_key(resume_url) == source_cache_key
        )

        root_dir = resume_state.get("root_dir") or ""
        if resume_matches and root_dir and os.path.isdir(root_dir):
            return root_dir

        candidate_roots = self._deps.get_library_search_roots()
        if resume_matches:
            from chapter_naming import looks_like_manga_download_dir
            from local_library import get_library_scan_excluded_dirs

            resume_title = (resume_state.get("manga_title") or "").strip()
            if resume_title:
                for candidate_root in candidate_roots:
                    resume_dir = os.path.join(candidate_root, sanitize_filename(resume_title))
                    if os.path.isdir(resume_dir) and looks_like_manga_download_dir(resume_dir):
                        return resume_dir

            resume_dt = parse_resume_timestamp(resume_state.get("timestamp"))
            if resume_dt is not None:
                candidates = []
                excluded_dirs = get_library_scan_excluded_dirs()
                for candidate_root in candidate_roots:
                    try:
                        for entry in os.scandir(candidate_root):
                            if not entry.is_dir():
                                continue
                            if entry.name in excluded_dirs or entry.name.startswith("."):
                                continue
                            if not looks_like_manga_download_dir(entry.path):
                                continue
                            try:
                                modified_at = datetime.fromtimestamp(entry.stat().st_mtime)
                            except Exception:
                                continue
                            delta_seconds = abs((modified_at - resume_dt).total_seconds())
                            if delta_seconds <= 20 * 60:
                                candidates.append((delta_seconds, entry.path))
                    except Exception:
                        continue

                if candidates:
                    candidates.sort(key=lambda item: item[0])
                    return candidates[0][1]

        cached_detail = self._deps.get_cached_manga_detail(adapter, source_url)
        if cached_detail and cached_detail.title:
            from chapter_naming import looks_like_manga_download_dir
            for candidate_root in candidate_roots:
                cached_dir = os.path.join(candidate_root, sanitize_filename(cached_detail.title))
                if os.path.isdir(cached_dir) and looks_like_manga_download_dir(cached_dir):
                    return cached_dir

        return None

    def get_local_manga_detail(self, adapter, source_url):
        from local_library import format_local_library_status, get_library_update_status_lines

        entry = self._deps.find_library_entry_by_source_url(adapter, source_url)
        if entry is None:
            return None

        root_dir = (entry.get("root_dir") or "").strip()
        downloaded_count = int(entry.get("downloaded_chapter_count") or 0)
        if downloaded_count <= 0:
            return None
        latest_title = entry.get("last_downloaded_chapter_title") or "-"
        manga_title = str(entry.get("manga_title") or os.path.basename(root_dir.rstrip("\\/")) or "本地漫画")

        resume_state = self.load_download_state() or {}
        resume_url = (resume_state.get("url") or "").strip()
        resume_matches = (
            resume_state.get("site_key") == adapter.key
            and resume_url
            and adapter.get_manga_cache_key(resume_url) == adapter.get_manga_cache_key(source_url)
        )
        total_chapters = int(resume_state.get("total_chapters") or 0) if resume_matches else 0
        next_chapter_order = int(resume_state.get("current_chapter_order") or 0) if resume_matches else 0
        metadata_total = int(entry.get("total_chapters") or 0)
        effective_total = max(total_chapters, metadata_total)

        detail_parts = [format_local_library_status(entry).replace("本地状态: ", "")]
        detail_parts.extend(get_library_update_status_lines(entry))
        if latest_title:
            detail_parts.append(f"已下载到 {latest_title}")
        if next_chapter_order > 0:
            detail_parts.append("可从本地断点继续下载")
        if effective_total > 0:
            detail_parts.append(f"总章节数约 {effective_total} 章")
        if root_dir:
            detail_parts.append(f"目录: {root_dir}")

        return MangaDetail(
            title=manga_title,
            manga_url=(source_url or "").strip(),
            section="本地离线",
            cover_url=str(entry.get("cover_url") or entry.get("_local_cover_path") or ""),
            latest_chapter=latest_title or "-",
            update_time="本地目录",
            detail_hint="\n".join(detail_parts),
            detail_section_label=f"站点: {adapter.display_name}（离线）",
            chapter_count=effective_total or downloaded_count,
            start_chapter_title="",
        )

    def get_fallback_manga_detail(self, adapter, source_url):
        cached_detail = self._deps.get_cached_manga_detail(adapter, source_url)
        if cached_detail is not None:
            cached_hint = cached_detail.detail_hint or ""
            suffix = "当前站点暂时无法访问，已显示上次成功获取的缓存信息。"
            cached_detail.detail_hint = f"{cached_hint}；{suffix}" if cached_hint else suffix
            return cached_detail, "cache"

        local_detail = self.get_local_manga_detail(adapter, source_url)
        if local_detail is not None:
            local_hint = local_detail.detail_hint or ""
            suffix = "当前站点暂时无法访问\n已显示本地下载记录"
            local_detail.detail_hint = f"{local_hint}\n{suffix}" if local_hint else suffix
            return local_detail, "local"

        return None, ""

    # --- 核心下载逻辑 ---
    def download_manga(self, url, adapter_key, retry_plan=None):
        download_summary = None
        retry_failed_records = normalize_failed_retry_records(
            (retry_plan or {}).get("failed_chapter_records") or []
        )
        try:
            adapter = get_adapter(adapter_key)
            self._deps.status("正在分析漫画信息...")
            self._deps.log(f"开始下载: {url}", "info")
            self._deps.log(f"当前站点适配器: {adapter.display_name}", "info")
            if retry_failed_records:
                self._deps.log(
                    f"🔁 本次将重试失败章节: {format_failed_chapter_list_text(retry_failed_records)}",
                    "info",
                )

            # 1. 获取漫画信息
            manga_id, manga_slug, url_start_slug = adapter.get_manga_info_from_url(url)
            if not manga_id or not manga_slug:
                self._deps.log("❌ 无法获取漫画信息", "error")
                download_summary = {"final_state": "failed"}
                return
            self._deps.log(f"✅ 已识别漫画链接，漫画标识: {manga_id}", "info")

            # 2. 获取所有章节
            self._deps.status("正在获取章节列表...")
            self._deps.log("🔍 正在请求漫画主页并解析章节列表...", "info")
            if adapter.key == "manhuagui":
                self._deps.log("⚠️ 漫画柜站点响应可能较慢，主页链接首次解析通常需要几秒。", "warning")
            if adapter.key == "mangacopy":
                self._deps.log(
                    "ℹ️ 拷贝漫画在章节列表阶段可能会轮询多个 API host 并自动重试，请留意后续轮询日志。", "info"
                )
            heartbeat = self._deps.start_heartbeat(
                f"{adapter.display_name} 章节列表获取",
                status_prefix="正在获取章节列表",
                interval_seconds=8,
            )
            try:
                manga_title, all_chapters = adapter.get_all_chapters(manga_id)
            finally:
                self._deps.stop_heartbeat(heartbeat)
            if not all_chapters:
                self._deps.log("❌ 无法获取章节列表", "error")
                download_summary = {"final_state": "failed"}
                return

            self._deps.log(f"✅ 找到漫画: {manga_title}, 共 {len(all_chapters)} 章", "info")

            # 3. 确定起始章节（通过 GUI 回调读取 start_var）
            start_order = self._deps.get_start_chapter_order() - 1
            start_chapter_title = ""
            pending_chapters: List[Dict[str, Any]] = []
            if retry_failed_records:
                pending_chapters, missing_retry_records = match_retry_chapters(
                    all_chapters, retry_failed_records
                )
                if missing_retry_records:
                    self._deps.log(
                        f"⚠️ 以下失败章节未在最新章节列表中匹配到，已跳过重试: "
                        f"{format_failed_chapter_list_text(missing_retry_records)}",
                        "warning",
                    )
                if pending_chapters:
                    ordered_values = [
                        chapter.get("order")
                        for chapter in pending_chapters
                        if isinstance(chapter.get("order"), int)
                    ]
                    if ordered_values:
                        start_order = min(ordered_values)
                    first_retry_chapter = min(
                        pending_chapters,
                        key=lambda chapter: (
                            chapter.get("order") is None,
                            chapter.get("order") if chapter.get("order") is not None
                            else str(chapter.get("title") or ""),
                        ),
                    )
                    start_chapter_title = first_retry_chapter.get("title") or ""
                    self._deps.log(f"📥 本次仅重试 {len(pending_chapters)} 个失败章节", "info")
            else:
                if start_order <= 0 and url_start_slug:
                    matched_chapter = next(
                        (
                            chapter for chapter in all_chapters
                            if chapter.get("slug") == url_start_slug
                            or chapter.get("uuid") == url_start_slug
                        ),
                        None,
                    )
                    if matched_chapter is not None:
                        start_order = matched_chapter.get("order", start_order)
                        start_chapter_title = matched_chapter.get("title") or url_start_slug
                        self._deps.log(
                            f"⚙️ 已根据链接定位起始章节: {matched_chapter.get('title') or url_start_slug}",
                            "info",
                        )
                pending_chapters = [c for c in all_chapters if c["order"] >= start_order]

            if not pending_chapters:
                self._deps.log("⚠️ 没有找到需要下载的章节", "warning")
                download_summary = {
                    "final_state": "empty",
                    "failed_chapter_records": retry_failed_records,
                }
                return

            latest_chapter = all_chapters[-1] if all_chapters else {}
            detail_parts = [f"共 {len(all_chapters)} 章"]
            if retry_failed_records:
                detail_parts.append(f"失败重试: {format_failed_chapter_list_text(retry_failed_records)}")
            elif start_chapter_title:
                detail_parts.append(f"当前链接定位到 {start_chapter_title}")
            self._deps.cache_manga_detail(
                adapter,
                url,
                MangaDetail(
                    title=manga_title,
                    manga_url=url,
                    section="手动链接",
                    cover_url="",
                    latest_chapter=latest_chapter.get("title") or "-",
                    update_time=format_updated_at(latest_chapter.get("updated_at")),
                    detail_hint="，".join(detail_parts),
                    detail_section_label=f"站点: {adapter.display_name}",
                    chapter_count=len(all_chapters),
                    start_chapter_title=start_chapter_title,
                ),
            )

            # 4. 设置保存目录
            safe_manga_title = sanitize_filename(str(manga_title))
            root_dir = os.path.join(self._deps.get_download_workspace_dir(), safe_manga_title)
            os.makedirs(root_dir, exist_ok=True)
            self.active_manga_title = str(manga_title)
            self.active_download_root_dir = root_dir
            self.active_download_metadata = self.build_active_download_metadata(
                adapter, url, manga_title, root_dir,
                all_chapters, start_order, start_chapter_title,
            )
            self.save_active_download_metadata()

            self._deps.log(f"📂 保存目录: {root_dir}", "info")
            self._deps.log(f"📥 准备下载 {len(pending_chapters)} 章", "info")

            # 5. 构建基础URL模板
            base_url_template = adapter.build_chapter_url_template(manga_slug)

            # 6. 开始下载
            max_concurrent = self._deps.get_concurrent_limit()
            max_image_concurrent = self._deps.get_image_concurrent_limit()
            max_concurrent, max_image_concurrent, settings_message = adapter.adjust_download_settings(
                max_concurrent, max_image_concurrent,
            )
            if settings_message:
                self._deps.log(settings_message, "warning")

            download_summary = self.download_chapters_concurrently(
                adapter, pending_chapters, base_url_template, root_dir,
                max_concurrent, max_image_concurrent,
            )

        except RuntimeError as e:
            if self._deps.is_site_access_blocked_error(e):
                self._deps.handle_site_access_blocked(adapter.display_name, e)
            elif self._deps.is_site_unreachable_error(e):
                self._deps.handle_site_unreachable(adapter, e)
            else:
                self._deps.log(f"❌ 下载过程中出现错误: {str(e)}", "error")
            if download_summary is None:
                download_summary = {"final_state": "failed"}
        except Exception as e:
            self._deps.log(f"❌ 下载过程中出现错误: {str(e)}", "error")
            if download_summary is None:
                download_summary = {"final_state": "failed"}
        finally:
            self.download_complete(download_summary)

    def download_chapters_concurrently(self, adapter, chapters, base_url_template, root_dir,
                                       max_concurrent, max_image_concurrent) -> Dict[str, Any]:
        chapter_queue = [dict(chapter, _retry_count=0) for chapter in chapters]
        total_chapters = len(chapter_queue)
        completed_chapters = 0
        failed_chapters = 0
        failed_chapter_records: List[Dict[str, Any]] = []
        retry_limit = adapter.get_chapter_retry_limit()
        cooldown_until = 0.0
        stopped_early = False
        scheduler_error = None
        summary: Dict[str, Any] = {
            "final_state": "failed",
            "root_dir": root_dir,
            "manga_title": self.active_manga_title,
            "total_chapters": total_chapters,
            "completed_chapters": 0,
            "failed_chapters": 0,
            "failed_chapter_records": [],
            "should_offer_archive": False,
        }

        self._deps.log(
            f"开始并发下载，章节并发数: {max_concurrent}, 图片并发数: {max_image_concurrent}", "info"
        )

        try:
            self.executor = ThreadPoolExecutor(max_workers=max_concurrent)
            futures = {}

            while chapter_queue or futures:
                while self.is_paused and self.is_downloading and not self.stop_event.is_set():
                    self.pause_event.wait(timeout=0.2)

                if not self.is_downloading:
                    stopped_early = True
                    self._deps.log("🛑 下载已停止", "info")
                    for future in futures:
                        future.cancel()
                    break

                if not futures and chapter_queue and cooldown_until > time.time():
                    remaining = max(cooldown_until - time.time(), 0)
                    self._deps.status(f"网络波动，{remaining:.0f} 秒后自动重试...")
                    time.sleep(min(remaining, 0.5))
                    continue

                while chapter_queue and len(futures) < max_concurrent:
                    if cooldown_until > time.time():
                        break
                    chapter = chapter_queue.pop(0)
                    future = self.executor.submit(
                        adapter.download_chapter_images,
                        chapter["slug"],
                        base_url_template,
                        root_dir,
                        max_image_concurrent,
                        self.stop_event,
                        False,
                    )
                    futures[future] = chapter

                if not futures:
                    if chapter_queue:
                        stopped_early = True
                    break

                done, _ = wait(list(futures.keys()), timeout=0.2, return_when=FIRST_COMPLETED)
                if not done:
                    continue

                for future in done:
                    chapter = futures.pop(future)
                    try:
                        count, next_slug, _ = future.result()
                        if count > 0:
                            completed_chapters += 1
                            self._deps.log(
                                f"✅ 第 {chapter['order'] + 1} 章下载完成 ({completed_chapters}/{total_chapters})",
                                "info",
                            )
                            self.save_download_state(chapter["order"], total_chapters)
                            self.save_active_download_metadata()
                        else:
                            failed_chapters += 1
                            self._deps.log(
                                f"⚠️ 第 {chapter['order'] + 1} 章下载失败", "warning"
                            )
                            failed_chapter_records.append(
                                build_failed_chapter_record(
                                    chapter, "章节返回空数据或图片列表为空"
                                )
                            )
                            self.save_active_download_metadata(
                                failed_chapter_records=failed_chapter_records
                            )
                    except Exception as e:
                        retry_count = chapter.get("_retry_count", 0)
                        if (
                            retry_limit > 0
                            and retry_count < retry_limit
                            and adapter.should_retry_download_error(e)
                            and self.is_downloading
                            and not self.stop_event.is_set()
                        ):
                            retry_count += 1
                            delay = adapter.get_retry_delay_seconds(retry_count)
                            chapter["_retry_count"] = retry_count
                            chapter_queue.insert(0, chapter)
                            cooldown_until = max(cooldown_until, time.time() + delay)
                            self._deps.log(
                                f"⚠️ 第 {chapter['order'] + 1} 章请求超时，"
                                f"{delay:.0f} 秒后自动重试 ({retry_count}/{retry_limit})",
                                "warning",
                            )
                            self._deps.status(f"第 {chapter['order'] + 1} 章重试准备中...")
                            continue

                        failed_chapters += 1
                        failed_chapter_records.append(
                            build_failed_chapter_record(chapter, str(e))
                        )
                        self.save_active_download_metadata(
                            failed_chapter_records=failed_chapter_records
                        )
                        self._deps.log(
                            f"❌ 第 {chapter['order'] + 1} 章下载出错: {str(e)}", "error"
                        )

                    progress = (completed_chapters + failed_chapters) / total_chapters * 100
                    self._deps.set_progress(progress)
                    self._deps.status(f"进度: {completed_chapters + failed_chapters}/{total_chapters}")

        except Exception as e:
            scheduler_error = e
            self._deps.log(f"❌ 并发下载出错: {str(e)}", "error")
        finally:
            if self.executor:
                self.executor.shutdown(wait=False, cancel_futures=True)
                self.executor = None

        processed_chapters = completed_chapters + failed_chapters
        all_processed = processed_chapters >= total_chapters
        fully_completed = all_processed and completed_chapters == total_chapters
        partially_completed = completed_chapters > 0 and not fully_completed
        stopped_early = stopped_early or self.stop_event.is_set() or (
            not all_processed and not self.is_downloading
        )

        self._deps.log(f"\n📊 下载完成统计:", "info")
        self._deps.log(f"✅ 成功: {completed_chapters} 章", "info")
        self._deps.log(f"❌ 失败: {failed_chapters} 章", "info")
        if failed_chapter_records:
            self._deps.log(
                f"❌ 失败章节号: {get_failed_chapter_numbers_text(failed_chapter_records)}",
                "error",
            )
        if stopped_early and not all_processed:
            self._deps.log(f"⏹️ 未处理: {total_chapters - processed_chapters} 章", "warning")
        self._deps.log(f"📁 文件保存在: {root_dir}", "info")

        final_state = "failed"
        if fully_completed:
            final_state = "completed"
        elif stopped_early:
            final_state = "stopped"
        elif partially_completed:
            final_state = "partial"

        if scheduler_error and final_state == "completed":
            final_state = "partial" if completed_chapters > 0 else "failed"

        if final_state == "completed":
            self.clear_download_state()

        self.save_active_download_metadata(
            mark_completed=(final_state == "completed"),
            failed_chapter_records=failed_chapter_records,
            final_state=final_state,
        )

        summary.update({
            "final_state": final_state,
            "completed_chapters": completed_chapters,
            "failed_chapters": failed_chapters,
            "failed_chapter_records": failed_chapter_records,
            "should_offer_archive": completed_chapters > 0 and os.path.isdir(root_dir),
        })
        return summary

    # --- 下载完成（状态重置，通知 GUI） ---
    def download_complete(self, download_summary: Optional[Dict[str, Any]] = None):
        stopped = self.stop_event.is_set()
        active_download_url = self.active_download_url
        active_root_dir = self.active_download_root_dir
        active_manga_title = self.active_manga_title
        normalized_summary = dict(download_summary or {})
        if active_download_url and not normalized_summary.get("source_url"):
            normalized_summary["source_url"] = active_download_url
        if self.download_site_key and not normalized_summary.get("site_key"):
            normalized_summary["site_key"] = self.download_site_key
        if active_root_dir and not normalized_summary.get("root_dir"):
            normalized_summary["root_dir"] = active_root_dir
        if active_manga_title and not normalized_summary.get("manga_title"):
            normalized_summary["manga_title"] = active_manga_title
        normalized_summary["failed_chapter_records"] = normalize_failed_retry_records(
            normalized_summary.get("failed_chapter_records") or []
        )

        self.is_downloading = False
        self.is_paused = False
        self.executor = None
        self.active_download_url = ""
        self.active_download_root_dir = ""
        self.active_manga_title = ""
        self.active_download_metadata = None
        if normalized_summary["failed_chapter_records"] and normalized_summary.get("source_url"):
            self.set_failed_retry_plan(normalized_summary)
        else:
            self.set_failed_retry_plan(None)

        # 通知 GUI 更新按钮/进度/状态栏
        self._deps.download_complete_ui(normalized_summary)
