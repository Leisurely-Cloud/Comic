from __future__ import annotations

import os
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from backend.api.application import Application


class _FakeFailingAdapter:
    key = "fake"
    display_name = "Fake"
    supports_download = True

    def get_manga_info_from_url(self, url: str):
        return None, None, None


class _FakeSuccessfulAdapter:
    key = "fake"
    display_name = "Fake"
    supports_download = True

    def get_manga_info_from_url(self, url: str):
        return "manga-id", "fake-manga", "chapter-b"

    def get_all_chapters(self, manga_id):
        self._assert_manga_id(manga_id)
        return "测试漫画", [
            {"slug": "chapter-a", "order": 0, "title": "第1话"},
            {"slug": "chapter-b", "order": 1, "title": "第2话"},
        ]

    def build_chapter_url_template(self, manga_slug: str) -> str:
        self._assert_manga_slug(manga_slug)
        return "https://example.test/{slug}"

    def adjust_download_settings(self, chapter_concurrency: int, image_concurrency: int):
        return chapter_concurrency, image_concurrency, ""

    def get_chapter_retry_limit(self) -> int:
        return 0

    def should_retry_download_error(self, error: Exception) -> bool:
        return False

    def download_chapter_images(
        self,
        chapter_slug,
        base_url_template,
        root_dir,
        max_concurrent_images=5,
        stop_event=None,
        show_progress=True,
    ):
        chapter_dir = os.path.join(root_dir, f"001_{chapter_slug}")
        os.makedirs(chapter_dir, exist_ok=True)
        with open(os.path.join(chapter_dir, "001.jpg"), "wb") as file_obj:
            file_obj.write(b"img")
        return 1, None, {"slug": chapter_slug}

    @staticmethod
    def _assert_manga_id(manga_id):
        assert manga_id == "manga-id"

    @staticmethod
    def _assert_manga_slug(manga_slug):
        assert manga_slug == "fake-manga"


class _ControllableAdapter:
    key = "fake"
    display_name = "Fake"
    supports_download = True

    def __init__(self) -> None:
        self.started = []
        self.allow_finish = threading.Event()
        self.started_event = threading.Event()

    def get_manga_info_from_url(self, url: str):
        return "manga-id", "fake-manga", None

    def get_all_chapters(self, manga_id):
        return "测试漫画", [
            {"slug": "chapter-a", "order": 0, "title": "第1话"},
            {"slug": "chapter-b", "order": 1, "title": "第2话"},
        ]

    def build_chapter_url_template(self, manga_slug: str) -> str:
        return "https://example.test/{slug}"

    def adjust_download_settings(self, chapter_concurrency: int, image_concurrency: int):
        return chapter_concurrency, image_concurrency, ""

    def get_chapter_retry_limit(self) -> int:
        return 0

    def should_retry_download_error(self, error: Exception) -> bool:
        return False

    def download_chapter_images(
        self,
        chapter_slug,
        base_url_template,
        root_dir,
        max_concurrent_images=5,
        stop_event=None,
        show_progress=True,
    ):
        self.started.append(chapter_slug)
        self.started_event.set()
        while not self.allow_finish.wait(0.05):
            if stop_event is not None and stop_event.is_set():
                return 0, None, {"slug": chapter_slug}
        chapter_dir = os.path.join(root_dir, f"001_{chapter_slug}")
        os.makedirs(chapter_dir, exist_ok=True)
        with open(os.path.join(chapter_dir, "001.jpg"), "wb") as file_obj:
            file_obj.write(b"img")
        return 1, None, {"slug": chapter_slug}


class ApplicationDownloadTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp_dir.cleanup)
        self._old_download_dir = os.environ.get("COMIC_DOWNLOAD_DIR")
        os.environ["COMIC_DOWNLOAD_DIR"] = self._temp_dir.name
        self.addCleanup(self._restore_env)

    def _restore_env(self) -> None:
        if self._old_download_dir is None:
            os.environ.pop("COMIC_DOWNLOAD_DIR", None)
        else:
            os.environ["COMIC_DOWNLOAD_DIR"] = self._old_download_dir

    def _wait_for_terminal_status(self, app: Application, task_id: str, timeout: float = 5.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            task = app.get_download(task_id)
            if task and task["status"] in {"completed", "failed", "partial", "stopped"}:
                return task
            time.sleep(0.05)
        self.fail(f"task {task_id} did not reach terminal status within {timeout}s")

    def _wait_for_status(self, app: Application, task_id: str, expected_status: str, timeout: float = 5.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            task = app.get_download(task_id)
            if task and task["status"] == expected_status:
                return task
            time.sleep(0.05)
        self.fail(f"task {task_id} did not reach status {expected_status!r} within {timeout}s")

    def test_invalid_url_marks_task_failed_instead_of_fake_completion(self):
        app = Application()
        with patch("backend.api.application.resolve_adapter_from_url", return_value=_FakeFailingAdapter()):
            task = app.create_download("https://example.invalid/manga", site="fake")
            final_task = self._wait_for_terminal_status(app, task["id"])

        self.assertEqual(final_task["status"], "failed")
        self.assertEqual(final_task["progress"], 0.0)
        self.assertIn("无法解析漫画链接", final_task["task_error"]["message"])

    def test_real_download_updates_progress_and_completes(self):
        app = Application()
        adapter = _FakeSuccessfulAdapter()
        with patch("backend.api.application.resolve_adapter_from_url", return_value=adapter):
            task = app.create_download("https://example.test/manga/chapter-b", site="fake")
            final_task = self._wait_for_terminal_status(app, task["id"])

        self.assertEqual(final_task["status"], "completed")
        self.assertEqual(final_task["progress"], 100.0)
        self.assertEqual(final_task["task_error"], None)
        self.assertTrue(any("开始章节" in entry["message"] for entry in final_task["logs"]))
        self.assertTrue(any("下载完成" in entry["message"] for entry in final_task["logs"]))

    def test_pause_then_resume_applies_at_chapter_boundary(self):
        app = Application()
        adapter = _ControllableAdapter()
        with patch("backend.api.application.resolve_adapter_from_url", return_value=adapter):
            task = app.create_download("https://example.test/manga", site="fake")
            self.assertTrue(adapter.started_event.wait(timeout=2), "first chapter did not start")

            self.assertTrue(app.pause_download(task["id"]))
            pausing_task = self._wait_for_status(app, task["id"], "pausing")
            self.assertIn("正在暂停", pausing_task["status_text"])

            adapter.allow_finish.set()
            paused_task = self._wait_for_status(app, task["id"], "paused")
            self.assertEqual(paused_task["progress"], 50.0)
            self.assertEqual(adapter.started, ["chapter-a"])

            self.assertTrue(app.resume_download(task["id"]))
            final_task = self._wait_for_terminal_status(app, task["id"])
            self.assertEqual(final_task["status"], "completed")
            self.assertEqual(adapter.started, ["chapter-a", "chapter-b"])

    def test_stop_interrupts_before_next_chapter(self):
        app = Application()
        adapter = _ControllableAdapter()
        with patch("backend.api.application.resolve_adapter_from_url", return_value=adapter):
            task = app.create_download("https://example.test/manga", site="fake")
            self.assertTrue(adapter.started_event.wait(timeout=2), "first chapter did not start")

            self.assertTrue(app.stop_download(task["id"]))
            stopping_task = self._wait_for_status(app, task["id"], "stopping")
            self.assertIn("正在停止", stopping_task["status_text"])

            final_task = self._wait_for_terminal_status(app, task["id"])
            self.assertEqual(final_task["status"], "stopped")
            self.assertEqual(final_task["progress"], 0.0)
            self.assertEqual(adapter.started, ["chapter-a"])


if __name__ == "__main__":
    unittest.main()
