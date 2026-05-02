import os
import re
import time
import argparse
import json
import builtins
import sys
import requests
import threading
from bs4 import BeautifulSoup
from urllib.parse import urljoin, urlparse, parse_qs, quote
from concurrent.futures import ThreadPoolExecutor, as_completed, wait, FIRST_COMPLETED
from typing import Optional, Dict, List, Tuple
from dataclasses import dataclass
try:
    from tqdm import tqdm
except ImportError:
    def tqdm(*args, **kwargs):  # type: ignore[misc]
        if args:
            return args[0]
        return kwargs.get("iterable", [])
import logging
from requests.adapters import HTTPAdapter
from .storage_paths import ensure_storage_root_dir, get_storage_root_dir, APP_STATE_DIR_NAME


def _safe_print(*args, **kwargs):
    encoding = getattr(sys.stdout, "encoding", None) or "utf-8"
    safe_args = []
    for arg in args:
        text = str(arg)
        safe_text = text.encode(encoding, errors="replace").decode(encoding, errors="replace")
        safe_args.append(safe_text)
    return builtins.print(*safe_args, **kwargs)


print = _safe_print

# 🔒 打印锁，防止多线程打印错乱
print_lock = threading.Lock()

BASE_SITE_URL = "https://baozimh.org"

# User-Agent 池
USER_AGENTS = [
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
]


class OperationCancelledError(Exception):
    """用于在 GUI 中区分“用户主动停止”与“请求失败”两种情况。"""


