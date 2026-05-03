"""HTTP server for the backend API."""
from __future__ import annotations

import json
import os
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Dict, Optional
from urllib.parse import parse_qs, urlparse

# Ensure the app root is on sys.path so `backend.*` imports work when run as a script.
_THIS_DIR = os.path.dirname(os.path.abspath(__file__))
_PARENT_DIR = os.path.dirname(os.path.dirname(_THIS_DIR))
if _PARENT_DIR not in sys.path:
    sys.path.insert(0, _PARENT_DIR)

from backend.api.application import Application


def _configure_stdio() -> None:
    for stream_name in ("stdout", "stderr"):
        stream = getattr(sys, stream_name, None)
        reconfigure = getattr(stream, "reconfigure", None)
        if callable(reconfigure):
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except Exception:
                pass


_configure_stdio()


app = Application()
_ALLOWED_ORIGIN_HOSTS = {"127.0.0.1", "localhost"}


def _is_allowed_origin(origin: str) -> bool:
    if not origin or origin == "null":
        return False
    try:
        parsed = urlparse(origin)
    except Exception:
        return False
    if parsed.scheme not in {"http", "https"}:
        return False
    return parsed.hostname in _ALLOWED_ORIGIN_HOSTS


def _json_response(handler: BaseHTTPRequestHandler, status: int, data: Any) -> None:
    body = json.dumps(data, ensure_ascii=False).encode("utf-8")
    handler.send_response(status)
    handler.send_header("Content-Type", "application/json; charset=utf-8")
    handler.send_header("Content-Length", str(len(body)))
    _write_cors_headers(handler)
    handler.end_headers()
    handler.wfile.write(body)


def _write_cors_headers(handler: BaseHTTPRequestHandler) -> None:
    origin = handler.headers.get("Origin", "")
    if origin and _is_allowed_origin(origin):
        handler.send_header("Access-Control-Allow-Origin", origin)
        handler.send_header("Vary", "Origin")


def _error_response(handler: BaseHTTPRequestHandler, status: int, code: str, message: str) -> None:
    _json_response(handler, status, {"error": {"code": code, "message": message}})


def _read_json_body(handler: BaseHTTPRequestHandler) -> Dict[str, Any]:
    length = int(handler.headers.get("Content-Length", 0))
    if length == 0:
        return {}
    raw = handler.rfile.read(length)
    return json.loads(raw.decode("utf-8"))


