"""UI 线程调度：把后台线程提交的任务攒进队列，在 Tk 主线程上每 16ms 批量回调。

从后台 HTTP / 下载线程直接碰 tkinter 会触发 "main thread is not in main loop" 崩溃。
统一走 UiScheduler.run_on_ui_thread：
- 调用方如果已经在 UI 线程，同步执行
- 否则入队，下一次 after 回调批量处理（每轮最多 200 条避免长时间卡主线程）
"""
from __future__ import annotations

import queue
import threading
from typing import Callable


PUMP_INTERVAL_MS = 16
FLUSH_BATCH_SIZE = 200


class UiScheduler:
    def __init__(
        self,
        root,
        *,
        is_closing: Callable[[], bool] = lambda: False,
        on_task_error: Callable[[BaseException], None] = lambda exc: None,
    ):
        self._root = root
        self._is_closing = is_closing
        self._on_task_error = on_task_error
        self._queue: queue.Queue = queue.Queue()
        self._thread_ident = threading.get_ident()
        self._pump_job = None

    # --- 生命周期 ---
    def start(self):
        self._schedule_pump()

    def stop(self):
        if self._pump_job is not None:
            try:
                self._root.after_cancel(self._pump_job)
            except Exception:
                pass
            self._pump_job = None

    # --- 公开 API ---
    def run_on_ui_thread(self, func, *args, **kwargs):
        """UI 线程上调用会同步执行；后台线程则入队，由下一次 pump 回调执行。"""
        if self._is_closing():
            return
        if threading.get_ident() == self._thread_ident:
            func(*args, **kwargs)
            return
        self._queue.put((func, args, kwargs))

    # --- 内部 ---
    def _schedule_pump(self):
        if self._is_closing() or not self._root.winfo_exists():
            return
        if self._pump_job is None:
            self._pump_job = self._root.after(PUMP_INTERVAL_MS, self._pump)

    def _pump(self):
        self._pump_job = None
        if self._is_closing() or not self._root.winfo_exists():
            return

        processed = 0
        while processed < FLUSH_BATCH_SIZE:
            try:
                func, args, kwargs = self._queue.get_nowait()
            except queue.Empty:
                break
            try:
                func(*args, **kwargs)
            except Exception as exc:
                try:
                    self._on_task_error(exc)
                except Exception:
                    pass
            processed += 1

        self._schedule_pump()