class ProxyPool:
    def __init__(self):
        self.proxies = []
        self.lock = threading.Lock()
        # 扩展的免费代理源，包含几个高可用列表
        self.proxy_sources = [
            "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/http.txt",
            # "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt",
            # "https://api.proxyscrape.com/v2/?request=getproxies&protocol=http&timeout=5000&country=all&ssl=all&anonymity=all",
            "https://raw.githubusercontent.com/prxchk/proxy-list/main/http.txt"
        ]
        self.enabled = False # 默认开启
        self._last_fetch_time = 0
        self._last_fetch_attempt_time = 0
        self._fetch_interval = 600 # 10分钟更新一次
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
        # 已验证代理的磁盘持久化，避免每次重启都要再跑一遍 50 并发 * 300 候选的验证循环
        self._persistence_ttl = 3600  # 1 小时内复用磁盘缓存
        self._persistence_cache_path = None
        self._persistence_loaded = False

    def _new_session(self):
        session = requests.Session()
        session.trust_env = False
        return session

    def _get_persistence_cache_path(self):
        if self._persistence_cache_path is None:
            try:
                self._persistence_cache_path = os.path.join(
                    get_storage_root_dir(), APP_STATE_DIR_NAME, "verified_proxies.json"
                )
            except Exception:
                self._persistence_cache_path = ""
        return self._persistence_cache_path

    def _load_persisted_proxies_if_fresh(self):
        """首次访问时从磁盘恢复上一次运行验证过的代理节点。需在 self.lock 下调用。"""
        if self._persistence_loaded:
            return
        self._persistence_loaded = True

        path = self._get_persistence_cache_path()
        if not path or not os.path.isfile(path):
            return

        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
        except Exception:
            return

        try:
            timestamp = float(data.get("timestamp") or 0)
        except (TypeError, ValueError):
            timestamp = 0
        mode = data.get("validation_mode")
        proxies = data.get("proxies") or []

        if not isinstance(proxies, list):
            return
        if time.time() - timestamp > self._persistence_ttl:
            return
        if mode and mode != self.validation_mode:
            return

        normalized = [str(p).strip() for p in proxies if isinstance(p, str) and str(p).strip()]
        if not normalized:
            return

        self.proxies = normalized
        self._last_fetch_time = timestamp
        age_seconds = max(int(time.time() - timestamp), 0)
        with print_lock:
            print(f"📦 已从本地缓存恢复 {len(self.proxies)} 个代理节点（缓存年龄 {age_seconds} 秒）")

    def _save_persisted_proxies(self):
        """把当前已验证的代理列表原子写入磁盘。调用方需先确认 self.proxies 非空。"""
        path = self._get_persistence_cache_path()
        if not path:
            return
        proxies_snapshot = list(self.proxies)
        if not proxies_snapshot:
            return
        try:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            payload = {
                "timestamp": time.time(),
                "validation_mode": self.validation_mode,
                "proxies": proxies_snapshot,
            }
            tmp_path = path + ".tmp"
            with open(tmp_path, "w", encoding="utf-8") as f:
                json.dump(payload, f, ensure_ascii=False)
            os.replace(tmp_path, path)
        except Exception:
            pass

    def _drop_persisted_proxies(self):
        """验证模式切换或手动清空时，顺带把磁盘缓存失效掉。"""
        path = self._get_persistence_cache_path()
        if not path:
            return
        try:
            if os.path.isfile(path):
                os.remove(path)
        except Exception:
            pass

    def get_validation_mode(self):
        return self.validation_mode

    def get_validation_mode_label(self):
        return "宽松" if self.validation_mode == "relaxed" else "严格"

    def set_validation_mode(self, mode: str):
        normalized = str(mode or "").strip().lower() or "relaxed"
        if normalized not in {"strict", "relaxed"}:
            raise ValueError("代理池验证模式仅支持 strict 或 relaxed")
        with self.lock:
            self.validation_mode = normalized
            self.proxies = []
            self._last_fetch_time = 0
            self._persistence_loaded = True  # 模式切换后不再信任老缓存
            self._drop_persisted_proxies()

    def _get_validation_targets(self):
        if self.validation_mode == "strict":
            return self._strict_validation_targets
        return self._relaxed_validation_targets

    def verify_proxy(self, proxy_ip, stop_event=None):
        """验证单个代理是否可用"""
        if should_stop(stop_event):
            raise OperationCancelledError("已停止内置代理池测试")
        proxy = {
            "http": f"http://{proxy_ip}",
            "https": f"http://{proxy_ip}"
        }
        session = self._new_session()
        targets = self._get_validation_targets()
        required_successes = len(targets) if self.validation_mode == "strict" else 1
        success_count = 0
        for target_url in targets:
            if should_stop(stop_event):
                raise OperationCancelledError("已停止内置代理池测试")
            try:
                resp = session.get(target_url, proxies=proxy, timeout=(4, 6), allow_redirects=True)
                if resp.status_code == 200:
                    success_count += 1
                    if success_count >= required_successes:
                        return proxy_ip
                elif self.validation_mode == "strict":
                    return None
            except Exception:
                if self.validation_mode == "strict":
                    return None
        return proxy_ip if success_count >= required_successes else None

    def fetch_proxies(self, stop_event=None):
        """从网络获取并验证免费代理"""
        if not self.enabled:
            return

        with self.lock:
            if should_stop(stop_event):
                raise OperationCancelledError("已停止内置代理池测试")
            self._load_persisted_proxies_if_fresh()
            if time.time() - self._last_fetch_time < self._fetch_interval and self.proxies:
                return

            self._last_fetch_attempt_time = time.time()
            print("🔄 正在从多个公开源抓取代理列表...")
            raw_proxies = set()
            source_pattern = re.compile(r'^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}:\d+$')

            # 1. 并发获取原始代理列表
            def fetch_source(url):
                if should_stop(stop_event):
                    raise OperationCancelledError("已停止内置代理池测试")
                try:
                    resp = self._new_session().get(url, timeout=10)
                    found = set()
                    if resp.status_code == 200:
                        for line in resp.content.decode("utf-8", errors="replace").strip().splitlines():
                            line = line.strip()
                            if source_pattern.match(line):
                                found.add(line)
                        print(f"    已从代理源抓取 {len(found)} 条候选节点")
                    return found
                except OperationCancelledError:
                    raise
                except Exception as e:
                    print(f"⚠️ 抓取代理源失败: {e}")
                    return set()

            source_executor = ThreadPoolExecutor(max_workers=max(len(self.proxy_sources), 1))
            source_futures = [source_executor.submit(fetch_source, url) for url in self.proxy_sources]
            try:
                pending = set(source_futures)
                while pending:
                    if should_stop(stop_event):
                        raise OperationCancelledError("已停止内置代理池测试")
                    done, pending = wait(pending, timeout=0.25, return_when=FIRST_COMPLETED)
                    for future in done:
                        if should_stop(stop_event):
                            raise OperationCancelledError("已停止内置代理池测试")
                        raw_proxies.update(future.result() or set())
            finally:
                source_executor.shutdown(wait=False, cancel_futures=True)

            if not raw_proxies:
                if should_stop(stop_event):
                    raise OperationCancelledError("已停止内置代理池测试")
                print("⚠️ 当前没有从公开源抓到任何代理节点")
                return

            mode_label = self.get_validation_mode_label()
            if self.validation_mode == "strict":
                mode_desc = "所有 HTTPS 测试目标都必须通过"
            else:
                mode_desc = "任一测试目标通过即可，成功率更高但误判也会更多"
            print(f"🔄 正在验证 {len(raw_proxies)} 个候选节点的可用性（{mode_label}模式：{mode_desc}）...")
            
            # 2. 并发验证代理可用性
            verified_proxies = []
            verify_executor = ThreadPoolExecutor(max_workers=50)
            try:
                candidates = list(raw_proxies)[:300]
                pending = {
                    verify_executor.submit(self.verify_proxy, proxy_ip, stop_event): proxy_ip
                    for proxy_ip in candidates
                }

                completed_count = 0
                while pending:
                    if should_stop(stop_event):
                        raise OperationCancelledError("已停止内置代理池测试")
                    done, unfinished = wait(set(pending.keys()), timeout=0.25, return_when=FIRST_COMPLETED)
                    if not done:
                        continue
                    for future in done:
                        proxy_ip = pending.pop(future, None)
                        if should_stop(stop_event):
                            raise OperationCancelledError("已停止内置代理池测试")
                        result = future.result()
                        if result:
                            verified_proxies.append(result)

                        completed_count += 1
                        if completed_count % 50 == 0:
                            print(f"    已验证 {completed_count}/{len(candidates)} 个候选节点...")
            finally:
                verify_executor.shutdown(wait=False, cancel_futures=True)

            if verified_proxies:
                self.proxies = verified_proxies
                self._last_fetch_time = time.time()
                self._save_persisted_proxies()
                print(f"✅ 已加载 {len(self.proxies)} 个通过{mode_label}模式验证的代理节点")
            else:
                print(f"⚠️ 没有代理节点通过{mode_label}模式验证，请稍后重试或改用手动代理")

    def get_proxy(self):
        """随机获取一个代理"""
        if not self.enabled or not self.proxies:
            return None
        import random
        proxy_ip = random.choice(self.proxies)
        return {
            "http": f"http://{proxy_ip}",
            "https": f"http://{proxy_ip}"
        }

    def remove_proxy(self, proxy_dict):
        """移除失效代理"""
        if not self.enabled or not proxy_dict:
            return
        proxy_url = proxy_dict.get("http")
        if not proxy_url:
            return
        proxy_ip = proxy_url.replace("http://", "")
        with self.lock:
            if proxy_ip in self.proxies:
                self.proxies.remove(proxy_ip)
                # print(f"🗑️ Removed bad proxy: {proxy_ip}")

    def clear_cached_proxies(self):
        """清空当前缓存的代理节点，强制下次重新拉取。"""
        with self.lock:
            self.proxies = []
            self._last_fetch_time = 0
            self._persistence_loaded = True
            self._drop_persisted_proxies()

