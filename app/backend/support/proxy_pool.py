"""代理池管理：获取、验证、持久化代理。"""
from __future__ import annotations

import json
import os
import threading
import time
from typing import Dict, List, Optional

import requests

from .storage_paths import get_storage_root_dir, APP_STATE_DIR_NAME


class ProxyPool:
    def __init__(self):
        self.proxies: List[str] = []
        self.lock = threading.Lock()
        self.proxy_sources = [
            "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/http.txt",
            "https://raw.githubusercontent.com/prxchk/proxy-list/main/http.txt"
        ]
        self.enabled = False
        self._last_fetch_time = 0
        self._last_fetch_attempt_time = 0
        self._fetch_interval = 600
        self.validation_mode = "relaxed"
        self._strict_validation_targets = (
            "https://httpbin.org/ip",
            "https://www.cloudflare.com/cdn-cgi/trace",
        )
        self._relaxed_validation_targets = (
            "https://httpbin.org/ip",
            "https://www.cloudflare.com/cdn-cgi/trace",
            "http://httpbin.org/ip",
        )
        self._persistence_ttl = 3600
        self._persistence_cache_path: Optional[str] = None
        self._persistence_loaded = False

    def _new_session(self):
        session = requests.Session()
        session.trust_env = False
        return session

    def _get_persistence_cache_path(self) -> str:
        if self._persistence_cache_path is None:
            try:
                self._persistence_cache_path = os.path.join(
                    get_storage_root_dir(), APP_STATE_DIR_NAME, "verified_proxies.json"
                )
            except Exception:
                self._persistence_cache_path = ""
        return self._persistence_cache_path

    def _load_persisted_proxies(self) -> List[str]:
        cache_path = self._get_persistence_cache_path()
        if not cache_path or not os.path.exists(cache_path):
            return []
        try:
            with open(cache_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            if not isinstance(data, dict):
                return []
            ts = data.get("timestamp", 0)
            if time.time() - ts > self._persistence_ttl:
                return []
            proxies = data.get("proxies", [])
            if isinstance(proxies, list):
                return [p for p in proxies if isinstance(p, str)]
        except Exception:
            pass
        return []

    def _save_persisted_proxies(self, proxies: List[str]) -> None:
        cache_path = self._get_persistence_cache_path()
        if not cache_path:
            return
        try:
            os.makedirs(os.path.dirname(cache_path), exist_ok=True)
            tmp_path = cache_path + ".tmp"
            with open(tmp_path, "w", encoding="utf-8") as f:
                json.dump({"timestamp": time.time(), "proxies": proxies}, f)
            os.replace(tmp_path, cache_path)
        except Exception:
            pass

    def fetch_proxies(self) -> List[str]:
        all_proxies: List[str] = []
        for source_url in self.proxy_sources:
            try:
                resp = requests.get(source_url, timeout=10)
                resp.raise_for_status()
                lines = resp.text.strip().splitlines()
                for line in lines:
                    proxy = line.strip()
                    if proxy and ":" in proxy:
                        all_proxies.append(f"http://{proxy}")
            except Exception:
                continue
        return all_proxies

    def validate_proxy(self, proxy: str, timeout: int = 5) -> bool:
        targets = self._relaxed_validation_targets if self.validation_mode == "relaxed" else self._strict_validation_targets
        for target in targets:
            try:
                resp = requests.get(target, proxies={"http": proxy, "https": proxy}, timeout=timeout)
                if resp.status_code != 200:
                    return False
            except Exception:
                return False
        return True

    def get_proxy(self) -> Optional[str]:
        if not self.enabled:
            return None
        with self.lock:
            if not self._persistence_loaded:
                self._persistence_loaded = True
                persisted = self._load_persisted_proxies()
                if persisted:
                    self.proxies = persisted
                    self._last_fetch_time = time.time()
            now = time.time()
            if not self.proxies or now - self._last_fetch_time > self._fetch_interval:
                if now - self._last_fetch_attempt_time > 60:
                    self._last_fetch_attempt_time = now
                    new_proxies = self.fetch_proxies()
                    if new_proxies:
                        self.proxies = new_proxies
                        self._last_fetch_time = now
                        self._save_persisted_proxies(new_proxies)
            if self.proxies:
                return self.proxies[0]
        return None

    def report_failure(self, proxy: str) -> None:
        with self.lock:
            if proxy in self.proxies:
                self.proxies.remove(proxy)

    def get_random_proxy(self) -> Optional[str]:
        import random
        with self.lock:
            if self.proxies:
                return random.choice(self.proxies)
        return None


# 全局代理池实例
proxy_pool = ProxyPool()
