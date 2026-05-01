"""连接诊断工具：错误分类、路由标签、排障文本。"""
from __future__ import annotations

from downcomic import proxy_pool


def is_site_access_blocked_error(error: Exception) -> bool:
    return "暂时拒绝当前网络环境访问" in str(error or "")


def is_site_unreachable_error(error: Exception) -> bool:
    message = str(error or "")
    unreachable_markers = (
        "页面请求失败",
        "Connection to",
        "Max retries exceeded",
        "Read timed out",
        "ConnectTimeout",
        "ReadTimeout",
        "ProxyError",
        "timed out",
        "NameResolutionError",
    )
    return any(marker in message for marker in unreachable_markers)


def get_connection_route_label(adapter) -> str:
    if getattr(adapter, "key", "") == "manhuagui" and proxy_pool.enabled:
        return "直连优先，代理池兜底"
    if bool(getattr(adapter, "supports_proxy_pool", lambda: False)()) and proxy_pool.enabled:
        return "内置代理池"
    supports_manual_proxy = bool(getattr(adapter, "supports_manual_proxy", lambda: False)())
    if supports_manual_proxy and adapter.has_manual_proxy():
        return f"手动代理 {adapter.get_manual_proxy_url()}"
    if bool(getattr(adapter, "should_use_env_for_http", lambda: False)()):
        return "系统代理/环境代理"
    return "直连"


def get_connection_troubleshooting_text(adapter) -> str:
    if getattr(adapter, "key", "") == "manhuagui" and proxy_pool.enabled:
        return "\n".join([
            "1. 漫画柜当前会优先尝试直连，只有直连失败时才会回退到代理池。",
            "2. 如果仍失败，先关闭内置代理池再测一次，确认是不是本机网络本身不通。",
            "3. 如果直连不稳，优先改用你自己的稳定手动代理节点。",
        ])
    if bool(getattr(adapter, "supports_proxy_pool", lambda: False)()) and proxy_pool.enabled:
        lines = [
            '1. 先点"测试连接"，确认当前代理池里是否能拿到可用节点。',
            '2. 如果仍失败，先关闭内置代理池后再测一次直连，判断是不是代理源质量问题。',
            '3. 如果当前站点支持手动代理，也可以改填你自己的稳定代理节点再试。',
        ]
        if not bool(getattr(adapter, "supports_manual_proxy", lambda: False)()):
            lines[2] = "3. 用浏览器直接打开站点首页，确认不是站点本身临时异常。"
        return "\n".join(lines)
    supports_manual_proxy = bool(getattr(adapter, "supports_manual_proxy", lambda: False)())
    if supports_manual_proxy and adapter.has_manual_proxy():
        return "\n".join([
            '1. 先点"测试连接"，确认当前代理节点是否真的可用。',
            '2. 如果仍失败，优先更换代理节点，或先关闭代理后改用其它网络。',
            '3. 用浏览器直接打开站点首页，确认不是站点本身临时异常。',
        ])
    if supports_manual_proxy:
        return "\n".join([
            "1. 先换手机热点或其它网络做对比测试。",
            "2. 如果换网后恢复，说明当前宽带/IP 很可能被限制了。",
            '3. 也可以填写 HTTP/HTTPS/SOCKS5 代理后，点"测试连接"再试。',
        ])
    return "\n".join([
        "1. 先换手机热点或其它网络做对比测试。",
        "2. 用浏览器直接打开站点首页，确认是否为站点临时异常。",
        "3. 如果浏览器也不通，就先不要继续排查程序代码。",
    ])
