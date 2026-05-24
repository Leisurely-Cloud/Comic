"""HTTP 请求工具函数。"""
from __future__ import annotations

import sys
import builtins
import threading
import time
from typing import Optional

import requests
from requests.adapters import HTTPAdapter

from .proxy_pool import proxy_pool


def _safe_print(*args, **kwargs):
    encoding = getattr(sys.stdout, "encoding", None) or "utf-8"
    safe_args = []
    for arg in args:
        text = str(arg)
        safe_text = text.encode(encoding, errors="replace").decode(encoding, errors="replace")
        safe_args.append(safe_text)
    return builtins.print(*safe_args, **kwargs)


print = _safe_print

# 打印锁，防止多线程打印错乱
print_lock = threading.Lock()

# User-Agent 池
USER_AGENTS = [
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
]


class OperationCancelledError(Exception):
    """用于在 GUI 中区分"用户主动停止"与"请求失败"两种情况。"""


def should_stop(stop_event: Optional[threading.Event] = None) -> bool:
    """统一判断是否需要停止下载。"""
    return stop_event is not None and stop_event.is_set()


def get_session() -> requests.Session:
    """获取带连接池的 requests Session。"""
    session = requests.Session()
    adapter = HTTPAdapter(pool_connections=10, pool_maxsize=20)
    session.mount("http://", adapter)
    session.mount("https://", adapter)
    return session


def safe_request(
    url: str,
    timeout: int = 10,
    retries: int = 5,
    delay: int = 1,
    headers: Optional[dict] = None,
    stop_event: Optional[threading.Event] = None,
    stream: bool = False,
) -> Optional[requests.Response]:
    """安全的 HTTP GET 请求，支持重试、代理、停止事件。"""
    import random

    if headers is None:
        headers = {"User-Agent": random.choice(USER_AGENTS)}

    session = get_session()
    last_error = None

    for attempt in range(retries):
        if should_stop(stop_event):
            raise OperationCancelledError("下载已取消")

        try:
            proxy = proxy_pool.get_random_proxy() if proxy_pool.enabled else None
            resp = session.get(
                url,
                timeout=timeout,
                headers=headers,
                proxies={"http": proxy, "https": proxy} if proxy else None,
                stream=stream,
            )
            resp.raise_for_status()
            return resp
        except requests.exceptions.RequestException as e:
            last_error = e
            if proxy and "proxy" in str(e).lower():
                proxy_pool.report_failure(proxy)
            if attempt < retries - 1:
                time.sleep(delay * (attempt + 1))

    if last_error:
        raise last_error
    return None


def _api_fetch_json(url: str, referer: str = "", timeout: int = 15) -> Optional[dict]:
    """获取 JSON API 响应。"""
    import random

    headers = {
        "User-Agent": random.choice(USER_AGENTS),
        "Accept": "application/json, text/plain, */*",
    }
    if referer:
        headers["Referer"] = referer

    try:
        resp = safe_request(url, timeout=timeout, headers=headers)
        if resp:
            return resp.json()
    except Exception:
        pass
    return None
