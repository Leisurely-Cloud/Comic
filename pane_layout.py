"""Pane 布局管理：sash 位置的保存、恢复与调度。"""
from __future__ import annotations

from typing import Callable, Optional


class PaneLayout:
    def __init__(
        self,
        root,
        content_pane,
        ranking_pane,
        is_closing: Callable[[], bool],
    ):
        self._root = root
        self._content_pane = content_pane
        self._ranking_pane = ranking_pane
        self._is_closing = is_closing

        self._saved_content_sash: Optional[int] = None
        self._saved_ranking_sash: Optional[int] = None
        self._pane_restore_job = None
        self._pane_restore_followup_job = None
        self._window_was_iconic = False

    def configure_initial(self):
        if self._is_closing():
            return
        try:
            total_width = self._content_pane.winfo_width()
            if total_width > 0:
                discovery_width = int(total_width * 0.54)
                discovery_width = max(720, discovery_width)
                discovery_width = min(discovery_width, max(total_width - 430, 720))
                self._content_pane.sashpos(0, discovery_width)
                self._saved_content_sash = discovery_width
        except Exception:
            pass
        try:
            total_width = self._ranking_pane.winfo_width()
            if total_width > 0:
                list_width = int(total_width * 0.47)
                list_width = max(330, list_width)
                list_width = min(list_width, max(total_width - 290, 330))
                self._ranking_pane.sashpos(0, list_width)
                self._saved_ranking_sash = list_width
        except Exception:
            pass

    @staticmethod
    def _clamp(value, total_width, min_width, trailing_min_width):
        if total_width <= 0:
            return int(value)
        max_width = max(total_width - trailing_min_width, min_width)
        return min(max(int(value), min_width), max_width)

    def capture(self):
        if self._is_closing():
            return
        try:
            if self._content_pane.winfo_exists():
                self._saved_content_sash = self._content_pane.sashpos(0)
        except Exception:
            pass
        try:
            if self._ranking_pane.winfo_exists():
                self._saved_ranking_sash = self._ranking_pane.sashpos(0)
        except Exception:
            pass

    def restore(self):
        if self._is_closing():
            return
        try:
            if self._root.state() == "iconic":
                return
        except Exception:
            return

        restored = False
        try:
            total_width = self._content_pane.winfo_width()
            if total_width > 0 and self._saved_content_sash is not None:
                discovery_width = self._clamp(self._saved_content_sash, total_width, 720, 430)
                self._content_pane.sashpos(0, discovery_width)
                self._saved_content_sash = discovery_width
                restored = True
        except Exception:
            pass

        try:
            total_width = self._ranking_pane.winfo_width()
            if total_width > 0 and self._saved_ranking_sash is not None:
                list_width = self._clamp(self._saved_ranking_sash, total_width, 330, 290)
                self._ranking_pane.sashpos(0, list_width)
                self._saved_ranking_sash = list_width
                restored = True
        except Exception:
            pass

        if not restored:
            self.configure_initial()

    def _run_restore(self):
        self._pane_restore_job = None
        self.restore()

    def _run_restore_followup(self):
        self._pane_restore_followup_job = None
        self.restore()

    def schedule_restore(self, delay=40):
        if self._is_closing():
            return
        self._window_was_iconic = False
        if self._pane_restore_job is not None:
            try:
                self._root.after_cancel(self._pane_restore_job)
            except Exception:
                pass
        if self._pane_restore_followup_job is not None:
            try:
                self._root.after_cancel(self._pane_restore_followup_job)
            except Exception:
                pass
        self._pane_restore_job = self._root.after(delay, self._run_restore)
        self._pane_restore_followup_job = self._root.after(delay + 140, self._run_restore_followup)

    def on_window_unmap(self, event=None):
        if event is not None and event.widget is not self._root:
            return
        try:
            state = self._root.state()
        except Exception:
            return
        if state == "iconic":
            self.capture()
            self._window_was_iconic = True

    def on_window_map(self, event=None):
        if event is not None and event.widget is not self._root:
            return
        if self._window_was_iconic:
            self.schedule_restore(delay=30)

    def on_window_configure(self, event=None):
        if event is not None and event.widget is not self._root:
            return
        if not self._window_was_iconic:
            return
        try:
            if self._root.state() != "iconic":
                self.schedule_restore(delay=20)
        except Exception:
            pass