# 全局代理池实例
proxy_pool = ProxyPool()

# 默认请求头
HEADERS = {
    "Accept": "application/json, text/plain, */*",
    "Referer": "https://baozimh.org/",
}

# 配置日志
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)

# 连接池配置
SESSION_POOL = threading.local()


def should_stop(stop_event=None):
    """统一判断是否需要停止下载。"""
    return stop_event is not None and stop_event.is_set()

def get_session():
    """获取线程本地的session，支持连接复用"""
    if not hasattr(SESSION_POOL, 'session'):
        SESSION_POOL.session = requests.Session()
        SESSION_POOL.session.trust_env = False
        SESSION_POOL.session.headers.update(HEADERS)
        # max_retries=0：交给外层 safe_request 的重试循环统一处理，避免 5*4=20 次叠加
        # 池大小对齐章节×图片并发上限，减少 "Connection pool is full" 丢弃
        adapter = HTTPAdapter(
            pool_connections=32,
            pool_maxsize=64,
            max_retries=0,
        )
        SESSION_POOL.session.mount('http://', adapter)
        SESSION_POOL.session.mount('https://', adapter)
    return SESSION_POOL.session


def safe_request(url, timeout=10, retries=5, delay=1, headers=None, stop_event=None, stream=False):
    """带延时重试的安全请求 (支持代理和UA轮询)"""
    import random

    if should_stop(stop_event):
        return None

    # 首次尝试先获取代理
    if proxy_pool.enabled and not proxy_pool.proxies:
        proxy_pool.fetch_proxies()

    if headers is None:
        headers = HEADERS.copy()

    # 每次请求随机 UA
    headers["User-Agent"] = random.choice(USER_AGENTS)

    # 第一次尝试直连 (为了速度，如果直连能通最好)
    # 但如果为了防封，应该直接用代理
    # 这里策略：如果有代理，优先用代理。如果代理全挂了，才尝试直连（或者报错）

    for attempt in range(retries + 1):
        if should_stop(stop_event):
            return None

        proxy = proxy_pool.get_proxy()
        # if not proxy:
        #    print("⚠️ No proxy available, trying direct connection...")

        try:
            # 增加 timeout，因为代理通常较慢
            # print(f"DEBUG: Requesting {url} with proxy {proxy}")
            session = get_session()
            resp = session.get(url, headers=headers, timeout=timeout + 5, proxies=proxy, stream=stream)
            resp.raise_for_status()
            return resp
        except Exception as e:
            # 如果使用了代理且失败，移除该代理
            if proxy:
                proxy_pool.remove_proxy(proxy)
                
            if attempt < retries:
                if should_stop(stop_event):
                    return None
                # with print_lock:
                    # 只有连续失败多次才打印，避免刷屏
                    # if attempt > 0: 
                    #    print(f"⚠️ Request failed ({e}), retrying in {delay}s... ({attempt + 1}/{retries})")
                time.sleep(delay)
            else:
                with print_lock:
                    print(f"❌ Failed after {retries + 1} attempts: {url}")
                return None


def sanitize_filename(name: str) -> str:
    """去除文件名中非法字符"""
    return re.sub(r'[\\/:*?"<>|]', '_', name.strip())


def build_absolute_url(url: str) -> str:
    """将站内相对路径转换为完整 URL。"""
    return urljoin(f"{BASE_SITE_URL}/", url)


def normalize_chapterlist_url(manga_url: str) -> str:
    """把 /manga/{slug} 详情页 URL 转成 /chapterlist/{slug}。"""
    parsed = urlparse(manga_url)
    path_parts = [part for part in parsed.path.strip("/").split("/") if part]

    if len(path_parts) >= 2 and path_parts[0] == "chapterlist":
        return build_absolute_url(parsed.path)

    if len(path_parts) >= 2 and path_parts[0] == "manga":
        return build_absolute_url(f"/chapterlist/{path_parts[1]}")

    return build_absolute_url(parsed.path)


def unwrap_cover_url(cover_url: str) -> str:
    """
    还原 Next/Image 包装后的真实封面地址。
    例如：
    https://pro-api.../_next/image?url=https%3A%2F%2Fcover...&w=250&q=60
    """
    if not cover_url:
        return ""

    parsed = urlparse(cover_url)
    if "/_next/image" not in parsed.path:
        return cover_url

    query = parse_qs(parsed.query)
    real_url = query.get("url", [cover_url])[0]
    return real_url


def coerce_html_attr_to_str(value) -> str:
    """把 BeautifulSoup 属性值安全转成字符串。"""
    if isinstance(value, str):
        return value
    if isinstance(value, (list, tuple)):
        for item in value:
            if isinstance(item, str) and item.strip():
                return item
        return ""
    return "" if value is None else str(value)


