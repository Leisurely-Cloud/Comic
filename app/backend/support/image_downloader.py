"""图片下载功能。"""
from __future__ import annotations

import os
from typing import Optional
from urllib.parse import urljoin

from .http_helpers import print, print_lock, safe_request, should_stop

try:
    from tqdm import tqdm
except ImportError:
    def tqdm(*args, **kwargs):  # type: ignore[misc]
        if args:
            return args[0]
        return kwargs.get("iterable", [])


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
    from concurrent.futures import ThreadPoolExecutor, as_completed
    from bs4 import BeautifulSoup

    from .file_utils import sanitize_filename
    from .http_helpers import _api_fetch_json

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

    data = _api_fetch_json(api_url, referer=chapter_url)
    next_slug = None
    order = 0

    if not data:
        return 0, None, None

    try:
        if not data.get("data") or not data["data"].get("info") or not data["data"]["info"].get("images"):
            with print_lock:
                print(f"⚠️ Invalid API response structure for {chapter_url}")
            return 0, None, None

        info = data["data"]["info"]
        images_info = info["images"]
        img_list = images_info.get("images", [])
        line = images_info.get("line", 0)
        order = info.get("order", 0)

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
            ext = ".webp"

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
