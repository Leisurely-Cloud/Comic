"""漫画柜适配器 + LZ 解密工具。"""
from __future__ import annotations

import ast
import html as html_lib
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
import threading
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import quote, unquote, urljoin, urlparse

import requests
from bs4 import BeautifulSoup
from concurrent.futures import ThreadPoolExecutor, as_completed
from tqdm import tqdm

from .downcomic import (
    HomepageMangaCard,
    OperationCancelledError,
    print_lock,
    proxy_pool,
    sanitize_filename,
)

from .base import (
    BaseSiteAdapter,
    MangaDetail,
    coerce_html_attr_to_str,
    extract_cover_url_from_data,
    extract_cover_url_from_html,
    find_start_chapter_title,
    resolve_media_url,
)

MANHUAGUI_LZJS = r"""var LZString=(function(){var f=String.fromCharCode;var keyStrBase64="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";var baseReverseDic={};function getBaseValue(alphabet,character){if(!baseReverseDic[alphabet]){baseReverseDic[alphabet]={};for(var i=0;i<alphabet.length;i++){baseReverseDic[alphabet][alphabet.charAt(i)]=i}}return baseReverseDic[alphabet][character]}var LZString={decompressFromBase64:function(input){if(input==null)return"";if(input=="")return null;return LZString._0(input.length,32,function(index){return getBaseValue(keyStrBase64,input.charAt(index))})},_0:function(length,resetValue,getNextValue){var dictionary=[],next,enlargeIn=4,dictSize=4,numBits=3,entry="",result=[],i,w,bits,resb,maxpower,power,c,data={val:getNextValue(0),position:resetValue,index:1};for(i=0;i<3;i+=1){dictionary[i]=i}bits=0;maxpower=Math.pow(2,2);power=1;while(power!=maxpower){resb=data.val&data.position;data.position>>=1;if(data.position==0){data.position=resetValue;data.val=getNextValue(data.index++)}bits|=(resb>0?1:0)*power;power<<=1}switch(next=bits){case 0:bits=0;maxpower=Math.pow(2,8);power=1;while(power!=maxpower){resb=data.val&data.position;data.position>>=1;if(data.position==0){data.position=resetValue;data.val=getNextValue(data.index++)}bits|=(resb>0?1:0)*power;power<<=1}c=f(bits);break;case 1:bits=0;maxpower=Math.pow(2,16);power=1;while(power!=maxpower){resb=data.val&data.position;data.position>>=1;if(data.position==0){data.position=resetValue;data.val=getNextValue(data.index++)}bits|=(resb>0?1:0)*power;power<<=1}c=f(bits);break;case 2:return""}dictionary[3]=c;w=c;result.push(c);while(true){if(data.index>length){return""}bits=0;maxpower=Math.pow(2,numBits);power=1;while(power!=maxpower){resb=data.val&data.position;data.position>>=1;if(data.position==0){data.position=resetValue;data.val=getNextValue(data.index++)}bits|=(resb>0?1:0)*power;power<<=1}switch(c=bits){case 0:bits=0;maxpower=Math.pow(2,8);power=1;while(power!=maxpower){resb=data.val&data.position;data.position>>=1;if(data.position==0){data.position=resetValue;data.val=getNextValue(data.index++)}bits|=(resb>0?1:0)*power;power<<=1}dictionary[dictSize++]=f(bits);c=dictSize-1;enlargeIn--;break;case 1:bits=0;maxpower=Math.pow(2,16);power=1;while(power!=maxpower){resb=data.val&data.position;data.position>>=1;if(data.position==0){data.position=resetValue;data.val=getNextValue(data.index++)}bits|=(resb>0?1:0)*power;power<<=1}dictionary[dictSize++]=f(bits);c=dictSize-1;enlargeIn--;break;case 2:return result.join('')}if(enlargeIn==0){enlargeIn=Math.pow(2,numBits);numBits++}if(dictionary[c]){entry=dictionary[c]}else{if(c===dictSize){entry=w+w.charAt(0)}else{return null}}result.push(entry);dictionary[dictSize++]=w+entry.charAt(0);enlargeIn--;w=entry;if(enlargeIn==0){enlargeIn=Math.pow(2,numBits);numBits++}}}};return LZString})();String.prototype.splic=function(f){return LZString.decompressFromBase64(this).split(f)};"""
MANHUAGUI_BASE64_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/="
MANHUAGUI_BASE64_LOOKUP = {
    char: index for index, char in enumerate(MANHUAGUI_BASE64_ALPHABET)
}


def manhuagui_lz_decompress_from_base64(value: str) -> Optional[str]:
    if value is None:
        return ""
    if value == "":
        return None

    def get_next_value(index: int) -> int:
        if index >= len(value):
            return 0
        return MANHUAGUI_BASE64_LOOKUP.get(value[index], 0)

    return manhuagui_lz_decompress(len(value), 32, get_next_value)


def manhuagui_lz_decompress(length: int, reset_value: int, get_next_value) -> Optional[str]:
    dictionary: Dict[int, Any] = {0: 0, 1: 1, 2: 2}
    enlarge_in = 4
    dict_size = 4
    num_bits = 3
    data = {
        "val": get_next_value(0),
        "position": reset_value,
        "index": 1,
    }

    def read_bits(bit_count: int) -> int:
        bits = 0
        max_power = 1 << bit_count
        power = 1
        while power != max_power:
            resb = data["val"] & data["position"]
            data["position"] >>= 1
            if data["position"] == 0:
                data["position"] = reset_value
                data["val"] = get_next_value(data["index"])
                data["index"] += 1
            bits |= (1 if resb > 0 else 0) * power
            power <<= 1
        return bits

    next_value = read_bits(2)
    if next_value == 0:
        c = chr(read_bits(8))
    elif next_value == 1:
        c = chr(read_bits(16))
    elif next_value == 2:
        return ""
    else:
        return None

    dictionary[3] = c
    result = [c]
    w = c

    while True:
        if data["index"] > length:
            return ""

        c = read_bits(num_bits)
        if c == 0:
            dictionary[dict_size] = chr(read_bits(8))
            dict_size += 1
            c = dict_size - 1
            enlarge_in -= 1
        elif c == 1:
            dictionary[dict_size] = chr(read_bits(16))
            dict_size += 1
            c = dict_size - 1
            enlarge_in -= 1
        elif c == 2:
            return "".join(result)

        if enlarge_in == 0:
            enlarge_in = 1 << num_bits
            num_bits += 1

        entry = dictionary.get(c)
        if entry is None:
            if c == dict_size:
                entry = w + w[0]
            else:
                return None

        result.append(entry)
        dictionary[dict_size] = w + entry[0]
        dict_size += 1
        enlarge_in -= 1
        w = entry

        if enlarge_in == 0:
            enlarge_in = 1 << num_bits
            num_bits += 1