@dataclass
class HomepageMangaCard:
    section: str
    title: str
    manga_url: str
    chapterlist_url: str
    cover_url: str = ""
    latest_chapter: str = ""
    update_time: str = ""


def _extract_standard_card_section(soup: BeautifulSoup, heading: str) -> List[HomepageMangaCard]:
    cards: List[HomepageMangaCard] = []
    header = next((node for node in soup.find_all("h2") if node.get_text(strip=True) == heading), None)
    if not header:
        return cards

    title_link = header.find_parent("a", class_=re.compile(r"\bhometitle\b"))
    section_wrapper = None
    if title_link:
        title_row = title_link.find_parent("div")
        if title_row:
            section_wrapper = title_row.find_parent("div")

    if not section_wrapper:
        section_wrapper = header.find_parent("div")

    if not section_wrapper:
        return cards

    cardlist = section_wrapper.find("div", class_=re.compile(r"\bcardlist\b"))
    if not cardlist:
        return cards

    for wrapper in cardlist.find_all("div", recursive=False):
        item = wrapper.find("a", href=True)
        if not item:
            continue

        href = coerce_html_attr_to_str(item.get("href", "")).strip()
        title_node = item.find("h3")
        img_node = item.find("img")
        title = title_node.get_text(strip=True) if title_node else ""

        if not href or not title:
            continue

        manga_url = build_absolute_url(href)
        cards.append(
            HomepageMangaCard(
                section=heading,
                title=title,
                manga_url=manga_url,
                chapterlist_url=normalize_chapterlist_url(manga_url),
                cover_url=unwrap_cover_url(coerce_html_attr_to_str(img_node.get("src", "")).strip()) if img_node else "",
            )
        )

    return cards


def _extract_recent_update_section(soup: BeautifulSoup) -> List[HomepageMangaCard]:
    cards: List[HomepageMangaCard] = []
    header = next((node for node in soup.find_all("h2") if node.get_text(strip=True) == "近期更新"), None)
    if not header:
        return cards

    section_wrapper = header.find_parent("div")
    if not section_wrapper:
        return cards

    for item in section_wrapper.select("a.slicarda[href]"):
        href = coerce_html_attr_to_str(item.get("href", "")).strip()
        img_node = item.find("img")
        title_node = item.find("h3", class_=re.compile(r"\bslicardtitle\b"))
        time_node = item.find("p", class_=re.compile(r"\bslicardtagp\b"))
        latest_node = item.find("p", class_=re.compile(r"\bslicardtitlep\b"))

        title = title_node.get_text(strip=True) if title_node else ""
        if not href or not title:
            continue

        manga_url = build_absolute_url(href)
        cards.append(
            HomepageMangaCard(
                section="近期更新",
                title=title,
                manga_url=manga_url,
                chapterlist_url=normalize_chapterlist_url(manga_url),
                cover_url=unwrap_cover_url(coerce_html_attr_to_str(img_node.get("src", "")).strip()) if img_node else "",
                latest_chapter=latest_node.get_text(strip=True) if latest_node else "",
                update_time=time_node.get_text(strip=True) if time_node else "",
            )
        )

    return cards


def fetch_homepage_manga_cards() -> List[HomepageMangaCard]:
    """抓取首页主要榜单漫画卡片。"""
    homepage_url = build_absolute_url("/")
    with print_lock:
        print(f"🔍 Fetching homepage: {homepage_url}")

    resp = safe_request(homepage_url, retries=1)
    if not resp:
        return []

    soup = BeautifulSoup(resp.content, "html.parser")

    cards: List[HomepageMangaCard] = []
    cards.extend(_extract_recent_update_section(soup))
    cards.extend(_extract_standard_card_section(soup, "熱門更新"))
    cards.extend(_extract_standard_card_section(soup, "人氣排行"))
    cards.extend(_extract_standard_card_section(soup, "最新上架"))

    return cards


def fetch_section_manga_cards(section: str, page: int = 1) -> List[HomepageMangaCard]:
    """抓取分区列表页漫画卡片。支持 rank/hot-update/new/recent。"""
    if page < 1:
        page = 1

    if section == "recent":
        homepage_resp = safe_request(build_absolute_url("/"), retries=1)
        if not homepage_resp:
            return []
        return _extract_recent_update_section(BeautifulSoup(homepage_resp.content, "html.parser"))

    section_paths = {
        "rank": "/hots",
        "hot-update": "/dayup",
        "new": "/newss",
    }
    section_titles = {
        "rank": "人氣排行",
        "hot-update": "熱門更新",
        "new": "最新上架",
    }

    path = section_paths.get(section)
    if not path:
        return []

    page_url = build_absolute_url(f"{path}/page/{page}")
    with print_lock:
        print(f"🔍 Fetching section page: {page_url}")

    resp = safe_request(page_url, retries=1)
    if not resp:
        return []

    soup = BeautifulSoup(resp.content, "html.parser")

    cards: List[HomepageMangaCard] = []
    cardlist = soup.find("div", class_=re.compile(r"\bcardlist\b"))
    if not cardlist:
        return cards

    for wrapper in cardlist.find_all("div", recursive=False):
        item = wrapper.find("a", href=True)
        if not item:
            continue

        href = coerce_html_attr_to_str(item.get("href", "")).strip()
        title_node = item.find("h3")
        img_node = item.find("img")
        title = title_node.get_text(strip=True) if title_node else ""

        if not href or not title:
            continue

        manga_url = build_absolute_url(href)
        cards.append(
            HomepageMangaCard(
                section=section_titles.get(section, section),
                title=title,
                manga_url=manga_url,
                chapterlist_url=normalize_chapterlist_url(manga_url),
                cover_url=unwrap_cover_url(coerce_html_attr_to_str(img_node.get("src", "")).strip()) if img_node else "",
            )
        )

    return cards


