"""GUI 日志面板：批量刷新 + tag 自动分类 + stdout/stderr 重定向。

老实现把 15 个方法塞在 ComicDownloaderGUI 里，和下载、搜索、库管理等逻辑混在一处。
这里聚合成一个 LogPanel 类：外面只看 log/log_raw/clear/start/stop/detach 这几件事，
内部的队列、Tk after 调度、tag 推断、ANSI/URL 清洗都不再暴露。
"""
from __future__ import annotations

import queue
import re
import time
import tkinter as tk
from typing import Callable


FLUSH_INTERVAL_MS = 60
FLUSH_BATCH_SIZE = 200
AUTO_SCROLL_THRESHOLD = 0.995


_WEB_URL_PATTERN = re.compile(r"https?://\S+")
_ANSI_PATTERN = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
_PLACEHOLDER_PATTERN = re.compile(r"^[-=~.#_\s]+$")
_HANGING_COMMA_PATTERN = re.compile(r"\(\s*,\s*")
_EMPTY_PARENS_PATTERN = re.compile(r"\(\s*\)")


def strip_web_urls(message: str) -> str:
    return _WEB_URL_PATTERN.sub("", message or "")


def strip_ansi_sequences(message: str) -> str:
    return _ANSI_PATTERN.sub("", message or "")


def normalize_log_message(message: str) -> str:
    """清理链接、ANSI 控制序列和多余空白，回到便于阅读的单行中文形式。"""
    text = strip_ansi_sequences(message)
    text = strip_web_urls(text)
    text = _HANGING_COMMA_PATTERN.sub("，", text)
    text = _EMPTY_PARENS_PATTERN.sub("", text)
    text = " ".join(text.split())
    return text.strip(" :-")


def _should_display_raw(message: str) -> bool:
    text = (message or "").strip()
    if not text:
        return False
    if _PLACEHOLDER_PATTERN.fullmatch(text):
        return False
    if text in {"...", "…"}:
        return False
    return True


def infer_log_tag(message: str, default: str = "info") -> str:
    text = message.lower()
    if any(t in message for t in ["❌", "出错", "错误"]) or any(
        t in text for t in ["failed", "error", "exception", "traceback"]
    ):
        return "error"
    if any(t in message for t in ["⚠️", "跳过", "暂停"]) or any(
        t in text for t in ["warning", "skip", "skipped", "retry"]
    ):
        return "warning"
    if any(t in message for t in ["✅", "完成", "成功", "已恢复"]) or any(
        t in text for t in ["completed", "downloaded", "saved", "loaded"]
    ):
        return "success"
    if any(t in message for t in ["📂", "保存目录", "保存在"]):
        return "path"
    if any(t in message for t in ["🔍", "准备下载"]) or any(
        t in text for t in ["processing", "analyzing", "fetching", "downloading", "queued", "validating"]
    ):
        return "debug"
    if any(t in message for t in ["🛑", "停止", "进度"]) or any(
        t in text for t in ["cancelled", "stopped", "progress"]
    ):
        return "status"
    if "http://" in text or "https://" in text:
        return "muted"
    return default


class StdIoRedirector:
    """把 stdout/stderr 按行喂给回调；LogPanel 负责后续格式化与入队。"""

    def __init__(self, on_line: Callable[[str], None], is_closing: Callable[[], bool]):
        self._on_line = on_line
        self._is_closing = is_closing
        self._buffer = ""

    def write(self, data):
        if self._is_closing() or not data:
            return
        self._buffer += data.replace("\r", "\n")
        while "\n" in self._buffer:
            line, self._buffer = self._buffer.split("\n", 1)
            if line.strip():
                self._on_line(line.strip())

    def flush(self):
        if self._buffer.strip() and not self._is_closing():
            self._on_line(self._buffer.strip())
        self._buffer = ""


class LogPanel:
    """批量刷新的 tkinter Text 日志面板。

    调用方只需要：
        panel = LogPanel(root, text_widget, is_closing=lambda: self._closing)
        panel.start()
        panel.log("..."); panel.log_raw("...")
        panel.stop()
    """

    def __init__(
        self,
        root,
        text_widget,
        *,
        max_lines: int = 800,
        is_closing: Callable[[], bool] = lambda: False,
    ):
        self._root = root
        self._text = text_widget
        self._max_lines = max_lines
        self._is_closing = is_closing
        self._queue: queue.Queue = queue.Queue()
        self._flush_job = None

    # --- 生命周期 ---
    def start(self):
        self._schedule_flush()

    def stop(self):
        if self._flush_job is not None:
            try:
                self._root.after_cancel(self._flush_job)
            except Exception:
                pass
            self._flush_job = None

    # --- 公开 API ---
    def log(self, message: str, tag: str = "info"):
        """对应原 log_message：normalize + 加时间戳 + 推断 tag。"""
        cleaned = normalize_log_message(message)
        if not cleaned:
            return
        timestamp = time.strftime("%H:%M:%S")
        self._enqueue(f"[{timestamp}] {cleaned}\n", infer_log_tag(cleaned, tag))

    def log_raw(self, message: str):
        """对应原 log_raw_output：仅用于被重定向的 stdout/stderr 行。"""
        cleaned = normalize_log_message(message)
        if not _should_display_raw(cleaned):
            return
        self._enqueue(f"{cleaned}\n", infer_log_tag(cleaned, "info"))

    def safe_append(self, message: str, tag: str = "info"):
        """不做 normalize，只根据内容推断 tag。预格式化的文本走这里。"""
        self._enqueue(message, infer_log_tag(message, tag))

    def append_line(self, message: str, tag: str = "info"):
        """透传：调用方已经准备好最终文本和 tag。"""
        self._enqueue(message, tag)

    def clear(self):
        try:
            while True:
                self._queue.get_nowait()
        except queue.Empty:
            pass
        self._text.delete(1.0, tk.END)

    # --- 内部 ---
    def _enqueue(self, message: str, tag: str):
        if self._is_closing() or not message:
            return
        self._queue.put((message, tag))

    def _schedule_flush(self):
        if self._is_closing() or not self._root.winfo_exists():
            return
        if self._flush_job is None:
            self._flush_job = self._root.after(FLUSH_INTERVAL_MS, self._flush)

    def _flush(self):
        self._flush_job = None
        if self._is_closing() or not self._root.winfo_exists() or not self._text.winfo_exists():
            return

        pending = []
        try:
            while len(pending) < FLUSH_BATCH_SIZE:
                pending.append(self._queue.get_nowait())
        except queue.Empty:
            pass

        if not pending:
            self._schedule_flush()
            return

        # 只有用户已贴近底部时才 follow，避免向上翻阅被强制拉回
        try:
            _, yview_bottom = self._text.yview()
            auto_scroll = yview_bottom >= AUTO_SCROLL_THRESHOLD
        except Exception:
            auto_scroll = True

        # 一次 insert 调用喂入多段 (text, tag)，减少 Tcl 往返
        insert_args = []
        for message, tag in pending:
            insert_args.append(message)
            insert_args.append((tag,) if tag else ())
        self._text.insert(tk.END, *insert_args)

        self._trim()
        if auto_scroll:
            self._text.see(tk.END)

        self._schedule_flush()

    def _trim(self):
        try:
            total_lines = int(self._text.index("end-1c").split(".")[0])
        except Exception:
            return
        overflow = total_lines - self._max_lines
        if overflow > 0:
            self._text.delete("1.0", f"{overflow + 1}.0")