def manhuagui_unpack_packed_js(payload: str, alphabet_size: int, word_count: int, key_data: str) -> Optional[str]:
    decoded_keys = manhuagui_lz_decompress_from_base64(key_data)
    key_parts = decoded_keys.split("|") if isinstance(decoded_keys, str) else []

    def encode_number(value: int) -> str:
        if value < alphabet_size:
            prefix = ""
        else:
            prefix = encode_number(value // alphabet_size)
        remainder = value % alphabet_size
        if remainder > 35:
            return prefix + chr(remainder + 29)
        return prefix + "0123456789abcdefghijklmnopqrstuvwxyz"[remainder]

    replacements = {}
    for index in range(word_count):
        word = encode_number(index)
        replacement = key_parts[index] if index < len(key_parts) else ""
        replacements[word] = replacement or word

    return re.sub(r"\b(\w+)\b", lambda match: replacements.get(match.group(1), match.group(1)), payload)


def fix_manhuagui_json_text(js_text: str) -> str:
    js_text = re.sub(r'(:\s*),', r': null,', js_text)

    empty_keys = re.findall(r'""\s*:', js_text)
    for index in range(len(empty_keys)):
        js_text = js_text.replace('"":', f'"e{index}":', 1)

    js_text = re.sub(r',\s*(?=[}\]])', '', js_text)
    return js_text


class ManhuaguiAdapter(BaseSiteAdapter):
    IMAGE_SERVERS = (
        "i.hamreus.com",
        "us2.hamreus.com",
        "us.hamreus.com",
        "dx.hamreus.com",
        "eu.hamreus.com",
        "lt.hamreus.com",
    )
    TEMP_CHAPTER_PREFIX = ".下载中_"

    def __init__(self):
        super().__init__(
            key="manhuagui",
            display_name="漫画柜",
            supported_domains=("www.manhuagui.com", "manhuagui.com"),
            supports_discovery=True,
            supports_search=True,
            supports_download=True,
            discovery_sections={
                "日排行": "rank-day",
                "周排行": "rank-week",
                "月排行": "rank-month",
                "总排行": "rank-total",
            },
            discovery_placeholder="排行榜",
            status_hint="已启用排行榜、站内搜索和手动 URL 下载。",
        )
        self._session_headers = {
            "DNT": "1",
            "Connection": "keep-alive",
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
            "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
            "Cache-Control": "no-cache",
            "Pragma": "no-cache",
            "Upgrade-Insecure-Requests": "1",
            "User-Agent": (
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/137.0.0.0 Safari/537.36"
            ),
        }
        self._thread_local = threading.local()
        self._prefer_env_html_session = self._has_proxy_env()
        self._prefer_env_image_session = self._has_proxy_env()
        self._manual_proxy_url = ""
        self._manual_proxy_dict = None

    def _build_session(self, trust_env: bool, proxy_dict: Optional[Dict[str, str]] = None) -> requests.Session:
        session = requests.Session()
        session.trust_env = trust_env
        session.headers.update(self._session_headers)
        if proxy_dict:
            session.proxies.update(proxy_dict)
        return session

    def _get_session(self, mode: str) -> requests.Session:
        attr_name = f"_{mode}_session"
        session = getattr(self._thread_local, attr_name, None)
        if session is None:
            if mode == "manual":
                session = self._build_session(trust_env=False, proxy_dict=self._manual_proxy_dict)
            elif mode == "env":
                session = self._build_session(trust_env=True)
            else:
                session = self._build_session(trust_env=False)
            setattr(self._thread_local, attr_name, session)
        return session

    def _normalize_manual_proxy(self, proxy_url: str) -> Tuple[str, Optional[Dict[str, str]]]:
        normalized = (proxy_url or "").strip()
        if not normalized:
            return "", None

        if "://" not in normalized:
            normalized = f"http://{normalized}"

        parsed = urlparse(normalized)
        if not parsed.scheme or not parsed.netloc:
            raise ValueError("代理地址格式不正确，请使用 host:port 或 http://host:port")

        supported_schemes = {"http", "https", "socks5", "socks5h"}
        if parsed.scheme.lower() not in supported_schemes:
            raise ValueError("当前仅支持 http/https/socks5/socks5h 代理地址")

        proxy_dict = {
            "http": normalized,
            "https": normalized,
        }
        return normalized, proxy_dict

    def supports_manual_proxy(self) -> bool:
        return True

    def set_manual_proxy(self, proxy_url: str):
        normalized, proxy_dict = self._normalize_manual_proxy(proxy_url)
        self._manual_proxy_url = normalized
        self._manual_proxy_dict = proxy_dict
        self._thread_local = threading.local()
        self._prefer_env_html_session = self._has_proxy_env() and not proxy_dict
        self._prefer_env_image_session = self._has_proxy_env() and not proxy_dict

    def get_manual_proxy_url(self) -> str:
        return self._manual_proxy_url

    def has_manual_proxy(self) -> bool:
        return bool(self._manual_proxy_dict)

    def configure_requests_session(self, session: requests.Session, for_image: bool = False):
        session.headers.update(self._session_headers)
        if self.is_proxy_pool_enabled():
            session.trust_env = False
            session.proxies.clear()
            return
        if self._manual_proxy_dict:
            session.trust_env = False
            session.proxies.clear()
            session.proxies.update(self._manual_proxy_dict)
            return
        session.trust_env = self.should_use_env_for_http()
        session.proxies.clear()

    def _has_proxy_env(self) -> bool:
        proxy_env_names = (
            "HTTP_PROXY",
            "HTTPS_PROXY",
            "ALL_PROXY",
            "http_proxy",
            "https_proxy",
            "all_proxy",
        )
        return any(os.environ.get(name) for name in proxy_env_names)

    def _iter_request_sessions(self, prefer_env: Optional[bool] = None):
        direct_session = self._get_session("direct")
        if self.is_proxy_pool_enabled():
            return (("direct", direct_session), ("pool", direct_session))

        if self._manual_proxy_dict:
            manual_session = self._get_session("manual")
            return (("manual", manual_session), ("direct", direct_session))

        has_proxy_env = self._has_proxy_env()
        if not has_proxy_env:
            return (("direct", direct_session),)

        env_session = self._get_session("env")
        prefer_env = self._prefer_env_html_session if prefer_env is None else prefer_env
        primary = ("env", env_session) if prefer_env else ("direct", direct_session)
        secondary = ("direct", direct_session) if prefer_env else ("env", env_session)
        return (primary, secondary)

    def _request_html(
        self,
        url: str,
        referer: Optional[str] = None,
        timeout: Tuple[int, int] = (8, 15),
        retry_rounds: int = 3,
        cooldown_seconds: float = 1.6,
        prefer_env: Optional[bool] = None,
        stop_event=None,
    ) -> str:
        errors = []
        headers = {}
        if referer:
            headers["Referer"] = referer

        for attempt in range(retry_rounds):
            if stop_event is not None and stop_event.is_set():
                raise OperationCancelledError("已停止连通性测试")
            current_prefer_env = self._has_proxy_env() if prefer_env is True else prefer_env
            for mode, session in self._iter_request_sessions(prefer_env=current_prefer_env):
                if stop_event is not None and stop_event.is_set():
                    raise OperationCancelledError("已停止连通性测试")
                try:
                    response = self.request_with_session(
                        session,
                        "GET",
                        url,
                        headers=headers,
                        timeout=timeout,
                        use_proxy_pool=(mode == "pool"),
                        proxy_attempts=3 if mode == "pool" else 1,
                        stop_event=stop_event,
                    )
                    response.raise_for_status()
                    self._prefer_env_html_session = (mode == "env")
                    return response.text
                except OperationCancelledError:
                    raise
                except Exception as exc:
                    errors.append(f"{mode} -> {exc}")
            if attempt < retry_rounds - 1:
                if stop_event is not None and stop_event.is_set():
                    raise OperationCancelledError("已停止连通性测试")
                time.sleep(cooldown_seconds + attempt * 0.8)

        detail = " | ".join(errors[-6:]) if errors else "未知网络错误"
        raise RuntimeError(f"Manhuagui 页面请求失败: {detail}")

    def _request_html_interactive(self, url: str, referer: Optional[str] = None, stop_event=None) -> str:
        using_proxy = self.is_proxy_pool_enabled() or self.has_manual_proxy() or self._has_proxy_env()
        timeout = (5, 10) if using_proxy else (4, 7)
        retry_rounds = 2 if using_proxy else 1
        cooldown_seconds = 0.5 if using_proxy else 0.0
        return self._request_html(
            url,
            referer=referer,
            timeout=timeout,
            retry_rounds=retry_rounds,
            cooldown_seconds=cooldown_seconds,
            prefer_env=self._has_proxy_env(),
            stop_event=stop_event,
        )

    def _request_rank_html(self, url: str, referer: Optional[str] = None, stop_event=None) -> str:
        using_proxy = self.is_proxy_pool_enabled() or self.has_manual_proxy() or self._has_proxy_env()
        timeout = (8, 16) if using_proxy else (8, 14)
        retry_rounds = 2
        cooldown_seconds = 0.8 if using_proxy else 0.5
        return self._request_html(
            url,
            referer=referer,
            timeout=timeout,
            retry_rounds=retry_rounds,
            cooldown_seconds=cooldown_seconds,
            prefer_env=self._has_proxy_env(),
            stop_event=stop_event,
        )

    def probe_connection(self, target_url: str, stop_event=None) -> Tuple[int, str]:
        self._request_html_interactive(
            target_url,
            referer=f"https://{self.supported_domains[0]}/",
            stop_event=stop_event,
        )
        return 200, target_url

    def adjust_download_settings(self, chapter_concurrency: int, image_concurrency: int) -> Tuple[int, int, str]:
        adjusted_chapter = min(chapter_concurrency, 1)
        adjusted_image = min(image_concurrency, 3)
        if adjusted_chapter != chapter_concurrency or adjusted_image != image_concurrency:
            return (
                adjusted_chapter,
                adjusted_image,
                f"{self.display_name} 当前容易触发超时，已自动调整为章节并发 {adjusted_chapter}、图片并发 {adjusted_image} 以提升稳定性。",
            )
        return adjusted_chapter, adjusted_image, ""

    def get_chapter_retry_limit(self) -> int:
        return 4

    def get_retry_delay_seconds(self, retry_count: int) -> float:
        return min(10 + (retry_count - 1) * 8, 36)

    def should_retry_download_error(self, error: Exception) -> bool:
        message = str(error or "")
        transient_markers = (
            "Read timed out",
            "ConnectTimeout",
            "Connection to",
            "RemoteDisconnected",
            "ChunkedEncodingError",
            "ProxyError",
            "页面请求失败",
            "图片下载不完整",
        )
        return any(marker in message for marker in transient_markers)

    def should_use_env_for_http(self) -> bool:
        if self.is_proxy_pool_enabled():
            return False
        if self._manual_proxy_dict:
            return False
        return self._prefer_env_html_session or self._prefer_env_image_session or self._has_proxy_env()

    def is_single_page_section(self, section: str) -> bool:
        return str(section or "").startswith("rank-")

    def _build_rank_page_url(self, section: str) -> Tuple[str, str]:
        section_map = {
            "rank-day": (f"https://{self.supported_domains[0]}/rank/", "日排行"),
            "rank-week": (f"https://{self.supported_domains[0]}/rank/week.html", "周排行"),
            "rank-month": (f"https://{self.supported_domains[0]}/rank/month.html", "月排行"),
            "rank-total": (f"https://{self.supported_domains[0]}/rank/total.html", "总排行"),
        }
        return section_map.get(section, section_map["rank-day"])

    def _build_mobile_rank_page_url(self, section: str) -> str:
        section_map = {
            "rank-day": "https://m.manhuagui.com/rank/",
            "rank-week": "https://m.manhuagui.com/rank/week.html",
            "rank-month": "https://m.manhuagui.com/rank/month.html",
            "rank-total": "https://m.manhuagui.com/rank/total.html",
        }
        return section_map.get(section, section_map["rank-day"])

    def _parse_rank_cards_from_html(self, html: str, page_url: str, section_label: str) -> List[HomepageMangaCard]:
        soup = BeautifulSoup(html or "", "html.parser")
        cards: List[HomepageMangaCard] = []

        for row in soup.select("table.rank-detail tr"):
            rank_cell = row.select_one("td.rank-no")
            title_link = row.select_one("td.rank-title h5 a[href]")
            if rank_cell is None or title_link is None:
                continue

            title = title_link.get_text(" ", strip=True)
            href = coerce_html_attr_to_str(title_link.get("href", "")).strip()
            if not title or not href:
                continue

            manga_url = urljoin(page_url, href)
            rank_text = rank_cell.get_text(" ", strip=True)
            author_text = ""
            author_node = row.select_one(".rank-author")
            if author_node is not None:
                author_text = author_node.get_text(" ", strip=True).replace(" ,", ",")
            latest_text = ""
            latest_node = row.select_one(".rank-update a")
            if latest_node is not None:
                latest_text = latest_node.get_text(" ", strip=True)
            update_time = ""
            time_node = row.select_one(".rank-time")
            if time_node is not None:
                update_time = time_node.get_text(" ", strip=True)
            score_text = ""
            score_node = row.select_one(".rank-score")
            if score_node is not None:
                score_text = score_node.get_text(" ", strip=True)
            status_text = ""
            status_node = row.select_one("td.rank-title span")
            if status_node is not None:
                status_text = status_node.get_text(" ", strip=True)
            trend_text = ""
            trend_node = row.select_one(".rank-trend span")
            if trend_node is not None:
                trend_classes = " ".join(trend_node.get("class") or [])
                if "trend-up" in trend_classes:
                    trend_text = "趋势: 上升"
                elif "trend-down" in trend_classes:
                    trend_text = "趋势: 下降"
                else:
                    trend_text = "趋势: 持平"

            card = HomepageMangaCard(
                section=section_label,
                title=title,
                manga_url=manga_url,
                chapterlist_url=manga_url,
                cover_url="",
                latest_chapter=latest_text,
                update_time=update_time,
            )

            detail_parts = []
            if author_text:
                detail_parts.append(f"作者: {author_text}")
            if score_text:
                detail_parts.append(f"评分: {score_text}")
            if trend_text:
                detail_parts.append(trend_text)
            setattr(card, "detail_hint", "，".join(detail_parts))

            section_parts = [section_label]
            if rank_text:
                section_parts.append(f"第 {rank_text} 名")
            if status_text:
                section_parts.append(status_text)
            setattr(card, "detail_section_label", " · ".join(section_parts))
            cards.append(card)

        return cards

    def _parse_mobile_rank_cards_from_html(self, html: str, page_url: str, section_label: str) -> List[HomepageMangaCard]:
        soup = BeautifulSoup(html or "", "html.parser")
        cards: List[HomepageMangaCard] = []

        for item in soup.select("div.cont-list ul#detail > li"):
            link = item.select_one("a[href]")
            title_node = item.select_one("a[href] h3")
            if link is None or title_node is None:
                continue

            title = title_node.get_text(" ", strip=True)
            href = coerce_html_attr_to_str(link.get("href", "")).strip()
            if not title or not href:
                continue

            manga_url = urljoin(page_url, href)
            rank_text = ""
            rank_node = item.select_one("div.rank span")
            if rank_node is not None:
                rank_text = rank_node.get_text(" ", strip=True)

            cover_url = ""
            image_node = item.select_one("div.thumb img")
            if image_node is not None:
                cover_url = (
                    coerce_html_attr_to_str(image_node.get("data-src", "")).strip()
                    or coerce_html_attr_to_str(image_node.get("src", "")).strip()
                )
                if cover_url.startswith("//"):
                    cover_url = f"https:{cover_url}"
                elif cover_url:
                    cover_url = urljoin(page_url, cover_url)

            status_text = ""
            status_node = item.select_one("div.thumb i")
            if status_node is not None:
                status_text = status_node.get_text(" ", strip=True)

            detail_map: Dict[str, str] = {}
            for detail_row in item.select("dl"):
                label_node = detail_row.find("dt")
                value_node = detail_row.find("dd")
                if label_node is None or value_node is None:
                    continue
                label = label_node.get_text(" ", strip=True).replace(" ", "").replace("：", "")
                detail_map[label] = value_node.get_text(" ", strip=True)

            author_text = detail_map.get("作者", "")
            category_text = detail_map.get("类别", "")
            latest_text = detail_map.get("更新至", "")
            update_time = detail_map.get("更新于", "")

            card = HomepageMangaCard(
                section=section_label,
                title=title,
                manga_url=manga_url,
                chapterlist_url=manga_url,
                cover_url=cover_url,
                latest_chapter=latest_text,
                update_time=update_time,
            )

            detail_parts = []
            if author_text:
                detail_parts.append(f"作者: {author_text}")
            if category_text:
                detail_parts.append(f"题材: {category_text}")
            setattr(card, "detail_hint", "，".join(detail_parts))

            section_parts = [section_label]
            if rank_text:
                section_parts.append(f"第 {rank_text} 名")
            if status_text:
                section_parts.append(status_text)
            setattr(card, "detail_section_label", " · ".join(section_parts))
            cards.append(card)

        return cards

    def fetch_section_cards(self, section: str, page: int = 1, theme: str = "") -> List[HomepageMangaCard]:
        page_url, section_label = self._build_rank_page_url(section)
        desktop_error = None

        try:
            html = self._request_rank_html(
                page_url,
                referer=f"https://{self.supported_domains[0]}/",
            )
            cards = self._parse_rank_cards_from_html(html, page_url, section_label)
            if cards:
                return cards
            desktop_error = RuntimeError("电脑版排行榜页已返回，但未解析到榜单内容")
        except Exception as exc:
            desktop_error = exc

        print(f"[漫画柜] 电脑版排行榜页请求失败，准备切换移动站榜单页: {desktop_error}")

        mobile_url = self._build_mobile_rank_page_url(section)
        try:
            mobile_html = self._request_rank_html(
                mobile_url,
                referer="https://m.manhuagui.com/",
            )
            mobile_cards = self._parse_mobile_rank_cards_from_html(mobile_html, mobile_url, section_label)
            if mobile_cards:
                print(f"[漫画柜] 已切换到移动站榜单页继续抓取: {section_label}，共 {len(mobile_cards)} 条")
                return mobile_cards
            raise RuntimeError("移动站排行榜页已返回，但未解析到榜单内容")
        except Exception as mobile_exc:
            raise RuntimeError(
                f"Manhuagui 页面请求失败: desktop -> {desktop_error} | mobile -> {mobile_exc}"
            ) from mobile_exc

    def _find_chapter_script_text(self, html: str) -> str:
        soup = BeautifulSoup(html or "", "html.parser")
        for script in soup.find_all("script"):
            script_text = script.get_text() or ""
            if not script_text:
                continue
            if (
                r'["\x65\x76\x61\x6c"]' in script_text
                or "return p;}" in script_text
                or "SMH.imgData(" in script_text
            ):
                return script_text
        return ""

    def _parse_chapter_payload_text(self, payload_text: str) -> Dict:
        normalized = fix_manhuagui_json_text((payload_text or "").strip())
        if not normalized:
            raise RuntimeError("Manhuagui 章节图片数据为空")
        try:
            return json.loads(normalized)
        except json.JSONDecodeError as exc:
            raise RuntimeError("Manhuagui 章节图片数据解析失败") from exc

    def _normalize_image_file_name(self, file_name: Any) -> str:
        normalized = str(file_name or "").strip()
        if normalized.lower().endswith(".webp") and "." in normalized[:-5]:
            return normalized[:-5]
        return normalized

    def _build_single_image_url(
        self,
        path: str,
        file_name: str,
        cid: Any,
        md5: str,
        e_value: Any,
        m_value: Any,
    ) -> str:
        encoded_path = quote(path or "", safe="/")
        encoded_file_name = quote(str(file_name or ""), safe="._-")
        if cid and md5:
            return f"https://{self.IMAGE_SERVERS[0]}{encoded_path}{encoded_file_name}?cid={cid}&md5={md5}"
        if e_value is not None and m_value is not None:
            return f"https://{self.IMAGE_SERVERS[0]}{encoded_path}{encoded_file_name}?e={e_value}&m={m_value}"
        return f"https://{self.IMAGE_SERVERS[0]}{encoded_path}{encoded_file_name}"

    def _build_image_url_variants(self, comic_data: Dict, file_name: Any) -> List[str]:
        path = comic_data.get("path") or ""
        cid = comic_data.get("cid")
        sl_data = comic_data.get("sl") or {}
        md5 = sl_data.get("md5", "")
        e_value = sl_data.get("e")
        m_value = sl_data.get("m")

        raw_file_name = str(file_name or "").strip()
        preferred_file_name = self._normalize_image_file_name(raw_file_name)

        urls: List[str] = []
        for candidate_name in (preferred_file_name, raw_file_name):
            if not candidate_name:
                continue
            image_url = self._build_single_image_url(path, candidate_name, cid, md5, e_value, m_value)
            if image_url not in urls:
                urls.append(image_url)
        return urls

    def _candidate_image_extensions(self, file_name: Any) -> List[str]:
        raw_name = str(file_name or "").strip()
        preferred_name = self._normalize_image_file_name(raw_name)
        extensions: List[str] = []
        for candidate_name in (preferred_name, raw_name):
            ext = os.path.splitext(candidate_name)[1].lower()
            if ext and ext not in extensions:
                extensions.append(ext)
        if not extensions:
            extensions.append(".jpg")
        return extensions

    def _build_image_entries(self, comic_data: Dict) -> List[Dict[str, Any]]:
        files = comic_data.get("files") or []
        entries: List[Dict[str, Any]] = []
        for index, file_name in enumerate(files, 1):
            image_urls = self._build_image_url_variants(comic_data, file_name)
            if not image_urls:
                continue
            entries.append({
                "index": index,
                "urls": image_urls,
                "extensions": self._candidate_image_extensions(file_name),
            })
        return entries

    def _build_download_tasks_for_dir(self, image_entries: List[Dict[str, Any]], chapter_dir: str) -> List[Dict[str, Any]]:
        tasks: List[Dict[str, Any]] = []
        for entry in image_entries:
            dest_stem = os.path.join(chapter_dir, f"{int(entry['index']):03d}")
            candidate_paths: List[str] = []
            for ext in entry.get("extensions") or [".jpg"]:
                normalized_ext = ext.lower()
                if not normalized_ext.startswith("."):
                    normalized_ext = f".{normalized_ext}"
                candidate_path = f"{dest_stem}{normalized_ext}"
                if candidate_path not in candidate_paths:
                    candidate_paths.append(candidate_path)
            tasks.append({
                "index": entry["index"],
                "urls": list(entry.get("urls") or []),
                "dest_stem": dest_stem,
                "candidate_paths": candidate_paths,
            })
        return tasks

    def _remove_file_quietly(self, path: str):
        if not path:
            return
        try:
            if os.path.exists(path):
                os.remove(path)
        except OSError:
            pass

    def _find_existing_image_path(self, candidate_paths: List[str]) -> Optional[str]:
        for candidate_path in candidate_paths:
            self._remove_file_quietly(f"{candidate_path}.part")
            if not os.path.exists(candidate_path):
                continue
            try:
                if os.path.getsize(candidate_path) > 0:
                    return candidate_path
            except OSError:
                pass
            self._remove_file_quietly(candidate_path)
        return None

    def _is_chapter_complete(self, download_tasks: List[Dict[str, Any]]) -> bool:
        return bool(download_tasks) and all(
            self._find_existing_image_path(task.get("candidate_paths") or [])
            for task in download_tasks
        )

    def _prepare_chapter_download_dir(self, chapter_dir: str, image_entries: List[Dict[str, Any]]) -> Tuple[str, List[Dict[str, Any]], bool]:
        existing_tasks = self._build_download_tasks_for_dir(image_entries, chapter_dir)
        if self._is_chapter_complete(existing_tasks):
            return chapter_dir, existing_tasks, True

        chapter_root = os.path.dirname(chapter_dir)
        chapter_dir_name = os.path.basename(chapter_dir)
        temp_chapter_dir = os.path.join(chapter_root, f"{self.TEMP_CHAPTER_PREFIX}{chapter_dir_name}")

        active_dir = temp_chapter_dir
        if os.path.isdir(temp_chapter_dir):
            active_dir = temp_chapter_dir
        elif os.path.isdir(chapter_dir):
            try:
                os.replace(chapter_dir, temp_chapter_dir)
                active_dir = temp_chapter_dir
            except OSError:
                active_dir = chapter_dir

        os.makedirs(active_dir, exist_ok=True)
        return active_dir, self._build_download_tasks_for_dir(image_entries, active_dir), False

    def _commit_chapter_download_dir(self, active_dir: str, final_dir: str):
        if os.path.abspath(active_dir) == os.path.abspath(final_dir):
            return
        if os.path.isdir(final_dir):
            shutil.rmtree(final_dir)
        os.replace(active_dir, final_dir)

    def _iter_image_request_urls(self, image_url: str) -> List[str]:
        parsed = urlparse(image_url)
        scheme = parsed.scheme or "https"
        request_path = parsed.path + (f"?{parsed.query}" if parsed.query else "")
        hosts: List[str] = []
        if parsed.netloc:
            hosts.append(parsed.netloc)
        for server in self.IMAGE_SERVERS:
            if server not in hosts:
                hosts.append(server)
        return [f"{scheme}://{host}{request_path}" for host in hosts]

    def _looks_like_html_bytes(self, chunk: bytes) -> bool:
        sample = (chunk or b"").lstrip()[:64].lower()
        return (
            sample.startswith(b"<!doctype html")
            or sample.startswith(b"<html")
            or sample.startswith(b"<body")
            or sample.startswith(b"<?xml")
        )

    def _select_dest_path_for_url(self, dest_stem: str, image_url: str, candidate_paths: List[str]) -> str:
        path = urlparse(image_url).path
        ext = os.path.splitext(path)[1].lower()
        if ext:
            resolved = f"{dest_stem}{ext}"
            if resolved in candidate_paths or not candidate_paths:
                return resolved
        return candidate_paths[0] if candidate_paths else f"{dest_stem}.jpg"

    def _extract_payload_text_from_script(self, script_text: str) -> str:
        start_token = "SMH.imgData("
        start_index = script_text.find(start_token)
        if start_index < 0:
            return ""
        start_index += len(start_token)

        end_index = script_text.find(").preInit()", start_index)
        if end_index < 0:
            end_index = script_text.find(").preInit(", start_index)
        if end_index < 0:
            return ""
        return script_text[start_index:end_index].strip()

    def _extract_chapter_payload_python(self, html: str) -> Dict:
        script_text = self._find_chapter_script_text(html)
        if not script_text:
            raise RuntimeError("Manhuagui 章节脚本结构已变化，未找到脚本段")

        direct_payload = self._extract_payload_text_from_script(script_text)
        if direct_payload:
            return self._parse_chapter_payload_text(direct_payload)

        packed_patterns = (
            re.compile(r"return p;}\('(.*?)',(\d+),(\d+),'(.*?)'\[", re.S),
            re.compile(r"return p;}\('(.*?)',(\d+),(\d+),'(.*?)'\.split\('\|'\)", re.S),
        )

        unpacked_js = None
        for pattern in packed_patterns:
            match = pattern.search(script_text)
            if not match:
                continue
            unpacked_js = manhuagui_unpack_packed_js(
                match.group(1),
                int(match.group(2)),
                int(match.group(3)),
                match.group(4),
            )
            if unpacked_js:
                break

        if not unpacked_js:
            raise RuntimeError("Manhuagui 章节脚本结构已变化，未找到可解包数据")

        payload_text = self._extract_payload_text_from_script(unpacked_js)
        if not payload_text:
            raise RuntimeError("Manhuagui 章节图片数据结构已变化，未找到 SMH.imgData")

        return self._parse_chapter_payload_text(payload_text)

    def _extract_chapter_payload_cscript(self, html: str) -> Dict:
        script_match = re.search(r'\["\\x65\\x76\\x61\\x6c"\](.*?)</script>', html, re.S)
        if not script_match:
            raise RuntimeError("Manhuagui 章节脚本结构已变化，未找到加密数据段")

        js_payload = script_match.group(1).strip()
        script = "\n".join([
            MANHUAGUI_LZJS,
            f"var __decoded = {js_payload};",
            "var __start = __decoded.indexOf('SMH.imgData(');",
            "var __end = __decoded.indexOf(').preInit();', __start);",
            "if (__start < 0 || __end < 0) { WScript.Echo(''); }",
            "else { WScript.Echo(encodeURIComponent(__decoded.substring(__start + 12, __end))); }",
        ])

        temp_path = None
        try:
            with tempfile.NamedTemporaryFile("w", suffix=".js", delete=False, encoding="utf-8") as temp_file:
                temp_file.write(script)
                temp_path = temp_file.name

            result = subprocess.run(
                ["cscript.exe", "//Nologo", temp_path],
                capture_output=True,
                text=True,
                timeout=20,
            )
            if result.returncode != 0:
                raise RuntimeError(result.stderr.strip() or "cscript 执行失败")

            payload_text = unquote((result.stdout or "").strip())
            if not payload_text:
                raise RuntimeError("Manhuagui 章节图片数据结构已变化，未找到 SMH.imgData")
            return json.loads(payload_text)
        except subprocess.TimeoutExpired as exc:
            raise RuntimeError("Manhuagui 章节脚本解析超时") from exc
        except json.JSONDecodeError as exc:
            raise RuntimeError("Manhuagui 章节图片数据解析失败") from exc
        finally:
            if temp_path:
                try:
                    os.remove(temp_path)
                except OSError:
                    pass

    def _extract_chapter_payload(self, html: str) -> Dict:
        errors = []

        try:
            payload = self._extract_chapter_payload_python(html)
            if payload:
                return payload
        except Exception as exc:
            errors.append(f"python -> {exc}")

        try:
            payload = self._extract_chapter_payload_cscript(html)
            if payload:
                return payload
        except Exception as exc:
            errors.append(f"cscript -> {exc}")

        detail = " | ".join(errors) if errors else "未知错误"
        raise RuntimeError(f"Manhuagui 章节图片数据解析失败: {detail}")

    def _decode_viewstate_html(self, soup: BeautifulSoup) -> Optional[BeautifulSoup]:
        warning_node = soup.find("div", class_="warning-bar")
        viewstate_node = soup.select_one("input#__VIEWSTATE")
        if warning_node is None or viewstate_node is None:
            return None

        encoded_html = coerce_html_attr_to_str(viewstate_node.get("value")).strip()
        if not encoded_html:
            return None

        decoded_html = manhuagui_lz_decompress_from_base64(encoded_html)
        if not decoded_html:
            return None
        return BeautifulSoup(decoded_html, "html.parser")

    def _extract_chapters_from_list_soup(self, chapter_soup: BeautifulSoup, manga_id: str) -> List[Dict]:
        chapter_pattern = re.compile(rf"/comic/{re.escape(str(manga_id))}/(\d+)\.html$")
        chapters = []
        seen = set()

        list_groups = chapter_soup.select("div.chapter-list")
        for group in list_groups:
            for part in group.select("ul"):
                part_chapters = []
                for li in part.select("li"):
                    link = li.find("a", href=True)
                    if not link:
                        continue
                    href = coerce_html_attr_to_str(link.get("href", "")).strip()
                    match = chapter_pattern.search(href)
                    if not match:
                        continue
                    chapter_id = match.group(1)
                    span = li.find("span")
                    chapter_title = ""
                    if span is not None:
                        chapter_title = (span.find(string=True, recursive=False) or "").strip()
                    if not chapter_title:
                        chapter_title = coerce_html_attr_to_str(link.get("title", "")).strip() or link.get_text(" ", strip=True) or chapter_id
                    part_chapters.append((chapter_id, chapter_title))

                for chapter_id, chapter_title in reversed(part_chapters):
                    if chapter_id in seen:
                        continue
                    seen.add(chapter_id)
                    chapters.append({
                        "slug": chapter_id,
                        "order": len(chapters),
                        "title": chapter_title,
                        "updated_at": "",
                    })

        return chapters

    def _extract_chapters_from_link_scan(self, chapter_soup: BeautifulSoup, manga_id: str) -> List[Dict]:
        chapter_pattern = re.compile(rf"/comic/{re.escape(str(manga_id))}/(\d+)\.html$")
        chapters = []
        seen = set()

        for link in chapter_soup.find_all("a", href=True):
            match = chapter_pattern.search(coerce_html_attr_to_str(link.get("href", "")).strip())
            if not match:
                continue
            chapter_id = match.group(1)
            if chapter_id in seen:
                continue
            seen.add(chapter_id)
            chapter_title = (
                coerce_html_attr_to_str(link.get("title", "")).strip()
                or link.get_text(" ", strip=True)
                or chapter_id
            )
            chapters.append({
                "slug": chapter_id,
                "order": len(chapters),
                "title": chapter_title,
                "updated_at": "",
            })

        chapters.reverse()
        for index, chapter in enumerate(chapters):
            chapter["order"] = index
        return chapters

    def _build_image_urls(self, comic_data: Dict) -> List[str]:
        image_urls = []
        for file_name in comic_data.get("files") or []:
            variants = self._build_image_url_variants(comic_data, file_name)
            if variants:
                image_urls.append(variants[0])
        return image_urls

    def _download_image(
        self,
        image_urls: List[str],
        dest_stem: str,
        candidate_paths: List[str],
        referer: str,
        stop_event=None,
    ) -> bool:
        if stop_event is not None and stop_event.is_set():
            return False
        if self._find_existing_image_path(candidate_paths):
            return True

        for image_url in image_urls:
            final_path = self._select_dest_path_for_url(dest_stem, image_url, candidate_paths)
            temp_path = f"{final_path}.part"
            for attempt_url in self._iter_image_request_urls(image_url):
                for mode, session in self._iter_request_sessions(prefer_env=self._prefer_env_image_session):
                    if stop_event is not None and stop_event.is_set():
                        self._remove_file_quietly(temp_path)
                        return False
                    try:
                        with self.request_with_session(
                            session,
                            "GET",
                            attempt_url,
                            headers={"Referer": referer, "Accept": "image/avif,image/webp,image/*,*/*;q=0.8"},
                            timeout=30,
                            stream=True,
                            use_proxy_pool=(mode == "pool"),
                            proxy_attempts=2 if mode == "pool" else 1,
                        ) as response:
                            if response.status_code != 200:
                                continue

                            content_type = (response.headers.get("Content-Type") or "").lower()
                            if content_type and not content_type.startswith("image/"):
                                continue

                            os.makedirs(os.path.dirname(final_path), exist_ok=True)
                            bytes_written = 0
                            first_chunk = True
                            with open(temp_path, "wb") as file_obj:
                                for chunk in response.iter_content(65536):
                                    if stop_event is not None and stop_event.is_set():
                                        raise InterruptedError
                                    if not chunk:
                                        continue
                                    if first_chunk:
                                        first_chunk = False
                                        if self._looks_like_html_bytes(chunk) and not content_type.startswith("image/"):
                                            raise ValueError("图片响应不是图片")
                                    file_obj.write(chunk)
                                    bytes_written += len(chunk)

                            if bytes_written <= 0:
                                self._remove_file_quietly(temp_path)
                                continue

                            os.replace(temp_path, final_path)
                            self._prefer_env_image_session = (mode == "env")
                            return True
                    except InterruptedError:
                        self._remove_file_quietly(temp_path)
                        return False
                    except Exception:
                        self._remove_file_quietly(temp_path)
                        continue

        for candidate_path in candidate_paths:
            self._remove_file_quietly(candidate_path)
            self._remove_file_quietly(f"{candidate_path}.part")
        return False

    def get_manga_info_from_url(self, url: str):
        parsed = urlparse((url or "").strip())
        path_parts = [part for part in parsed.path.strip("/").split("/") if part]

        manga_id = None
        chapter_id = None

        if len(path_parts) >= 2 and path_parts[0] == "comic":
            manga_id = path_parts[1]
            if len(path_parts) >= 3:
                chapter_match = re.match(r"(\d+)\.html$", path_parts[2])
                if chapter_match:
                    chapter_id = chapter_match.group(1)

        if not manga_id:
            return None, None, None
        return manga_id, manga_id, chapter_id

    def get_manga_cache_key(self, url: str) -> str:
        manga_id, _, _ = self.get_manga_info_from_url(url)
        if manga_id:
            return f"{self.key}:{manga_id}"
        return super().get_manga_cache_key(url)

    def fetch_search_cards(self, keyword: str, page: int = 1) -> List[HomepageMangaCard]:
        keyword = (keyword or "").strip()
        if not keyword:
            return []

        try:
            cards = self._fetch_search_cards_desktop(keyword, page=page)
            if cards:
                return cards
            print(f"[漫画柜] 桌面搜索页返回 0 条结果，准备切换移动站搜索: 关键词={keyword}, 第 {page} 页")
        except Exception as exc:
            print(f"[漫画柜] 桌面搜索页请求失败，准备切换移动站搜索: {exc}")

        return self._fetch_search_cards_mobile(keyword, page=page)

    def _fetch_search_cards_desktop(self, keyword: str, page: int = 1) -> List[HomepageMangaCard]:
        page = max(int(page or 1), 1)
        page_suffix = "" if page == 1 else f"_p{page}"
        encoded_keyword = quote(keyword, safe="")
        search_url = f"https://{self.supported_domains[0]}/s/{encoded_keyword}{page_suffix}.html"
        html = self._request_html_interactive(
            search_url,
            referer=f"https://{self.supported_domains[0]}/",
        )
        soup = BeautifulSoup(html, "html.parser")

        cards: List[HomepageMangaCard] = []
        for item in soup.select(".book-result > ul > li"):
            title_link = item.select_one(".book-detail > dl > dt > a[href]")
            if not title_link:
                continue

            title = title_link.get_text(strip=True)
            href = coerce_html_attr_to_str(title_link.get("href", "")).strip()
            if not title or not href:
                continue

            manga_url = urljoin(search_url, href)
            cover_node = item.select_one(".book-cover > a > img")
            cover_url = ""
            if cover_node is not None:
                cover_url = resolve_media_url(
                    search_url,
                    coerce_html_attr_to_str(cover_node.get("data-src") or cover_node.get("src") or ""),
                )

            status_node = item.select_one(".book-detail > dl > dd:nth-child(2) span span")
            year_node = item.select_one(".book-detail > dl > dd:nth-child(3) span a")
            author_node = item.select_one(".book-detail > dl > dd:nth-child(4)")

            status_text = status_node.get_text(strip=True) if status_node else ""
            year_text = year_node.get_text(strip=True) if year_node else ""
            author_text = ""
            if author_node:
                author_text = author_node.get_text(" ", strip=True)
                author_text = re.sub(r"^作者[:：]\s*", "", author_text)

            card = HomepageMangaCard(
                section="搜索结果",
                title=title,
                manga_url=manga_url,
                chapterlist_url=manga_url,
                cover_url=cover_url,
            )

            detail_parts = []
            if author_text:
                detail_parts.append(f"作者: {author_text}")
            if year_text:
                detail_parts.append(f"年份: {year_text}")
            setattr(card, "detail_hint", "，".join(detail_parts))
            setattr(card, "detail_section_label", f"状态: {status_text or '未知'}")

            cards.append(card)

        return cards

    def _fetch_search_cards_mobile(self, keyword: str, page: int = 1) -> List[HomepageMangaCard]:
        page = max(int(page or 1), 1)
        encoded_keyword = quote(keyword, safe="")
        page_suffix = "_o1.html" if page == 1 else f"_p{page}.html"
        search_url = f"https://m.manhuagui.com/s/{encoded_keyword}{page_suffix}"
        html = self._request_html_interactive(
            search_url,
            referer="https://m.manhuagui.com/",
        )
        soup = BeautifulSoup(html, "html.parser")

        cards: List[HomepageMangaCard] = []
        for item in soup.select(".cont-list > ul > li"):
            link = item.select_one("a[href]")
            title_node = item.select_one("h3")
            if link is None or title_node is None:
                continue

            href = coerce_html_attr_to_str(link.get("href", "")).strip()
            title = title_node.get_text(" ", strip=True)
            if not href or not title:
                continue

            manga_url = urljoin(search_url, href)
            cover_node = item.select_one(".thumb img")
            cover_url = ""
            if cover_node is not None:
                cover_url = resolve_media_url(
                    search_url,
                    coerce_html_attr_to_str(cover_node.get("data-src") or cover_node.get("src") or ""),
                )

            status_node = item.select_one(".thumb i")
            status_text = status_node.get_text(" ", strip=True) if status_node is not None else ""

            info_map: Dict[str, str] = {}
            for row in item.select("dl"):
                dt = row.find("dt")
                dd = row.find("dd")
                if dt is None or dd is None:
                    continue
                key = dt.get_text(" ", strip=True).replace("：", "").replace(":", "").strip()
                value = dd.get_text(" ", strip=True).strip()
                if key and value:
                    info_map[key] = value

            author_text = info_map.get("作者", "")
            category_text = info_map.get("类别", "")
            latest_text = info_map.get("更新至", "")
            updated_text = info_map.get("更新于", "")

            card = HomepageMangaCard(
                section="搜索结果",
                title=title,
                manga_url=manga_url,
                chapterlist_url=manga_url,
                cover_url=cover_url,
                latest_chapter=latest_text,
                update_time=updated_text,
            )

            detail_parts = []
            if author_text:
                detail_parts.append(f"作者: {author_text}")
            if category_text:
                detail_parts.append(f"类型: {category_text}")
            setattr(card, "detail_hint", "，".join(detail_parts))
            setattr(card, "detail_section_label", f"状态: {status_text or '未知'} · 移动站搜索")
            cards.append(card)

        print(f"[漫画柜] 移动站搜索完成: 关键词={keyword}, 第 {page} 页, 共 {len(cards)} 条")
        return cards

    def _parse_detail_page(self, html: str, manga_id: str, detail_url: str):
        soup = BeautifulSoup(html, "html.parser")
        chapter_soup = self._decode_viewstate_html(soup) or soup

        title = ""
        title_node = soup.find("h1")
        if title_node:
            title = title_node.get_text(strip=True)
        if not title:
            title_tag = soup.find("title")
            if title_tag:
                title = title_tag.get_text(strip=True).split("漫画_")[0].strip(" -")
        if not title:
            title = f"Comic_{manga_id}"

        cover_url = extract_cover_url_from_html(html, detail_url)
        chapters = self._extract_chapters_from_list_soup(chapter_soup, manga_id)
        if not chapters:
            chapters = self._extract_chapters_from_link_scan(chapter_soup, manga_id)

        return title, cover_url, chapters

    def get_all_chapters(self, manga_id):
        detail_url = f"https://{self.supported_domains[0]}/comic/{manga_id}/"
        html = self._request_html_interactive(
            detail_url,
        )
        title, _, chapters = self._parse_detail_page(html, manga_id, detail_url)
        return title, chapters

    def fetch_manga_detail(self, url: str):
        manga_id, _, start_slug = self.get_manga_info_from_url(url)
        if not manga_id:
            raise RuntimeError(f"{self.display_name} 无法识别该漫画链接")

        detail_url = f"https://{self.supported_domains[0]}/comic/{manga_id}/"
        html = self._request_html_interactive(
            detail_url,
        )
        title, cover_url, chapters = self._parse_detail_page(html, manga_id, detail_url)
        latest = chapters[-1] if chapters else {}
        start_chapter_title = find_start_chapter_title(chapters, start_slug)
        chapter_count = len(chapters)
        detail_parts = [f"共 {chapter_count} 章"] if chapter_count else ["未解析到章节列表"]
        if start_chapter_title:
            detail_parts.append(f"当前链接定位到 {start_chapter_title}")

        return MangaDetail(
            title=title,
            manga_url=(url or "").strip(),
            section="手动链接",
            cover_url=cover_url,
            latest_chapter=latest.get("title") or "-",
            update_time="-",
            detail_hint="，".join(detail_parts),
            detail_section_label=f"站点: {self.display_name}",
            chapter_count=chapter_count,
            start_chapter_title=start_chapter_title,
        )

    def build_chapter_url_template(self, manga_slug: str) -> str:
        return f"https://{self.supported_domains[0]}/comic/{manga_slug}/{{slug}}.html"

    def download_chapter_images(
        self,
        chapter_slug,
        base_url_template,
        root_dir,
        max_concurrent_images=5,
        stop_event=None,
        show_progress=True,
    ):
        chapter_url = base_url_template.format(slug=chapter_slug)
        html = self._request_html(
            chapter_url,
            referer=f"https://{self.supported_domains[0]}/",
            timeout=(9, 16),
            retry_rounds=3,
            cooldown_seconds=1.8,
            prefer_env=self._has_proxy_env(),
        )
        comic_data = self._extract_chapter_payload(html)

        manga_name = comic_data.get("bname") or "Manhuagui"
        chapter_name = comic_data.get("cname") or chapter_slug
        chapter_dir_name = f"{str(chapter_slug).zfill(6)}_{sanitize_filename(str(chapter_name))}"
        chapter_dir = os.path.join(root_dir, chapter_dir_name)

        image_entries = self._build_image_entries(comic_data)
        if not image_entries:
            with print_lock:
                print(f"[警告] Manhuagui 章节无图片数据: {chapter_url}")
            return 0, None, {"slug": chapter_slug}

        active_chapter_dir, download_tasks, is_already_complete = self._prepare_chapter_download_dir(chapter_dir, image_entries)
        if is_already_complete:
            with print_lock:
                print(f"[跳过] Manhuagui 章节 {chapter_dir_name}: 已完整下载")
            return len(download_tasks), None, {"slug": chapter_slug}

        existing_count = 0
        pending_tasks = []
        for task in download_tasks:
            if self._find_existing_image_path(task.get("candidate_paths") or []):
                existing_count += 1
            else:
                pending_tasks.append(task)

        if existing_count >= len(download_tasks) and download_tasks:
            self._commit_chapter_download_dir(active_chapter_dir, chapter_dir)
            with print_lock:
                print(f"[跳过] Manhuagui 章节 {chapter_dir_name}: 已完整下载")
            return len(download_tasks), None, {"slug": chapter_slug}

        progress = tqdm(
            total=len(download_tasks),
            desc=f"📖 {chapter_dir_name[:30]}",
            unit="img",
            leave=False,
            dynamic_ncols=True,
            disable=not show_progress,
            initial=existing_count,
        )

        success_count = existing_count
        with progress:
            if pending_tasks:
                with ThreadPoolExecutor(max_workers=max_concurrent_images) as executor:
                    future_map = {
                        executor.submit(
                            self._download_image,
                            task["urls"],
                            task["dest_stem"],
                            task["candidate_paths"],
                            chapter_url,
                            stop_event,
                        ): task
                        for task in pending_tasks
                    }
                    for future in as_completed(future_map):
                        if stop_event is not None and stop_event.is_set():
                            break
                        if future.result():
                            success_count += 1
                        progress.update(1)

        if success_count >= len(download_tasks) and download_tasks:
            self._commit_chapter_download_dir(active_chapter_dir, chapter_dir)

        with print_lock:
            print(f"[完成] Manhuagui 章节下载完成: {manga_name} / {chapter_dir_name} ({success_count}/{len(download_tasks)})")

        if success_count < len(download_tasks) and not (stop_event is not None and stop_event.is_set()):
            raise RuntimeError(f"Manhuagui 图片下载不完整: {success_count}/{len(download_tasks)}")

        next_id = comic_data.get("nextId")
        next_slug = str(next_id) if next_id else None
        return success_count, next_slug, {"slug": next_slug} if next_slug else {"slug": chapter_slug}