def fetch_search_manga_cards(keyword: str, page: int = 1) -> List[HomepageMangaCard]:
    """按关键词抓取站内搜索结果，支持模糊搜索。"""
    keyword = (keyword or "").strip()
    if not keyword:
        return []

    if page < 1:
        page = 1

    page_url = f"{build_absolute_url('/s')}?q={quote(keyword)}&page={page}"
    with print_lock:
        print(f"🔍 Fetching search page: {page_url}")

    resp = safe_request(page_url, retries=1)
    if not resp:
        return []

    soup = BeautifulSoup(resp.content, "html.parser")

    cards: List[HomepageMangaCard] = []
    cardlist = soup.find("div", class_=re.compile(r"\bcardlist\b"))
    if not cardlist:
        return cards

    for wrapper in cardlist.find_all("div", recursive=False):
        item = wrapper.find("a", href=True)
        if not item:
            continue

        href = coerce_html_attr_to_str(item.get("href", "")).strip()
        title_node = item.find("h3")
        img_node = item.find("img")
        title = title_node.get_text(strip=True) if title_node else ""

        if not href or not title:
            continue

        manga_url = build_absolute_url(href)
        cards.append(
            HomepageMangaCard(
                section="搜索结果",
                title=title,
                manga_url=manga_url,
                chapterlist_url=normalize_chapterlist_url(manga_url),
                cover_url=unwrap_cover_url(coerce_html_attr_to_str(img_node.get("src", "")).strip()) if img_node else "",
            )
        )

    return cards


def filter_homepage_cards(cards: List[HomepageMangaCard], section: Optional[str] = None,
                          limit: Optional[int] = None) -> List[HomepageMangaCard]:
    """按分区和数量筛选首页卡片。"""
    section_aliases = {
        None: None,
        "all": None,
        "recent": "近期更新",
        "近期更新": "近期更新",
        "hot-update": "熱門更新",
        "热门更新": "熱門更新",
        "熱門更新": "熱門更新",
        "rank": "人氣排行",
        "排行": "人氣排行",
        "人气排行": "人氣排行",
        "人氣排行": "人氣排行",
        "new": "最新上架",
        "最新上架": "最新上架",
    }
    resolved_section = section_aliases.get(section, section)

    filtered = [card for card in cards if resolved_section is None or card.section == resolved_section]
    if limit is not None and limit > 0:
        filtered = filtered[:limit]
    return filtered


def print_homepage_cards(cards: List[HomepageMangaCard]):
    """将首页卡片以可读格式输出。"""
    if not cards:
        print("⚠️ No homepage manga cards found.")
        return

    for idx, card in enumerate(cards, 1):
        print(f"[{idx}] {card.title}")
        print(f"    分区: {card.section}")
        print(f"    详情页: {card.manga_url}")
        print(f"    目录页: {card.chapterlist_url}")
        if card.cover_url:
            print(f"    封面: {card.cover_url}")
        if card.latest_chapter:
            print(f"    最近章节: {card.latest_chapter}")
        if card.update_time:
            print(f"    更新时间: {card.update_time}")


def homepage_cards_to_dict(cards: List[HomepageMangaCard]) -> List[Dict[str, str]]:
    """把首页卡片对象转为可序列化字典。"""
    return [
        {
            "section": card.section,
            "title": card.title,
            "manga_url": card.manga_url,
            "chapterlist_url": card.chapterlist_url,
            "cover_url": card.cover_url,
            "latest_chapter": card.latest_chapter,
            "update_time": card.update_time,
        }
        for card in cards
    ]


def download_single_image(args):
    """下载单张图片的辅助函数，用于并发下载"""
    img_url, dest_path, idx, total, chapter_dir_name, stop_event = args
    filename = os.path.basename(dest_path)

    if should_stop(stop_event):
        return False, f"🛑 Cancelled {filename}"
    
    if os.path.exists(dest_path) and os.path.getsize(dest_path) > 0:
        return True, f"⏩ Skipped {filename}"

    r = safe_request(img_url, timeout=15, retries=2, stop_event=stop_event, stream=True)
    if not r:
        if should_stop(stop_event):
            return False, f"🛑 Cancelled {filename}"
        return False, f"❌ Failed to download {filename}"

    try:
        with open(dest_path, 'wb') as f:
            for chunk in r.iter_content(65536):
                if should_stop(stop_event):
                    try:
                        f.close()
                        if os.path.exists(dest_path):
                            os.remove(dest_path)
                    except OSError:
                        pass
                    return False, f"🛑 Cancelled {filename}"
                if chunk:
                    f.write(chunk)
        return True, f"✅ Saved {filename} ({idx}/{total})"
    except Exception as e:
        return False, f"❌ Failed to save {filename}: {e}"
    finally:
        try:
            r.close()
        except Exception:
            pass