class ApiHandler(BaseHTTPRequestHandler):
    """Routes requests to the Application layer."""

    protocol_version = "HTTP/1.1"

    def log_message(self, fmt: str, *args: Any) -> None:
        pass

    def _ensure_allowed_origin(self) -> bool:
        origin = self.headers.get("Origin", "")
        if origin and not _is_allowed_origin(origin):
            _error_response(self, 403, "forbidden_origin", "不允许的请求来源")
            return False
        return True

    # ------------------------------------------------------------------
    # HTTP method dispatch
    # ------------------------------------------------------------------
    def do_GET(self) -> None:
        if not self._ensure_allowed_origin():
            return

        parsed = urlparse(self.path)
        path = parsed.path.rstrip("/")
        params = {k: v[0] for k, v in parse_qs(parsed.query).items()}

        try:
            if path == "/api/health":
                _json_response(self, 200, app.get_health())
            elif path == "/api/downloads":
                _json_response(self, 200, {"items": app.get_downloads()})
            elif path.startswith("/api/downloads/") and path.endswith("/events"):
                parts = path.split("/")
                task_id = parts[3] if len(parts) > 3 else ""
                self._serve_sse(task_id, params)
                return
            elif path.startswith("/api/downloads/"):
                task_id = path.split("/")[-1]
                task = app.get_download(task_id)
                if task:
                    _json_response(self, 200, task)
                else:
                    _error_response(self, 404, "not_found", "任务不存在")
            elif path == "/api/settings":
                _json_response(self, 200, app.get_settings())
            elif path == "/api/library":
                page = max(int(params.get("page", "1") or 1), 1)
                page_size = max(int(params.get("page_size", "20") or 20), 1)
                _json_response(
                    self,
                    200,
                    app.get_library(
                        site_key=params.get("site_key", ""),
                        keyword=params.get("keyword", ""),
                        page=page,
                        page_size=page_size,
                    ),
                )
            elif path == "/api/library/check-updates":
                _json_response(self, 200, app.check_library_updates())
            else:
                _error_response(self, 404, "not_found", f"未知路径: {path}")
        except Exception as ex:
            _error_response(self, 500, "internal_error", str(ex))

    def _serve_sse(self, task_id: str, params: Dict[str, str]) -> None:
        if not self._ensure_allowed_origin():
            return

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "keep-alive")
        _write_cors_headers(self)
        self.end_headers()

        try:
            event_id = int(params.get("last_event_id", "0"))
        except ValueError:
            event_id = 0

        try:
            while True:
                task = app.get_download(task_id)
                if not task:
                    data = json.dumps({"error": "任务不存在"}, ensure_ascii=False)
                    self.wfile.write(f"event: error\ndata: {data}\n\n".encode("utf-8"))
                    self.wfile.flush()
                    break

                event_id += 1
                payload = json.dumps(task, ensure_ascii=False)
                self.wfile.write(f"id: {event_id}\nevent: update\ndata: {payload}\n\n".encode("utf-8"))
                self.wfile.flush()

                if task.get("status") in ("completed", "stopped", "failed", "partial"):
                    break

                time.sleep(0.5)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def do_POST(self) -> None:
        if not self._ensure_allowed_origin():
            return

        parsed = urlparse(self.path)
        path = parsed.path.rstrip("/")

        try:
            body = _read_json_body(self)

            if path == "/api/search":
                query = body.get("query", "").strip()
                site = body.get("site", "baozimh")
                page = int(body.get("page", 1))
                if not query:
                    _error_response(self, 400, "bad_request", "搜索关键词不能为空")
                    return
                results = app.search(query, site=site, page=page)
                _json_response(self, 200, {"items": results, "total": len(results)})

            elif path == "/api/resolve":
                url = body.get("url", "").strip()
                site = body.get("site", "")
                if not url:
                    _error_response(self, 400, "bad_request", "URL不能为空")
                    return
                detail = app.resolve(url, site=site)
                _json_response(self, 200, detail)

            elif path == "/api/downloads":
                url = body.get("url", "").strip()
                site = body.get("site", "")
                chapters = body.get("chapters")
                if not url:
                    _error_response(self, 400, "bad_request", "URL不能为空")
                    return
                task = app.create_download(url, site=site, chapters=chapters)
                _json_response(self, 201, task)

            elif path.startswith("/api/downloads/") and path.endswith("/pause"):
                task_id = path.split("/")[-2]
                if app.pause_download(task_id):
                    task = app.get_download(task_id)
                    _json_response(self, 200, {"status": (task or {}).get("status", "paused")})
                else:
                    _error_response(self, 400, "bad_state", "任务无法暂停")

            elif path.startswith("/api/downloads/") and path.endswith("/resume"):
                task_id = path.split("/")[-2]
                if app.resume_download(task_id):
                    task = app.get_download(task_id)
                    _json_response(self, 200, {"status": (task or {}).get("status", "running")})
                else:
                    _error_response(self, 400, "bad_state", "任务无法继续")

            elif path.startswith("/api/downloads/") and path.endswith("/stop"):
                task_id = path.split("/")[-2]
                if app.stop_download(task_id):
                    task = app.get_download(task_id)
                    _json_response(self, 200, {"status": (task or {}).get("status", "stopping")})
                else:
                    _error_response(self, 400, "bad_state", "任务无法停止")

            elif path == "/api/settings":
                _json_response(self, 200, app.update_settings(body))

            elif path == "/api/library/export-cbz":
                root_dir = body.get("root_dir", "")
                if not root_dir:
                    _error_response(self, 400, "bad_request", "目录不能为空")
                    return
                _json_response(self, 200, app.export_cbz(root_dir))

            else:
                _error_response(self, 404, "not_found", f"未知路径: {path}")

        except Exception as ex:
            _error_response(self, 500, "internal_error", str(ex))

    def do_OPTIONS(self) -> None:
        if not self._ensure_allowed_origin():
            return
        self.send_response(204)
        _write_cors_headers(self)
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.end_headers()


def run_server(host: str = "127.0.0.1", port: int = 8765) -> None:
    """Start the HTTP server."""
    server = ThreadingHTTPServer((host, port), ApiHandler)
    print(f"Backend API running on http://{host}:{port}/")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("Shutting down...")
    finally:
        server.server_close()


if __name__ == "__main__":
    run_server()
