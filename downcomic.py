import os
import re
import time
import argparse
import requests
import threading
from bs4 import BeautifulSoup
from urllib.parse import urljoin, urlparse
from concurrent.futures import ThreadPoolExecutor, as_completed, wait, FIRST_COMPLETED
from typing import Optional, Dict, List, Tuple
from dataclasses import dataclass
from tqdm import tqdm
import logging
from requests.adapters import HTTPAdapter

# 🔒 打印锁，防止多线程打印错乱
print_lock = threading.Lock()

# User-Agent 池
USER_AGENTS = [
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
]

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
        self._fetch_interval = 600 # 10分钟更新一次

    def verify_proxy(self, proxy_ip):
        """验证单个代理是否可用"""
        proxy = {
            "http": f"http://{proxy_ip}",
            "https": f"http://{proxy_ip}"
        }
        try:
            # 尝试访问一个稳定且响应快的地址进行验证
            # 使用 httpbin.org 验证
            resp = requests.get("http://httpbin.org/ip", proxies=proxy, timeout=5)
            if resp.status_code == 200:
                return proxy_ip
        except:
            pass
        return None

    def fetch_proxies(self):
        """从网络获取并验证免费代理"""
        if not self.enabled:
            return

        with self.lock:
            if time.time() - self._last_fetch_time < self._fetch_interval and self.proxies:
                return

            print("🔄 Fetching new proxies from multiple sources...")
            raw_proxies = set()
            
            # 1. 并发获取原始代理列表
            def fetch_source(url):
                try:
                    resp = requests.get(url, timeout=10)
                    if resp.status_code == 200:
                        lines = resp.text.strip().splitlines()
                        found = 0
                        for line in lines:
                            line = line.strip()
                            # 简单的 IP:Port 验证
                            if re.match(r'^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}:\d+$', line):
                                raw_proxies.add(line)
                                found += 1
                        print(f"    Fetched {found} proxies from {url}")
                except Exception as e:
                    print(f"⚠️ Failed to fetch from {url}: {e}")

            with ThreadPoolExecutor(max_workers=len(self.proxy_sources)) as executor:
                executor.map(fetch_source, self.proxy_sources)

            if not raw_proxies:
                print("⚠️ No proxies found from any source.")
                return

            print(f"🔄 Verifying {len(raw_proxies)} candidates (this may take a moment)...")
            
            # 2. 并发验证代理可用性
            verified_proxies = []
            with ThreadPoolExecutor(max_workers=50) as executor:
                # 限制验证数量，避免太久，只取前 200 个进行验证
                candidates = list(raw_proxies)[:200] 
                future_to_proxy = {executor.submit(self.verify_proxy, p): p for p in candidates}
                
                completed_count = 0
                for future in as_completed(future_to_proxy):
                    result = future.result()
                    if result:
                        verified_proxies.append(result)
                    
                    completed_count += 1
                    if completed_count % 50 == 0:
                        print(f"    Verified {completed_count}/{len(candidates)}...")

            if verified_proxies:
                self.proxies = verified_proxies
                self._last_fetch_time = time.time()
                print(f"✅ Successfully loaded {len(self.proxies)} VALID proxies.")
            else:
                print("⚠️ No valid proxies passed verification. Using direct connection as fallback might fail if IP banned.")

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
        SESSION_POOL.session.headers.update(HEADERS)
        # 配置连接池
        adapter = HTTPAdapter(
            pool_connections=10,
            pool_maxsize=50,
            max_retries=3
        )
        SESSION_POOL.session.mount('http://', adapter)
        SESSION_POOL.session.mount('https://', adapter)
    return SESSION_POOL.session


def safe_request(url, timeout=10, retries=5, delay=1, headers=None, stop_event=None):
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
            resp = requests.get(url, headers=headers, timeout=timeout + 5, proxies=proxy)
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


def download_single_image(args):
    """下载单张图片的辅助函数，用于并发下载"""
    img_url, dest_path, idx, total, chapter_dir_name, stop_event = args
    filename = os.path.basename(dest_path)

    if should_stop(stop_event):
        return False, f"🛑 Cancelled {filename}"
    
    if os.path.exists(dest_path) and os.path.getsize(dest_path) > 0:
        return True, f"⏩ Skipped {filename}"
    
    r = safe_request(img_url, timeout=15, retries=2, stop_event=stop_event)
    if not r:
        if should_stop(stop_event):
            return False, f"🛑 Cancelled {filename}"
        return False, f"❌ Failed to download {filename}"
    
    try:
        with open(dest_path, 'wb') as f:
            for chunk in r.iter_content(8192):
                if should_stop(stop_event):
                    try:
                        f.close()
                        if os.path.exists(dest_path):
                            os.remove(dest_path)
                    except OSError:
                        pass
                    return False, f"🛑 Cancelled {filename}"
                f.write(chunk)
        return True, f"✅ Saved {filename} ({idx}/{total})"
    except Exception as e:
        return False, f"❌ Failed to save {filename}: {e}"

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

    # 强制使用 UTF-8 编码，防止中文乱码
    resp.encoding = 'utf-8'

    soup = BeautifulSoup(resp.text, "html.parser")
    
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
        return 0, next_slug, {'slug': next_slug}

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
        
    # 强制 UTF-8
    resp.encoding = 'utf-8'
    soup = BeautifulSoup(resp.text, "html.parser")
    
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
                "title": attr.get("title")
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
    parser.add_argument("url", nargs="?", default="https://baozimh.org/chapterlist/tianguansifu-baimengshemoxiangtongchoustaremberjinjiangwenxuecheng", help="Manga directory URL or chapter URL")
    parser.add_argument("--start", type=int, help="Start downloading from this chapter order number (overrides URL chapter)", default=None)
    parser.add_argument("--concurrent", type=int, default=5, help="Max concurrent chapters download")
    parser.add_argument("--image-concurrent", type=int, default=5, help="Max concurrent images per chapter")
    parser.add_argument("--proxy", action="store_true", help="Enable proxy pool")
    parser.add_argument("--no-progress", action="store_true", help="Disable progress bars")
    
    args = parser.parse_args()
    
    if not args.url:
        print("Usage: python downcomic.py [URL] [--start ORDER] [--concurrent N]")
        print("Example: python downcomic.py https://baozimh.org/chapterlist/wozhenmeixiangzhongshenga-pikapi")
        # 为了方便调试，如果没有参数，可以默认使用用户之前提供的测试 URL
        # url = "https://baozimh.org/manga/wozhenmeixiangzhongshenga-pikapi/0_7"
        exit(1)
    else:
        url = args.url

    max_concurrent_chapters = args.concurrent
    max_concurrent_images = args.image_concurrent
    proxy_pool.enabled = args.proxy
    show_progress = not args.no_progress
    
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
    # 获取脚本所在目录的绝对路径，确保下载到脚本同级目录
    script_dir = os.path.dirname(os.path.abspath(__file__))
    safe_manga_title = sanitize_filename(str(manga_title))
    root_dir = os.path.join(script_dir, f"{safe_manga_title}")
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
                    f = executor.submit(download_chapter_images, chapter["slug"], base, root_dir)
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
