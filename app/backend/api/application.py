"""Application layer: search, resolve, download orchestration."""
from __future__ import annotations

import os
import threading
import time
import traceback
from typing import Any, Dict, List, Optional

from backend.models import MangaDetailRequest
from backend.services.detail_cache_service import DetailCacheService
from backend.services.library_service import LibraryService
from backend.services.manga_service import MangaDetailService
from backend.support.archive import export_manga_to_cbz, list_exportable_image_files
from backend.support.downcomic import sanitize_filename
from backend.support.local_library import (
    build_downloaded_chapter_records_from_disk,
    save_library_entry_metadata,
)
from backend.support.site_adapters import SITE_ADAPTERS, get_adapter, resolve_adapter_from_url
from backend.support.storage_paths import ensure_storage_root_dir, get_manga_detail_cache_file_path, get_task_history_file_path


class Application:
    """High-level operations exposed by the API."""

    def __init__(self) -> None:
        self._manga_service = MangaDetailService()
        self._storage_root_dir = ensure_storage_root_dir()
        self._legacy_root_dir = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
        self._detail_cache_service = DetailCacheService(
            cache_file=get_manga_detail_cache_file_path(),
        )
        self._detail_cache: Dict[str, Any] = self._detail_cache_service.load()
        self._library_service = LibraryService(
            storage_root_dir=self._storage_root_dir,
            legacy_root_dir=self._legacy_root_dir,
        )
        self._downloads: Dict[str, Dict[str, Any]] = {}
        self._download_runtime: Dict[str, Dict[str, Any]] = {}
        self._task_history_file = get_task_history_file_path()
        self._task_history: List[Dict[str, Any]] = self._load_task_history()
        self._search_cache: Dict[str, tuple] = {}  # key -> (timestamp, results)
        self._search_cache_ttl = 300  # 5 minutes
        self._export_tasks: Dict[str, Dict[str, Any]] = {}
        self._lock = threading.RLock()

    # ------------------------------------------------------------------
    # Health / settings
    # ------------------------------------------------------------------
    def get_health(self) -> Dict[str, Any]:
        return {
            "status": "ok",
            "storage_root": self._storage_root_dir,
            "pid": os.getpid(),
        }

    def get_settings(self) -> Dict[str, Any]:
        return {
            "storage_root": self._storage_root_dir,
            "legacy_root": self._legacy_root_dir,
            "download_runner_configured": True,
            "supported_sites": list(SITE_ADAPTERS.keys()),
        }

    def update_settings(self, _payload: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        # WinUI process settings are persisted client-side. The backend keeps
        # returning environment/runtime information for display purposes.
        return self.get_settings()

    # ------------------------------------------------------------------
    # Search
    # ------------------------------------------------------------------
    def search(self, query: str, site: str = "baozimh", page: int = 1) -> List[Dict[str, Any]]:
        """Search for manga on the given site, with short-lived result cache."""
        import time

        cache_key = f"{site}:{query}:{page}"
        now = time.time()

        with self._lock:
            cached = self._search_cache.get(cache_key)
            if cached and (now - cached[0]) < self._search_cache_ttl:
                return cached[1]

        adapter = get_adapter(site)
        if not hasattr(adapter, "fetch_search_cards"):
            raise RuntimeError(f"站点 {site} 不支持搜索")

        cards = adapter.fetch_search_cards(query, page=page)
        results: List[Dict[str, Any]] = []
        for card in cards:
            results.append(
                {
                    "title": getattr(card, "title", ""),
                    "url": getattr(card, "manga_url", ""),
                    "cover_url": getattr(card, "cover_url", ""),
                    "latest_chapter": getattr(card, "latest_chapter", ""),
                    "update_time": getattr(card, "update_time", ""),
                }
            )

        with self._lock:
            self._search_cache[cache_key] = (now, results)
            # Evict expired entries to prevent unbounded growth
            if len(self._search_cache) > 200:
                cutoff = now - self._search_cache_ttl
                self._search_cache = {
                    k: v for k, v in self._search_cache.items() if v[0] > cutoff
                }

        return results

    # ------------------------------------------------------------------
    # Resolve
    # ------------------------------------------------------------------
    def resolve(self, url: str, site: str = "") -> Dict[str, Any]:
        """Resolve a manga URL to its detail page."""
        request = MangaDetailRequest(url=url, fallback_site_key=site or None)
        result = self._manga_service.fetch_detail(
            request,
            cache_detail=self._cache_detail,
            fallback_detail_getter=self._get_cached_detail,
        )
        detail = result.detail
        adapter = result.adapter

        chapters: List[Dict[str, str]] = []
        latest_chapter = getattr(detail, "latest_chapter", "") or ""
        if getattr(adapter, "supports_download", False):
            manga_id, manga_slug, _ = adapter.get_manga_info_from_url(url)
            if manga_id and manga_slug:
                _, chapter_items = adapter.get_all_chapters(manga_id)
                chapter_url_template = adapter.build_chapter_url_template(manga_slug)
                for ch in chapter_items or []:
                    chapter_slug = str(ch.get("slug") or "")
                    chapter_title = str(ch.get("title") or chapter_slug)
                    chapters.append(
                        {
                            "title": chapter_title,
                            "url": chapter_url_template.format(slug=chapter_slug) if chapter_slug else "",
                        }
                    )
                if chapters:
                    latest_chapter = chapters[-1]["title"]

        return {
            "title": getattr(detail, "title", ""),
            "site_name": getattr(adapter, "display_name", site),
            "url": url,
            "latest_chapter": latest_chapter,
            "chapters": chapters,
            "cover_url": getattr(detail, "cover_url", ""),
            "detail_hint": getattr(detail, "detail_hint", ""),
        }

    def _cache_detail(self, adapter: Any, url: str, detail: Any) -> None:
        with self._lock:
            self._detail_cache = self._detail_cache_service.cache_detail(
                self._detail_cache,
                adapter,
                url,
                detail,
            )

    def _get_cached_detail(self, adapter: Any, url: str):
        with self._lock:
            detail = self._detail_cache_service.get_cached_detail(self._detail_cache, adapter, url)
        if detail is None:
            raise RuntimeError("未命中缓存")
        return detail, "detail_cache"

    # ------------------------------------------------------------------
    # Downloads
    # ------------------------------------------------------------------
    def create_download(self, url: str, site: str = "", chapters: Optional[List[str]] = None) -> Dict[str, Any]:
        """Create a real download task and return its initial snapshot."""
        import uuid

        task_id = str(uuid.uuid4())[:8]
        runtime = {
            "stop_event": threading.Event(),
            "pause_event": threading.Event(),
            "thread": None,
        }
        runtime["pause_event"].set()

        task = {
            "id": task_id,
            "url": url,
            "site": site,
            "chapters": list(chapters or []),
            "status": "pending",
            "status_text": "等待开始",
            "progress": 0.0,
            "logs": [],
            "task_error": None,
            "root_dir": "",
            "manga_title": "",
            "completed_chapter_count": 0,
            "total_chapter_count": 0,
        }
        with self._lock:
            self._downloads[task_id] = task
            self._download_runtime[task_id] = runtime

        thread = threading.Thread(target=self._process_download, args=(task_id,), daemon=True)
        runtime["thread"] = thread
        thread.start()
        return self.get_download(task_id) or self._snapshot_task(task)

    def _process_download(self, task_id: str) -> None:
        adapter = None
        all_chapters: List[Dict[str, Any]] = []
        try:
            runtime = self._get_runtime(task_id)
            task = self._require_task(task_id)
            adapter = resolve_adapter_from_url(task["url"], fallback_key=task["site"])
            if not getattr(adapter, "supports_download", False):
                raise RuntimeError(f"{adapter.display_name} 当前不支持下载")

            self._append_log(task_id, "info", "正在解析漫画链接...")
            manga_id, manga_slug, start_slug = adapter.get_manga_info_from_url(task["url"], stop_event=runtime["stop_event"])
            self._append_log(task_id, "info", f"解析链接: manga_id={manga_id}, slug={manga_slug}")
            if not manga_id or not manga_slug:
                raise RuntimeError(f"无法解析漫画链接 (id={manga_id}, slug={manga_slug})")

            if runtime["stop_event"].is_set():
                self._set_task_fields(task_id, status="stopped", status_text="已停止")
                self._append_log(task_id, "info", "下载已停止")
                self._record_task_to_history(task_id)
                return

            self._append_log(task_id, "info", "正在获取章节列表...")
            manga_title, all_chapters = adapter.get_all_chapters(manga_id)
            manga_title = manga_title or manga_slug
            self._append_log(task_id, "info", f"漫画: {manga_title}，共 {len(all_chapters)} 章")
            root_dir = os.path.join(self._storage_root_dir, sanitize_filename(str(manga_title)))
            os.makedirs(root_dir, exist_ok=True)

            base_url_template = adapter.build_chapter_url_template(manga_slug)
            requested = task.get("chapters") or []
            selected_chapters = self._select_chapters(
                all_chapters,
                requested_chapters=requested,
                start_slug=start_slug,
            )
            self._append_log(task_id, "info", f"请求章节: {len(requested)} 个，匹配到: {len(selected_chapters)} 个")
            if not selected_chapters:
                raise RuntimeError(f"没有匹配到可下载章节 (API返回 {len(all_chapters)} 章，请求 {len(requested)} 个)")

            chapter_concurrency, image_concurrency, setting_message = adapter.adjust_download_settings(1, 3)
            self._set_task_fields(
                task_id,
                site=adapter.key,
                manga_title=manga_title,
                root_dir=root_dir,
                total_chapter_count=len(selected_chapters),
                status="running",
                status_text=f"准备下载 0/{len(selected_chapters)} 章",
            )
            self._append_log(task_id, "info", f"站点: {adapter.display_name}")
            self._append_log(task_id, "info", f"保存目录: {root_dir}")
            if setting_message:
                self._append_log(task_id, "warn", setting_message)
            self._append_log(task_id, "info", f"章节并发: {chapter_concurrency}，图片并发: {image_concurrency}")

            completed = 0
            failed_records: List[Dict[str, Any]] = []
            for chapter in selected_chapters:
                if runtime["stop_event"].is_set():
                    self._set_task_fields(
                        task_id,
                        status="stopped",
                        status_text=f"已停止，完成 {completed}/{len(selected_chapters)} 章",
                    )
                    self._append_log(task_id, "info", "下载已停止")
                    self._record_task_to_history(task_id)
                    return

                self._wait_if_paused(task_id, runtime, completed, len(selected_chapters))
                if runtime["stop_event"].is_set():
                    self._set_task_fields(
                        task_id,
                        status="stopped",
                        status_text=f"已停止，完成 {completed}/{len(selected_chapters)} 章",
                    )
                    self._append_log(task_id, "info", "下载已停止")
                    self._record_task_to_history(task_id)
                    return

                chapter_title = str(chapter.get("title") or chapter.get("slug") or "未知章节")
                self._set_task_fields(
                    task_id,
                    status="running",
                    status_text=f"下载中 {completed}/{len(selected_chapters)} 章: {chapter_title}",
                )
                self._append_log(task_id, "info", f"开始章节: {chapter_title}")

                retry_limit = max(int(getattr(adapter, "get_chapter_retry_limit", lambda: 0)() or 0), 0)
                chapter_success = False
                last_error: Optional[Exception] = None
                for attempt in range(retry_limit + 1):
                    try:
                        downloaded_count, _, _ = adapter.download_chapter_images(
                            chapter.get("slug"),
                            base_url_template,
                            root_dir,
                            max_concurrent_images=image_concurrency,
                            stop_event=runtime["stop_event"],
                            show_progress=False,
                        )
                        if runtime["stop_event"].is_set():
                            break
                        if downloaded_count <= 0:
                            raise RuntimeError(f"{chapter_title} 未下载到任何图片")
                        self._append_log(task_id, "info", f"完成章节: {chapter_title} ({downloaded_count} 张图片)")
                        chapter_success = True
                        break
                    except Exception as exc:
                        last_error = exc
                        if runtime["stop_event"].is_set():
                            break
                        should_retry = (
                            attempt < retry_limit
                            and getattr(adapter, "should_retry_download_error", lambda _err: False)(exc)
                        )
                        if not should_retry:
                            break
                        delay_seconds = float(
                            getattr(adapter, "get_retry_delay_seconds", lambda retry_count: 0.0)(attempt + 1) or 0.0
                        )
                        self._append_log(
                            task_id,
                            "warn",
                            f"章节失败，准备重试 ({attempt + 1}/{retry_limit})：{chapter_title} - {exc}",
                        )
                        if delay_seconds > 0:
                            time.sleep(delay_seconds)

                if runtime["stop_event"].is_set():
                    self._set_task_fields(
                        task_id,
                        status="stopped",
                        status_text=f"已停止，完成 {completed}/{len(selected_chapters)} 章",
                    )
                    self._append_log(task_id, "info", "下载已停止")
                    self._record_task_to_history(task_id)
                    return

                if chapter_success:
                    completed += 1
                    progress = (completed / max(len(selected_chapters), 1)) * 100.0
                    self._set_task_fields(
                        task_id,
                        completed_chapter_count=completed,
                        progress=progress,
                        status="running",
                        status_text=f"已完成 {completed}/{len(selected_chapters)} 章",
                    )
                    self._append_log(task_id, "progress", f"完成章节: {chapter_title}")
                    self._persist_library_metadata(
                        task_id=task_id,
                        adapter=adapter,
                        manga_title=manga_title,
                        root_dir=root_dir,
                        all_chapters=all_chapters,
                        completed=False,
                        failed_records=failed_records,
                    )
                    continue

                reason = str(last_error or "章节下载失败")
                failed_records.append(
                    {
                        "order": chapter.get("order"),
                        "display_order": (int(chapter.get("order")) + 1) if isinstance(chapter.get("order"), int) else None,
                        "slug": str(chapter.get("slug") or ""),
                        "title": chapter_title,
                        "reason": reason,
                    }
                )
                self._append_log(task_id, "error", f"章节失败: {chapter_title} - {reason}")

            final_progress = (completed / max(len(selected_chapters), 1)) * 100.0 if selected_chapters else 0.0
            if failed_records:
                status = "partial" if completed > 0 else "failed"
                summary = f"部分完成 {completed}/{len(selected_chapters)} 章" if completed > 0 else "下载失败"
                self._set_task_error(task_id, "download_failed", f"{summary}，失败章节 {len(failed_records)} 个")
                self._set_task_fields(
                    task_id,
                    status=status,
                    progress=final_progress,
                    completed_chapter_count=completed,
                    status_text=summary,
                )
                self._persist_library_metadata(
                    task_id=task_id,
                    adapter=adapter,
                    manga_title=manga_title,
                    root_dir=root_dir,
                    all_chapters=all_chapters,
                    completed=False,
                    failed_records=failed_records,
                )
                self._record_task_to_history(task_id)
                return

            self._set_task_fields(
                task_id,
                status="completed",
                progress=100.0,
                completed_chapter_count=completed,
                status_text=f"下载完成 {completed}/{len(selected_chapters)} 章",
            )
            self._append_log(task_id, "info", "下载完成")
            self._persist_library_metadata(
                task_id=task_id,
                adapter=adapter,
                manga_title=manga_title,
                root_dir=root_dir,
                all_chapters=all_chapters,
                completed=True,
                failed_records=[],
            )
            self._record_task_to_history(task_id)
        except Exception as ex:
            self._set_task_error(task_id, "download_failed", str(ex))
            self._set_task_fields(task_id, status="failed", status_text=f"下载失败: {ex}")
            self._append_log(task_id, "error", f"下载异常: {ex}")
            self._append_log(task_id, "debug", traceback.format_exc())
            self._record_task_to_history(task_id)
        finally:
            with self._lock:
                runtime = self._download_runtime.get(task_id)
                if runtime is not None:
                    runtime["thread"] = None

    def _wait_if_paused(self, task_id: str, runtime: Dict[str, Any], completed: int, total: int) -> None:
        was_paused = False
        while not runtime["pause_event"].is_set():
            if runtime["stop_event"].is_set():
                return
            if not was_paused:
                self._set_task_fields(
                    task_id,
                    status="paused",
                    status_text=f"已暂停，完成 {completed}/{total} 章",
                )
                self._append_log(task_id, "info", "下载已暂停")
                was_paused = True
            time.sleep(0.2)

        if was_paused:
            self._set_task_fields(
                task_id,
                status="running",
                status_text=f"继续下载 {completed}/{total} 章",
            )
            self._append_log(task_id, "info", "下载已继续")

    def _select_chapters(
        self,
        chapters: List[Dict[str, Any]],
        *,
        requested_chapters: List[str],
        start_slug: Optional[str],
    ) -> List[Dict[str, Any]]:
        pending = list(chapters or [])
        if start_slug:
            for index, chapter in enumerate(pending):
                if str(chapter.get("slug") or "") == str(start_slug):
                    pending = pending[index:]
                    break

        requested = {str(item).strip() for item in requested_chapters if str(item).strip()}
        if not requested:
            return pending

        selected = []
        for chapter in pending:
            slug = str(chapter.get("slug") or "").strip()
            title = str(chapter.get("title") or "").strip()
            if slug in requested or title in requested:
                selected.append(chapter)
        return selected

    def _persist_library_metadata(
        self,
        *,
        task_id: str,
        adapter: Any,
        manga_title: str,
        root_dir: str,
        all_chapters: List[Dict[str, Any]],
        completed: bool,
        failed_records: List[Dict[str, Any]],
    ) -> None:
        downloaded_chapters = build_downloaded_chapter_records_from_disk(root_dir, all_chapters)
        last_downloaded = downloaded_chapters[-1] if downloaded_chapters else {}
        task = self._require_task(task_id)
        entry = {
            "schema_version": 1,
            "site_key": getattr(adapter, "key", ""),
            "site_name": getattr(adapter, "display_name", ""),
            "manga_title": manga_title,
            "manga_url": task.get("url", ""),
            "root_dir": root_dir,
            "cover_url": "",
            "total_chapters": len(all_chapters),
            "downloaded_chapter_count": len(downloaded_chapters),
            "last_downloaded_chapter_title": last_downloaded.get("title", ""),
            "last_downloaded_chapter_order": last_downloaded.get("order"),
            "downloaded_chapters": downloaded_chapters,
            "completed": completed and not failed_records,
            "saved_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            "created_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            "last_failed_chapter_records": failed_records,
            "last_failed_chapter_count": len(failed_records),
        }
        if not save_library_entry_metadata(entry):
            self._append_log(task_id, "warn", "保存本地元数据失败")

    def get_downloads(self) -> List[Dict[str, Any]]:
        with self._lock:
            tasks = [self._snapshot_task(task) for task in self._downloads.values()]
        tasks.sort(key=lambda item: item.get("id", ""), reverse=True)
        return tasks

    def get_download(self, task_id: str) -> Optional[Dict[str, Any]]:
        with self._lock:
            task = self._downloads.get(task_id)
            return self._snapshot_task(task) if task else None

    def pause_download(self, task_id: str) -> bool:
        with self._lock:
            task = self._downloads.get(task_id)
            runtime = self._download_runtime.get(task_id)
            if task and runtime and task["status"] == "running":
                runtime["pause_event"].clear()
                task["status"] = "pausing"
                task["status_text"] = "正在暂停，等待当前章节收尾"
                return True
            return False

    def resume_download(self, task_id: str) -> bool:
        with self._lock:
            task = self._downloads.get(task_id)
            runtime = self._download_runtime.get(task_id)
            if task and runtime and task["status"] in ("paused", "pausing"):
                runtime["pause_event"].set()
                task["status"] = "running"
                task["status_text"] = "继续下载中"
                return True
            return False

    def stop_download(self, task_id: str) -> bool:
        with self._lock:
            task = self._downloads.get(task_id)
            runtime = self._download_runtime.get(task_id)
            if task and runtime and task["status"] in ("running", "paused", "pausing"):
                runtime["stop_event"].set()
                runtime["pause_event"].set()
                task["status"] = "stopping"
                task["status_text"] = "正在停止"
                return True
            return False

    def batch_stop_downloads(self, task_ids: List[str]) -> Dict[str, Any]:
        stopped = []
        failed = []
        for task_id in task_ids:
            if self.stop_download(task_id):
                stopped.append(task_id)
            else:
                failed.append(task_id)
        return {"stopped": stopped, "failed": failed}

    def batch_delete_downloads(self, task_ids: List[str]) -> Dict[str, Any]:
        deleted = []
        failed = []
        with self._lock:
            for task_id in task_ids:
                task = self._downloads.get(task_id)
                if task and task.get("status") in ("completed", "failed", "stopped", "partial"):
                    runtime = self._download_runtime.pop(task_id, None)
                    del self._downloads[task_id]
                    deleted.append(task_id)
                else:
                    failed.append(task_id)
        return {"deleted": deleted, "failed": failed}

    # ------------------------------------------------------------------
    # Library
    # ------------------------------------------------------------------
    def get_library(
        self,
        site_key: str = "",
        keyword: str = "",
        page: int = 1,
        page_size: int = 20,
    ) -> Dict[str, Any]:
        cards = self._library_service.build_local_library_cards(
            saved_detail_cache=self._detail_cache,
            page_size=page_size,
            current_site_display_name="",
            site_key=site_key,
            page=page,
            keyword=keyword,
        )
        items = []
        for card in cards["cards"]:
            items.append(
                {
                    "manga_title": card.get("manga_title", ""),
                    "site_name": card.get("site_name", ""),
                    "root_dir": card.get("root_dir", ""),
                    "manga_url": card.get("manga_url", ""),
                    "downloaded_chapter_count": card.get("downloaded_chapter_count", 0),
                    "last_downloaded_chapter_title": card.get("last_downloaded_chapter_title", ""),
                }
            )

        return {
            "items": items,
            "total": cards["total"],
            "page": cards["page"],
            "page_size": cards["page_size"],
        }

    def check_library_updates(self) -> Dict[str, Any]:
        results: List[Dict[str, Any]] = []
        entries = self._library_service.iter_library_entries(saved_detail_cache=self._detail_cache)
        for entry in entries:
            manga_url = str(entry.get("manga_url") or "").strip()
            site_key = str(entry.get("site_key") or "").strip()
            if not manga_url:
                continue
            try:
                detail = self.resolve(manga_url, site=site_key)
                remote_count = len(detail.get("chapters") or [])
                local_count = int(entry.get("downloaded_chapter_count") or 0)
                results.append(
                    {
                        "title": entry.get("manga_title", ""),
                        "has_update": remote_count > local_count,
                        "remote_chapter_count": remote_count,
                        "local_chapter_count": local_count,
                    }
                )
            except Exception as ex:
                results.append(
                    {
                        "title": entry.get("manga_title", ""),
                        "has_update": False,
                        "remote_chapter_count": 0,
                        "local_chapter_count": int(entry.get("downloaded_chapter_count") or 0),
                        "error": str(ex),
                    }
                )
        return {"items": results}

    def export_cbz(self, root_dir: str) -> Dict[str, Any]:
        import uuid

        task_id = str(uuid.uuid4())[:8]
        entry = self._library_service.get_local_library_entry_by_root(
            root_dir=root_dir,
            saved_detail_cache=self._detail_cache,
        )
        manga_title = str((entry or {}).get("manga_title") or os.path.basename(root_dir.rstrip("\\/")) or "漫画下载")
        manga_url = str((entry or {}).get("manga_url") or "")

        task = {
            "id": task_id,
            "status": "running",
            "manga_title": manga_title,
            "current_chapter": "",
            "current_index": 0,
            "total_chapters": 0,
            "exported_count": 0,
            "export_dir": "",
            "skipped_chapters": [],
            "error": None,
        }
        with self._lock:
            self._export_tasks[task_id] = task

        thread = threading.Thread(
            target=self._process_export_cbz,
            args=(task_id, root_dir, manga_title, manga_url),
            daemon=True,
        )
        thread.start()

        return {"status": "ok", "task_id": task_id}

    def _process_export_cbz(self, task_id: str, root_dir: str, manga_title: str, manga_url: str) -> None:
        try:
            def on_progress(chapter_index: int, total_chapters: int, chapter_title: str) -> None:
                with self._lock:
                    task = self._export_tasks.get(task_id)
                    if task is not None:
                        task["current_chapter"] = chapter_title
                        task["current_index"] = chapter_index
                        task["total_chapters"] = total_chapters
                        task["exported_count"] = chapter_index

            export_dir, exported_archives, skipped_chapters = export_manga_to_cbz(
                root_dir=root_dir,
                manga_title=manga_title,
                manga_url=manga_url,
                progress_callback=on_progress,
            )

            with self._lock:
                task = self._export_tasks.get(task_id)
                if task is not None:
                    task["status"] = "completed"
                    task["export_dir"] = export_dir
                    task["exported_count"] = len(exported_archives)
                    task["total_chapters"] = len(exported_archives) + len(skipped_chapters)
                    task["skipped_chapters"] = skipped_chapters
        except Exception as ex:
            with self._lock:
                task = self._export_tasks.get(task_id)
                if task is not None:
                    task["status"] = "failed"
                    task["error"] = str(ex)

    def get_export_task(self, task_id: str) -> Optional[Dict[str, Any]]:
        with self._lock:
            return self._export_tasks.get(task_id)

    # ------------------------------------------------------------------
    # Ranking
    # ------------------------------------------------------------------
    def get_ranking(
        self,
        site: str = "baozimh",
        section: str = "",
        page: int = 1,
    ) -> Dict[str, Any]:
        """Fetch ranking/leaderboard data for the given site."""
        adapter = get_adapter(site)

        if not getattr(adapter, "supports_discovery", False):
            raise RuntimeError(f"站点 {adapter.display_name} 不支持排行榜浏览")

        available_sections = adapter.get_section_options()
        if not available_sections:
            raise RuntimeError(f"站点 {adapter.display_name} 没有可用的排行榜分区")

        if not section:
            section = next(iter(available_sections.keys()), "")

        if section not in available_sections:
            raise RuntimeError(f"站点 {adapter.display_name} 不支持分区: {section}")

        # available_sections maps display_name -> internal_key.
        # Some adapters (e.g. baozimh) expect the internal key in fetch_section_cards,
        # while others (mangacopy, manhuagui) use the display name as the key.
        internal_key = available_sections.get(section, section)
        cards = adapter.fetch_section_cards(internal_key, page=page)
        items = []
        for card in cards:
            item = {
                "title": getattr(card, "title", ""),
                "url": getattr(card, "manga_url", ""),
                "cover_url": getattr(card, "cover_url", ""),
                "latest_chapter": getattr(card, "latest_chapter", ""),
                "update_time": getattr(card, "update_time", ""),
                "section": getattr(card, "section", ""),
            }
            detail_hint = getattr(card, "detail_hint", "")
            if detail_hint:
                item["detail_hint"] = detail_hint
            detail_section_label = getattr(card, "detail_section_label", "")
            if detail_section_label:
                item["detail_section_label"] = detail_section_label
            items.append(item)

        return {
            "items": items,
            "total": len(items),
            "section": section,
            "available_sections": available_sections,
            "is_single_page": adapter.is_single_page_section(section),
        }

    def get_ranking_sections(self, site: str = "baozimh") -> Dict[str, Any]:
        """Get available ranking sections for a site."""
        adapter = get_adapter(site)
        sections = adapter.get_section_options()
        return {
            "site": site,
            "site_name": adapter.display_name,
            "sections": sections,
        }

    # ------------------------------------------------------------------
    # Reader
    # ------------------------------------------------------------------
    def get_reader_chapters(self, root_dir: str) -> Dict[str, Any]:
        """List chapters in a manga directory for the reader."""
        if not os.path.isdir(root_dir):
            return {"manga_title": os.path.basename(root_dir), "chapters": []}

        records = build_downloaded_chapter_records_from_disk(root_dir)
        chapters = []
        for r in records:
            chapters.append({
                "dir_name": r.get("dir_name", ""),
                "title": r.get("title", r.get("dir_name", "")),
                "order": r.get("order") if r.get("order") is not None else 0,
                "image_count": r.get("image_count", 0),
            })
        chapters.sort(key=lambda c: c.get("order") if c.get("order") is not None else 9999)

        manga_title = os.path.basename(root_dir.rstrip("\\/"))
        metadata_file = os.path.join(root_dir, "元数据.json")
        if os.path.isfile(metadata_file):
            try:
                import json
                with open(metadata_file, "r", encoding="utf-8") as f:
                    meta = json.load(f)
                manga_title = meta.get("manga_title") or manga_title
            except Exception:
                pass

        return {"manga_title": manga_title, "chapters": chapters}

    def get_chapter_images(self, root_dir: str, chapter_dir_name: str) -> Dict[str, Any]:
        """List image file paths in a chapter directory for the reader."""
        chapter_dir = os.path.join(root_dir, chapter_dir_name)
        if not os.path.isdir(chapter_dir):
            return {"images": []}

        images = list_exportable_image_files(chapter_dir)
        return {"images": images}

    # ------------------------------------------------------------------
    # Task history
    # ------------------------------------------------------------------
    def _load_task_history(self) -> List[Dict[str, Any]]:
        try:
            if os.path.exists(self._task_history_file):
                import json
                with open(self._task_history_file, "r", encoding="utf-8") as f:
                    data = json.load(f)
                if isinstance(data, list):
                    return data
        except Exception:
            pass
        return []

    def _save_task_history(self) -> None:
        try:
            import json
            tmp_path = self._task_history_file + ".tmp"
            with open(tmp_path, "w", encoding="utf-8") as f:
                json.dump(self._task_history[-200:], f, ensure_ascii=False, indent=2)
            os.replace(tmp_path, self._task_history_file)
        except Exception:
            pass

    def _record_task_to_history(self, task_id: str) -> None:
        with self._lock:
            task = self._downloads.get(task_id)
            if task is None:
                return
            snapshot = {
                "id": task.get("id", ""),
                "url": task.get("url", ""),
                "site": task.get("site", ""),
                "manga_title": task.get("manga_title", ""),
                "status": task.get("status", ""),
                "progress": float(task.get("progress", 0.0) or 0.0),
                "completed_chapter_count": int(task.get("completed_chapter_count", 0) or 0),
                "total_chapter_count": int(task.get("total_chapter_count", 0) or 0),
                "root_dir": task.get("root_dir", ""),
                "task_error": task.get("task_error"),
                "finished_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            }
            self._task_history.append(snapshot)
        self._save_task_history()

    def get_download_history(self, page: int = 1, page_size: int = 20) -> Dict[str, Any]:
        with self._lock:
            total = len(self._task_history)
            start = max(0, total - page * page_size)
            end = max(0, total - (page - 1) * page_size)
            items = list(reversed(self._task_history[start:end]))
        return {"items": items, "total": total, "page": page, "page_size": page_size}

    def clear_download_history(self) -> None:
        with self._lock:
            self._task_history.clear()
        self._save_task_history()

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------
    def _require_task(self, task_id: str) -> Dict[str, Any]:
        with self._lock:
            task = self._downloads.get(task_id)
            if task is None:
                raise RuntimeError("任务不存在")
            return task

    def _get_runtime(self, task_id: str) -> Dict[str, Any]:
        with self._lock:
            runtime = self._download_runtime.get(task_id)
            if runtime is None:
                raise RuntimeError("任务运行时不存在")
            return runtime

    def _append_log(self, task_id: str, tag: str, message: str) -> None:
        with self._lock:
            task = self._downloads.get(task_id)
            if task is None:
                return
            task["logs"].append(
                {
                    "time": time.strftime("%H:%M:%S"),
                    "tag": tag,
                    "message": str(message),
                }
            )
            if len(task["logs"]) > 200:
                task["logs"] = task["logs"][-200:]

    def _set_task_fields(self, task_id: str, **fields: Any) -> None:
        with self._lock:
            task = self._downloads.get(task_id)
            if task is None:
                return
            task.update(fields)
            if "task_error" not in fields and task.get("status") in {"running", "completed", "paused", "pausing", "stopping", "stopped"}:
                task["task_error"] = None

    def _set_task_error(self, task_id: str, code: str, message: str) -> None:
        with self._lock:
            task = self._downloads.get(task_id)
            if task is None:
                return
            task["task_error"] = {"code": code, "message": message}

    def _snapshot_task(self, task: Optional[Dict[str, Any]]) -> Optional[Dict[str, Any]]:
        if task is None:
            return None
        return {
            "id": task.get("id", ""),
            "url": task.get("url", ""),
            "site": task.get("site", ""),
            "manga_title": task.get("manga_title", ""),
            "status": task.get("status", ""),
            "status_text": task.get("status_text", ""),
            "progress": float(task.get("progress", 0.0) or 0.0),
            "task_error": task.get("task_error"),
            "logs": list(task.get("logs", [])),
        }