def download_chapter_images(chapter_slug, base_url_template, root_dir="LuoxiaoHeizhanji",
                            max_concurrent_images=5, stop_event=None, show_progress=True):
    """
    下载章节图片
    :param chapter_slug: 章节的 slug (例如 "0_7" 或 "1872415a3262850b1872158_124")
    :param base_url_template: 基础 URL 模板，包含 {slug} 占位符
    :param root_dir: 保存根目录
    :param max_concurrent_images: 最大并发图片下载数
    :return: (downloaded_count, next_chapter_slug, chapter_info)
    """
    chapter_url = base_url_template.format(slug=chapter_slug)
    if should_stop(stop_event):
        return 0, None, None

    with print_lock:
        print(f"🔍 Processing Chapter {chapter_slug}: {chapter_url}")
    
    # 1. 获取章节页面 HTML
    resp = safe_request(chapter_url, retries=1, stop_event=stop_event)
    if not resp:
        return 0, None, None

    soup = BeautifulSoup(resp.content, "html.parser")
    
    # 2. 提取 API 所需参数 (data-ms, data-cs)
    content_div = soup.find("div", id="chapterContent")
    if not content_div:
        with print_lock:
            print(f"⚠️ Could not find chapter content div for {chapter_url}")
        return 0, None, None
        
    manga_id = content_div.get("data-ms")
    chapter_id = content_div.get("data-cs")
    chapter_title = str(content_div.get("data-ct") or f"Chapter_{chapter_slug}")
    
    if not manga_id or not chapter_id:
        with print_lock:
            print(f"⚠️ Missing data-ms or data-cs for {chapter_url}")
        return 0, None, None

    # 3. 调用 API 获取图片列表
    api_url = f"https://api-get-v3.mgsearcher.com/api/chapter/getinfo?m={manga_id}&c={chapter_id}"
    # API 请求需要 Referer 为章节页面
    api_headers = HEADERS.copy()
    api_headers["Referer"] = chapter_url
    
    api_resp = safe_request(api_url, headers=api_headers, stop_event=stop_event)
    next_slug = None
    order = 0

    if not api_resp:
        return 0, None, None
        
    try:
        data = api_resp.json()
        if not data.get("data") or not data["data"].get("info") or not data["data"]["info"].get("images"):
            with print_lock:
                print(f"⚠️ Invalid API response structure for {chapter_url}")
            return 0, None, None
        
        info = data["data"]["info"]
        images_info = info["images"]
        img_list = images_info.get("images", [])
        line = images_info.get("line", 0)
        order = info.get("order", 0) # 获取章节序号
        
        # 获取下一章的 slug
        next_slug = info.get("nextslug")

        # 确定图片 CDN 域名
        cdn_host = "https://t40-2-4.g-mh.online" if line == 3 else "https://t40-1-4.g-mh.online"
        
    except Exception as e:
        with print_lock:
            print(f"⚠️ Failed to parse API response for {chapter_url}: {e}")
        return 0, None, None

    # 清理章节名称，移除非法字符
    safe_title = sanitize_filename(chapter_title)
    # 使用章节序号（order）作为前缀，而不是 slug
    # 确保序号格式化为3位数字，方便排序
    chapter_dir_name = f"{order:03d}_{safe_title}"
    chapter_dir = os.path.join(root_dir, chapter_dir_name)
    os.makedirs(chapter_dir, exist_ok=True)

    # 4. 构建图片 URLs
    img_urls = []
    for img in img_list:
        if should_stop(stop_event):
            return 0, next_slug, {'slug': next_slug} if next_slug else None
        if "url" in img:
            full_url = urljoin(cdn_host, img["url"])
            img_urls.append(full_url)

    if not img_urls:
        with print_lock:
            print(f"⚠️ No images found for {chapter_url}")
        return 0, next_slug, None

    # 检查是否已完整下载
    local_files = {
        f for f in os.listdir(chapter_dir)
        if f.lower().endswith((".jpg", ".jpeg", ".png", ".webp"))
    }
    if len(local_files) >= len(img_urls) and len(local_files) > 0:
        with print_lock:
            print(f"⏭️  Skipping Chapter {chapter_slug} ({chapter_dir_name}): already complete ({len(local_files)} images). Next: {next_slug}")
        return len(img_urls), next_slug, {'slug': next_slug}

    # 准备下载任务
    download_tasks = []
    for idx, img_url in enumerate(img_urls, 1):
        ext = os.path.splitext(img_url.split("?")[0])[1]
        if not ext:
            ext = ".webp" # 默认为 webp
            
        filename = f"{idx:03d}{ext}"
        dest_path = os.path.join(chapter_dir, filename)
        download_tasks.append((img_url, dest_path, idx, len(img_urls), chapter_dir_name, stop_event))

    # 使用并发下载和进度条
    count = 0
    success_count = 0
    
    with print_lock:
        print(f"📥 Downloading {len(download_tasks)} images for {chapter_dir_name}")
    
    # 使用进度条进行并发下载
    progress_cm = tqdm(total=len(download_tasks), desc=f"📖 {chapter_dir_name[:30]}",
                       unit="img", leave=False, dynamic_ncols=True, disable=not show_progress)
    with progress_cm as pbar:
        with ThreadPoolExecutor(max_workers=max_concurrent_images) as img_executor:
            future_to_task = {
                img_executor.submit(download_single_image, task): task
                for task in download_tasks
            }

            for future in as_completed(future_to_task):
                task = future_to_task[future]
                _, _, idx, total, _, _ = task

                if should_stop(stop_event):
                    for pending_future in future_to_task:
                        pending_future.cancel()
                    img_executor.shutdown(wait=False, cancel_futures=True)
                    break

                try:
                    success, message = future.result()
                    if success:
                        success_count += 1
                        pbar.set_postfix({"✅": f"{success_count}/{total}"})
                    else:
                        pbar.set_postfix({"❌": f"{idx}/{total}"})
                except Exception:
                    pbar.set_postfix({"❌": f"Error {idx}/{total}"})

                pbar.update(1)
                count += 1

    if should_stop(stop_event):
        with print_lock:
            print(f"🛑 Chapter {chapter_slug} cancelled.")
        return success_count, next_slug, {'slug': next_slug} if next_slug else None

    with print_lock:
        print(f"✅ Chapter {chapter_slug} ({chapter_dir_name}): {success_count}/{len(img_urls)} images downloaded. Next: {next_slug}")

    if success_count < len(img_urls):
        raise RuntimeError(f"包子漫画图片下载不完整: {success_count}/{len(img_urls)}")

    return success_count, next_slug, {'slug': next_slug}


