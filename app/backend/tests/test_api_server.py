from __future__ import annotations

import http.client
import json
import sys
import threading
import time
import unittest
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from backend.api import server as server_module


class _FakeServerApp:
    def __init__(self) -> None:
        self._download_call_count = 0

    def get_health(self):
        return {"status": "ok", "storage_root": "X:/downloads", "pid": 4321}

    def get_downloads(self):
        return []

    def get_download(self, task_id: str):
        if task_id == "stream":
            self._download_call_count += 1
            status = "running" if self._download_call_count < 20 else "completed"
            return {
                "id": "stream",
                "url": "https://example.test/manga",
                "site": "fake",
                "status": status,
                "status_text": "streaming",
                "progress": 10.0,
                "task_error": None,
                "logs": [],
            }
        if task_id == "created":
            return {
                "id": "created",
                "url": "https://example.test/manga",
                "site": "fake",
                "status": "pending",
                "status_text": "等待开始",
                "progress": 0.0,
                "task_error": None,
                "logs": [],
            }
        return None

    def get_settings(self):
        return {
            "storage_root": "X:/downloads",
            "legacy_root": "X:/legacy",
            "download_runner_configured": True,
            "supported_sites": ["baozimh"],
        }

    def get_library(self, **_kwargs):
        return {"items": [], "total": 0, "page": 1, "page_size": 20}

    def check_library_updates(self):
        return {"items": []}

    def search(self, query: str, site: str = "baozimh", page: int = 1):
        return [{"title": query, "url": f"https://{site}/{page}", "cover_url": "", "latest_chapter": "", "update_time": ""}]

    def resolve(self, url: str, site: str = ""):
        return {
            "title": "resolved",
            "site_name": site or "fake",
            "url": url,
            "latest_chapter": "第1话",
            "chapters": [],
            "cover_url": "",
            "detail_hint": "ok",
        }

    def create_download(self, url: str, site: str = "", chapters=None):
        return {
            "id": "created",
            "url": url,
            "site": site,
            "status": "pending",
            "status_text": "等待开始",
            "progress": 0.0,
            "task_error": None,
            "logs": [],
        }

    def pause_download(self, task_id: str):
        return task_id == "created"

    def resume_download(self, task_id: str):
        return task_id == "created"

    def stop_download(self, task_id: str):
        return task_id == "created"

    def update_settings(self, payload):
        return self.get_settings()

    def export_cbz(self, root_dir: str):
        return {"status": "ok", "message": root_dir}


class ApiServerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls._old_app = server_module.app
        cls._fake_app = _FakeServerApp()
        server_module.app = cls._fake_app
        cls._server = server_module.ThreadingHTTPServer(("127.0.0.1", 0), server_module.ApiHandler)
        cls._thread = threading.Thread(target=cls._server.serve_forever, daemon=True)
        cls._thread.start()
        cls._base_url = f"http://127.0.0.1:{cls._server.server_port}"

    @classmethod
    def tearDownClass(cls) -> None:
        cls._server.shutdown()
        cls._server.server_close()
        cls._thread.join(timeout=5)
        server_module.app = cls._old_app

    def test_health_returns_runtime_fields(self):
        with urlopen(f"{self._base_url}/api/health", timeout=2) as response:
            payload = json.loads(response.read().decode("utf-8"))

        self.assertEqual(payload["status"], "ok")
        self.assertEqual(payload["storage_root"], "X:/downloads")
        self.assertEqual(payload["pid"], 4321)

    def test_post_download_returns_full_snapshot(self):
        request = Request(
            f"{self._base_url}/api/downloads",
            data=json.dumps({"url": "https://example.test/manga", "site": "fake"}).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urlopen(request, timeout=2) as response:
            payload = json.loads(response.read().decode("utf-8"))

        self.assertEqual(payload["id"], "created")
        self.assertEqual(payload["status"], "pending")
        self.assertIn("status_text", payload)
        self.assertIn("progress", payload)

    def test_rejects_cross_origin_requests(self):
        request = Request(
            f"{self._base_url}/api/health",
            headers={"Origin": "https://evil.example"},
        )
        with self.assertRaises(HTTPError) as cm:
            urlopen(request, timeout=2)

        error = cm.exception
        self.assertEqual(error.code, 403)
        payload = json.loads(error.read().decode("utf-8"))
        error.close()
        self.assertEqual(payload["error"]["code"], "forbidden_origin")

    def test_sse_does_not_block_parallel_health_request(self):
        ready = threading.Event()
        release = threading.Event()
        errors: list[BaseException] = []

        def open_stream() -> None:
            try:
                conn = http.client.HTTPConnection("127.0.0.1", self._server.server_port, timeout=5)
                conn.putrequest("GET", "/api/downloads/stream/events")
                conn.endheaders()
                response = conn.getresponse()
                self.assertEqual(response.status, 200)
                ready.set()
                response.read(64)
                release.wait(timeout=3)
                conn.close()
            except BaseException as exc:  # pragma: no cover - propagated below
                errors.append(exc)
                ready.set()

        thread = threading.Thread(target=open_stream, daemon=True)
        thread.start()
        self.assertTrue(ready.wait(timeout=2), "SSE stream did not start")
        if errors:
            raise errors[0]

        started = time.perf_counter()
        with urlopen(f"{self._base_url}/api/health", timeout=2) as response:
            payload = json.loads(response.read().decode("utf-8"))
        elapsed = time.perf_counter() - started

        release.set()
        thread.join(timeout=3)
        if errors:
            raise errors[0]

        self.assertEqual(payload["status"], "ok")
        self.assertLess(elapsed, 1.0, f"health request took too long: {elapsed:.3f}s")


if __name__ == "__main__":
    unittest.main()
