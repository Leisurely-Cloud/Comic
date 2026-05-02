from __future__ import annotations

import threading
from typing import Any, Optional


class DownloadStartError(Exception):
    """下载启动失败。"""


class _DownloadServiceBase:
    """下载服务的公共状态。"""

    def __init__(self) -> None:
        self.is_downloading = False
        self.is_paused = False
        self.stop_event = threading.Event()
        self.pause_event = threading.Event()
        self.pause_event.set()

    def start_download(self, url: Optional[str] = None) -> None:
        raise NotImplementedError

    def stop_download(self) -> None:
        raise NotImplementedError

    def pause_download(self) -> None:
        raise NotImplementedError

    def resume_download(self) -> None:
        raise NotImplementedError

    def check_resume_download_on_startup(self) -> None:
        pass

    def check_resume_download(self) -> bool:
        return False


class GuiDownloadService(_DownloadServiceBase):
    """GUI 模式的下载服务：委托给 DownloadController。"""

    def __init__(self, controller: Any) -> None:
        super().__init__()
        self._controller = controller

    def start_download(self, url: Optional[str] = None) -> None:
        self._controller.start_download(url=url)

    def stop_download(self) -> None:
        self._controller.stop_download()

    def pause_download(self) -> None:
        self._controller.pause_download()

    def resume_download(self) -> None:
        self._controller.resume_download()

    def check_resume_download_on_startup(self) -> None:
        self._controller.check_resume_download_on_startup()

    def check_resume_download(self) -> bool:
        return self._controller.check_resume_download()


class HeadlessDownloadService(_DownloadServiceBase):
    """无头模式的下载服务（CLI / API 用）。"""

    def start_download(self, url: Optional[str] = None) -> None:
        if not url:
            raise DownloadStartError("未提供下载链接")
        raise NotImplementedError("HeadlessDownloadService 尚未实现")

    def stop_download(self) -> None:
        self.stop_event.set()
        self.is_downloading = False

    def pause_download(self) -> None:
        self.is_paused = True
        self.pause_event.clear()

    def resume_download(self) -> None:
        self.is_paused = False
        self.pause_event.set()


def build_gui_download_service(gui: Any) -> GuiDownloadService:
    """从 GUI 实例构建下载服务，自动创建 DownloadController。"""
    from download_controller import DownloadController, DownloadControllerDeps

    deps = DownloadControllerDeps(
        log=gui.log_message,
        status=gui.set_status,
        run_on_ui_thread=gui.schedule_ui_task,
        get_selected_adapter=lambda: gui.current_adapter,
        set_active_adapter=gui.set_active_adapter_by_key,
        get_download_url=lambda: gui.url_entry.get(),
        set_download_url=lambda url: gui.url_entry.delete(0, "end") or gui.url_entry.insert(0, url),
        get_selected_card_url=lambda: getattr(gui, "current_detail_url", ""),
        apply_proxy_settings=gui.apply_proxy_settings,
        refresh_proxy_controls=gui.refresh_proxy_controls,
        get_proxy_enabled=lambda: gui.proxy_enabled_var.get(),
        get_known_cover_url=gui.get_known_cover_url_for_download,
        set_progress=gui.set_progress,
        set_progress_style=gui.set_progress_style,
        update_control_buttons=gui.update_control_buttons,
        set_failed_retry_plan=gui.set_failed_retry_plan,
        download_complete_ui=gui.download_complete,
        root=gui.root,
        ask_resume_confirmation=gui.ask_resume_confirmation,
        offer_archive=gui.offer_archive,
        get_download_workspace_dir=gui.get_download_workspace_dir,
        get_start_chapter_order=lambda: gui.start_var.get(),
        set_start_chapter_order=lambda v: gui.start_var.set(v),
        get_concurrent_limit=lambda: gui.concurrent_var.get(),
        get_image_concurrent_limit=lambda: gui.image_concurrent_var.get(),
        start_heartbeat=gui.start_operation_heartbeat,
        stop_heartbeat=gui.stop_operation_heartbeat,
        cache_manga_detail=gui.cache_manga_detail,
        get_cached_manga_detail=gui.get_cached_manga_detail,
        find_library_entry_by_source_url=gui.find_library_entry_by_source_url,
        get_library_search_roots=gui.get_library_search_roots,
        is_site_access_blocked_error=gui.is_site_access_blocked_error,
        is_site_unreachable_error=gui.is_site_unreachable_error,
        handle_site_access_blocked=gui.handle_site_access_blocked_error,
        handle_site_unreachable=gui.handle_site_unreachable_error,
    )

    controller = DownloadController(
        deps=deps,
        resume_data_file=gui.resume_data_file,
        legacy_resume_data_file=gui.legacy_resume_data_file,
    )
    return GuiDownloadService(controller)


def build_headless_download_service() -> HeadlessDownloadService:
    return HeadlessDownloadService()