def get_manga_info_from_url(url):
    """
    从 URL 中提取漫画 ID 和 slug
    :param url: 漫画目录页或章节页 URL
    :return: (manga_id, manga_slug, start_slug)
    """
    parsed = urlparse(url)
    path_parts = parsed.path.strip("/").split("/")
    
    # 假设 URL 结构:
    # 目录页: /chapterlist/{manga_slug}
    # 章节页: /manga/{manga_slug}/{chapter_slug}
    
    manga_slug = None
    start_slug = None
    manga_id = None
    
    if "chapterlist" in path_parts:
        # /chapterlist/wozhenmeixiangzhongshenga-pikapi
        try:
            idx = path_parts.index("chapterlist")
            if idx + 1 < len(path_parts):
                manga_slug = path_parts[idx + 1]
        except ValueError:
            pass
    elif "manga" in path_parts:
        # /manga/wozhenmeixiangzhongshenga-pikapi/0_7
        try:
            idx = path_parts.index("manga")
            if idx + 1 < len(path_parts):
                manga_slug = path_parts[idx + 1]
            if idx + 2 < len(path_parts):
                start_slug = path_parts[idx + 2]
        except ValueError:
            pass
            
    if not manga_slug:
        with print_lock:
            print("❌ Could not extract manga slug from URL.")
        return None, None, None

    with print_lock:
        print(f"🔍 Analyzing URL: {url} (Slug: {manga_slug})")

    resp = safe_request(url, retries=1)
    if not resp:
        return None, None, None
        
    soup = BeautifulSoup(resp.content, "html.parser")
    
    # 尝试从目录页提取 data-mid
    # <div class="pb-6" id="allchapters" data-mid="878" ...>
    # 或者 <div id="mangachapters" data-mid="4349" ...>
    all_chapters_div = soup.find("div", id="allchapters")
    if not all_chapters_div:
        all_chapters_div = soup.find("div", id="mangachapters")
        
    if all_chapters_div:
        manga_id = all_chapters_div.get("data-mid")
        
    # 尝试从章节页提取 data-ms
    # <div id="chapterContent" class="hidden" data-ms="878" ...>
    if not manga_id:
        content_div = soup.find("div", id="chapterContent")
        if content_div:
            manga_id = content_div.get("data-ms")
            
    if not manga_id:
        with print_lock:
            print("❌ Could not find manga ID (data-mid or data-ms) in page.")
        return None, None, None
        
    with print_lock:
        print(f"✅ Found Manga ID: {manga_id}")
        
    return manga_id, manga_slug, start_slug


def get_all_chapters(manga_id):
    """
    获取所有章节列表
    :param manga_id: 漫画 ID (例如 878)
    :return: (manga_title, chapters_list)
    """
    api_url = f"https://api-get-v3.mgsearcher.com/api/manga/get?mid={manga_id}&mode=all"
    with print_lock:
        print(f"🔍 Fetching chapter list from API: {api_url}")
    
    resp = safe_request(api_url)
    if not resp:
        return None, []
        
    try:
        data = resp.json()
        if not data.get("status") or not data.get("data") or not data["data"].get("chapters"):
            with print_lock:
                print("⚠️ Invalid chapter list API response")
            return None, []
            
        manga_data = data["data"]
        manga_title = manga_data.get("title", f"Manga_{manga_id}")
        chapters_data = manga_data["chapters"]
        
        chapters = []
        for item in chapters_data:
            attr = item.get("attributes", {})
            chapters.append({
                "slug": attr.get("slug"),
                "order": attr.get("order"),
                "title": attr.get("title"),
                "updated_at": attr.get("updatedAt")
            })
        
        # 按 order 排序 (从小到大)
        chapters.sort(key=lambda x: x["order"])
        
        with print_lock:
            print(f"✅ Found manga: {manga_title}, {len(chapters)} chapters.")
            
        return manga_title, chapters
        
    except Exception as e:
        with print_lock:
            print(f"⚠️ Failed to parse chapter list: {e}")
        return None, []


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Download manga from baozimh.org")
    parser.add_argument("url", nargs="?", default=None, help="Manga directory URL, detail URL, or chapter URL")
    parser.add_argument("--start", type=int, help="Start downloading from this chapter order number (overrides URL chapter)", default=None)
    parser.add_argument("--concurrent", type=int, default=5, help="Max concurrent chapters download")
    parser.add_argument("--image-concurrent", type=int, default=5, help="Max concurrent images per chapter")
    parser.add_argument("--proxy", action="store_true", help="Enable proxy pool")
    parser.add_argument("--no-progress", action="store_true", help="Disable progress bars")
    parser.add_argument("--list-homepage", action="store_true", help="Fetch and print homepage manga cards")
    parser.add_argument("--homepage-section", default="all",
                        help="Homepage section filter: all/recent/hot-update/rank/new")
    parser.add_argument("--homepage-limit", type=int, default=10, help="Limit homepage results")
    parser.add_argument("--homepage-json", action="store_true", help="Print homepage cards as JSON")
    parser.add_argument("--homepage-download", type=int,
                        help="Download the Nth manga from the homepage list after filtering")
    
    args = parser.parse_args()

    max_concurrent_chapters = args.concurrent
    max_concurrent_images = args.image_concurrent
    proxy_pool.enabled = args.proxy
    show_progress = not args.no_progress

    homepage_cards = []
    if args.list_homepage or args.homepage_download is not None:
        homepage_cards = filter_homepage_cards(
            fetch_homepage_manga_cards(),
            section=args.homepage_section,
            limit=args.homepage_limit
        )

        if args.list_homepage:
            if args.homepage_json:
                print(json.dumps(homepage_cards_to_dict(homepage_cards), ensure_ascii=False, indent=2))
            else:
                print_homepage_cards(homepage_cards)

            if args.homepage_download is None:
                exit(0)

    if args.homepage_download is not None:
        if not homepage_cards:
            print("❌ Homepage manga list is empty. Nothing to download.")
            exit(1)

        index = args.homepage_download - 1
        if index < 0 or index >= len(homepage_cards):
            print(f"❌ Invalid homepage index: {args.homepage_download}. Valid range: 1-{len(homepage_cards)}")
            exit(1)

        selected_card = homepage_cards[index]
        url = selected_card.manga_url
        print(f"🎯 Selected homepage manga: {selected_card.title}")
        print(f"🔗 Using URL: {url}")
    elif args.url:
        url = args.url
    else:
        print("❌ 未提供下载 URL。此入口仅保留后端下载能力，不再启动旧 GUI。")
        exit(1)
    
    # 1. 分析 URL 获取漫画信息
    manga_id, manga_slug, url_start_slug = get_manga_info_from_url(url)
    
    if not manga_id or not manga_slug:
        print("❌ Failed to get manga info. Exiting.")
        exit(1)

    # 转换为模板格式
    base = f"https://baozimh.org/manga/{manga_slug}/{{slug}}"
    
    # 2. 获取所有章节
    manga_title, all_chapters = get_all_chapters(manga_id)
    if not all_chapters:
        print("❌ Failed to get chapter list. Exiting.")
        exit(1)

    # 3. 确定起始章节
    start_order = 0
    if args.start is not None:
        start_order = args.start
        print(f"⚙️  Start order set to {start_order} (from arguments)")
    elif url_start_slug:
        # 查找 URL 中指定的章节 slug 对应的 order
        found = False
        for c in all_chapters:
            if c["slug"] == url_start_slug:
                start_order = c["order"]
                print(f"⚙️  Start order set to {start_order} (found from URL chapter: {url_start_slug})")
                found = True
                break
        if not found:
            print(f"⚠️ Warning: Start slug {url_start_slug} not found in chapter list. Starting from beginning.")
    else:
        print("⚙️  No start chapter specified. Starting from the beginning.")

    # 4. 筛选出需要下载的章节 (从 start_order 开始)
    pending_chapters = [c for c in all_chapters if c["order"] >= start_order]
    
    if not pending_chapters:
        print(f"⚠️ No chapters found starting from order {start_order}.")
        exit(0)
    
    # 5. 设置保存目录
    storage_root_dir = ensure_storage_root_dir()
    safe_manga_title = sanitize_filename(str(manga_title))
    root_dir = os.path.join(storage_root_dir, f"{safe_manga_title}")
    os.makedirs(root_dir, exist_ok=True)
    
    print(f"📂 Saving to: {root_dir}")
    print(f"📥 Queued {len(pending_chapters)} chapters for download (starting from order {start_order}).")

    try:
        # 使用 ThreadPoolExecutor 实现并发下载章节
        with ThreadPoolExecutor(max_workers=max_concurrent_chapters) as executor:
            # 记录 future 对应的 chapter info
            futures = {} 

            # 主循环
            while pending_chapters or futures:
                # 1. 提交新任务，直到达到最大并发数
                while pending_chapters and len(futures) < max_concurrent_chapters:
                    chapter = pending_chapters.pop(0)
                    f = executor.submit(
                        download_chapter_images,
                        chapter["slug"],
                        base,
                        root_dir,
                        max_concurrent_images=max_concurrent_images,
                        show_progress=show_progress
                    )
                    futures[f] = chapter

                if not futures:
                    break

                # 2. 等待任意一个任务完成
                done, _ = wait(list(futures.keys()), return_when=FIRST_COMPLETED)
                
                for future in done:
                    chapter = futures.pop(future)
                    try:
                        count, _, _ = future.result()
                        # 这里不需要处理 next_slug，因为我们已经有了完整列表
                        if count == 0:
                             with print_lock:
                                print(f"⚠️ Chapter {chapter['order']} ({chapter['title']}) failed or empty.")

                    except Exception as e:
                        with print_lock:
                            print(f"⚠️ Exception in Chapter {chapter['order']}: {e}")
            
            print("\n✅ 所有任务处理完毕。")

    except KeyboardInterrupt:
        print("\n🛑 检测到用户中断，正在安全退出...")
        # executor.shutdown(wait=False, cancel_futures=True) 
        print("✅ 已中断所有下载任务。")
