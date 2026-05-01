"""代理面板：手动代理开关、内置代理池开关/验证模式、连通性测试。

和其他 Tier 拆分不同，这里是**有状态的 UI 子系统**，不是纯工具。因此：
- 面板自己拥有所有代理相关的 tk 变量（BooleanVar / StringVar）
- 拥有 `_syncing` / `is_testing_connection` / `connection_test_stop_event` 等控制状态
- 按 `register_widgets(...)` 挂进来的按钮/输入框
- 通过 `ProxyPanelDeps` 注入 GUI 提供的回调：adapter/url getter、日志、状态栏、UI 线程调度

GUI 侧通过 1 行 delegate 把老接口转发过来，绑在按钮 `command=` 上的名字不用动。
"""
from __future__ import annotations

import threading
import time
import tkinter as tk
from dataclasses import dataclass
from tkinter import messagebox
from typing import Any, Callable, Optional

from downcomic import OperationCancelledError, proxy_pool
from site_adapters import resolve_adapter_from_url


def get_proxy_pool_status_text() -> str:
    """给状态栏用的单行代理池概况。读取 proxy_pool 全局的当前快照。"""
    enabled = bool(proxy_pool.enabled)
    proxy_count = len(getattr(proxy_pool, "proxies", []) or [])
    last_fetch_time = float(getattr(proxy_pool, "_last_fetch_time", 0) or 0)
    last_attempt_time = float(getattr(proxy_pool, "_last_fetch_attempt_time", 0) or 0)
    mode_label = "宽松" if getattr(proxy_pool, "get_validation_mode", lambda: "relaxed")() == "relaxed" else "严格"

    if not enabled:
        return f"代理池状态: 已关闭 · 当前为{mode_label}验证模式"

    if proxy_count > 0:
        suffix = ""
        if last_fetch_time > 0:
            suffix = f" · 最近更新 {time.strftime('%H:%M:%S', time.localtime(last_fetch_time))}"
        return f"代理池状态: 已启用（{mode_label}模式），当前缓存 {proxy_count} 个可用节点{suffix}"

    if last_attempt_time > 0:
        attempt_text = time.strftime("%H:%M:%S", time.localtime(last_attempt_time))
        return f"代理池状态: 已启用（{mode_label}模式），当前没有可用节点 · 最近尝试 {attempt_text}"

    return f"代理池状态: 已启用（{mode_label}模式），尚未加载节点，首次请求时会自动获取"


@dataclass
class ProxyPanelDeps:
    """ProxyPanel 需要问 GUI 的所有东西。"""
    get_adapter: Callable[[], Any]
    get_download_url: Callable[[], str]
    log: Callable[[str, str], None]
    status: Callable[[str], None]
    normalize: Callable[[str], str]
    run_on_ui_thread: Callable[..., None]
    get_connection_test_target: Callable[[Any], str]
    get_connection_route_label: Callable[[Any], str]
    get_connection_troubleshooting_text: Callable[[Any], str]


class ProxyPanel:
    def __init__(self, *, deps: ProxyPanelDeps):
        self._deps = deps

        # tk 变量由 ProxyPanel 拥有，GUI 通过 self.proxy_var = panel.pool_enabled_var 透出给现有 widget
        self.pool_enabled_var = tk.BooleanVar(value=bool(proxy_pool.enabled))
        self.pool_relaxed_var = tk.BooleanVar(
            value=bool(getattr(proxy_pool, "get_validation_mode", lambda: "relaxed")() == "relaxed")
        )
        self.status_var = tk.StringVar(value="")
        self.manual_enabled_var = tk.BooleanVar(value=False)
        self.manual_url_var = tk.StringVar()

        # 控制态
        self._syncing = False
        self.is_testing_connection = False
        self.connection_test_stop_event = threading.Event()

        # 通过 register_widgets 逐个挂进来
        self._widgets: dict = {}

    # --- widget 挂接 ---
    def register_widgets(self, **widgets):
        """GUI 创建完按钮/输入框后，把引用传进来。允许空值（hasattr 兜底）。"""
        self._widgets.update({k: v for k, v in widgets.items() if v is not None})

    def _widget(self, name):
        return self._widgets.get(name)

    # --- 刷新面板显示 ---
    def refresh(self):
        adapter = self._deps.get_adapter()
        supports_manual_proxy = bool(getattr(adapter, "supports_manual_proxy", lambda: False)())
        supports_proxy_pool = bool(getattr(adapter, "supports_proxy_pool", lambda: False)())
        proxy_pool_enabled = bool(proxy_pool.enabled)

        self._syncing = True
        try:
            self.pool_enabled_var.set(proxy_pool_enabled)
            self.pool_relaxed_var.set(
                bool(getattr(proxy_pool, "get_validation_mode", lambda: "relaxed")() == "relaxed")
            )
            if supports_manual_proxy:
                self.manual_enabled_var.set(adapter.has_manual_proxy())
                self.manual_url_var.set(adapter.get_manual_proxy_url())
            else:
                self.manual_enabled_var.set(False)
                self.manual_url_var.set("")
        finally:
            self._syncing = False

        toggle_btn = self._widget("toggle_btn")
        if toggle_btn is not None:
            toggle_btn.config(
                state=tk.NORMAL if supports_manual_proxy and not proxy_pool_enabled else tk.DISABLED
            )

        apply_btn = self._widget("apply_btn")
        if apply_btn is not None:
            apply_btn.config(
                state=tk.NORMAL if supports_manual_proxy and not proxy_pool_enabled and not self.is_testing_connection else tk.DISABLED
            )

        test_btn = self._widget("test_btn")
        if test_btn is not None:
            test_btn.config(
                state=tk.NORMAL if not self.is_testing_connection else tk.DISABLED,
                text="测试中..." if self.is_testing_connection else "测试连接",
            )

        test_stop_btn = self._widget("test_stop_btn")
        if test_stop_btn is not None:
            test_stop_btn.config(
                state=tk.NORMAL if self.is_testing_connection else tk.DISABLED
            )

        proxy_entry = self._widget("proxy_entry")
        if proxy_entry is not None:
            proxy_entry.config(
                state=tk.NORMAL if supports_manual_proxy and self.manual_enabled_var.get() and not proxy_pool_enabled else tk.DISABLED
            )

        pool_toggle_btn = self._widget("pool_toggle_btn")
        if pool_toggle_btn is not None:
            pool_toggle_btn.config(
                state=tk.NORMAL if supports_proxy_pool and not self.is_testing_connection else tk.DISABLED
            )

        pool_relaxed_btn = self._widget("pool_relaxed_btn")
        if pool_relaxed_btn is not None:
            pool_relaxed_btn.config(
                state=tk.NORMAL if supports_proxy_pool and not self.is_testing_connection else tk.DISABLED
            )

        self.status_var.set(get_proxy_pool_status_text())

    # --- 手动代理 ---
    def on_manual_toggle(self):
        if self._syncing:
            return

        enabled = bool(self.manual_enabled_var.get())
        proxy_entry = self._widget("proxy_entry")
        if proxy_entry is not None:
            proxy_entry.config(state=tk.NORMAL if enabled else tk.DISABLED)

        if enabled:
            self._deps.status("已启用手动代理输入，请点击\"应用代理\"或直接开始请求。")
            return

        adapter = self._deps.get_adapter()
        try:
            adapter.set_manual_proxy("")
            self._deps.log(f"已关闭 {adapter.display_name} 手动代理。", "info")
            self._deps.status(f"{adapter.display_name} 已关闭手动代理")
        except Exception as e:
            self._deps.log(f"关闭手动代理失败: {str(e)}", "warning")

    def apply_manual_settings(self, show_feedback: bool = True) -> bool:
        if self._syncing:
            return True

        adapter = self._deps.get_adapter()
        if bool(getattr(adapter, "supports_proxy_pool", lambda: False)()) and proxy_pool.enabled:
            return True

        supports_manual_proxy = bool(getattr(adapter, "supports_manual_proxy", lambda: False)())
        if not supports_manual_proxy:
            self.refresh()
            return True

        enabled = bool(self.manual_enabled_var.get())
        proxy_text = (self.manual_url_var.get() or "").strip()

        if not enabled:
            try:
                adapter.set_manual_proxy("")
                proxy_entry = self._widget("proxy_entry")
                if proxy_entry is not None:
                    proxy_entry.config(state=tk.DISABLED)
                if show_feedback:
                    self._deps.log(f"已关闭 {adapter.display_name} 手动代理。", "info")
                    self._deps.status(f"{adapter.display_name} 已关闭手动代理")
                return True
            except Exception as e:
                self._deps.log(f"关闭手动代理失败: {str(e)}", "warning")
                return False

        if not proxy_text:
            messagebox.showwarning("提示", "请输入代理地址，例如 127.0.0.1:7890 或 http://127.0.0.1:7890")
            return False

        try:
            adapter.set_manual_proxy(proxy_text)
            proxy_entry = self._widget("proxy_entry")
            if proxy_entry is not None:
                proxy_entry.config(state=tk.NORMAL)
            self.manual_url_var.set(adapter.get_manual_proxy_url())
            if show_feedback:
                self._deps.log(f"已为 {adapter.display_name} 应用手动代理: {adapter.get_manual_proxy_url()}", "info")
                self._deps.status(f"{adapter.display_name} 手动代理已应用")
            return True
        except Exception as e:
            self._deps.log(f"应用手动代理失败: {str(e)}", "warning")
            self._deps.run_on_ui_thread(messagebox.showwarning, "代理设置失败", str(e))
            return False

    # --- 代理池 ---
    def on_pool_toggle(self):
        if self._syncing:
            return

        enabled = bool(self.pool_enabled_var.get())
        proxy_pool.enabled = enabled
        if enabled:
            proxy_pool.clear_cached_proxies()
        self.refresh()
        if enabled:
            mode_label = "宽松" if self.pool_relaxed_var.get() else "严格"
            self._deps.log(
                f"已启用内置代理池（{mode_label}模式）。已清空旧缓存，后续会按当前验证模式重新筛选公开代理节点。",
                "warning",
            )
            self._deps.status("已启用内置代理池")
            return

        self._deps.log("已关闭内置代理池，后续将恢复使用手动代理、系统代理或直连。", "info")
        self._deps.status("已关闭内置代理池")

    def on_validation_mode_toggle(self):
        if self._syncing:
            return

        target_mode = "relaxed" if self.pool_relaxed_var.get() else "strict"
        proxy_pool.set_validation_mode(target_mode)
        self.refresh()
        if target_mode == "relaxed":
            self._deps.log(
                "已切换到代理池宽松模式：任一测试目标通过即可保留节点，成功率更高但误判也会更多。",
                "warning",
            )
            self._deps.status("代理池已切换为宽松模式")
            return

        self._deps.log(
            "已切换到代理池严格模式：所有 HTTPS 测试目标都需通过，筛选更严格。",
            "warning",
        )
        self._deps.status("代理池已切换为严格模式")

    # --- 连通性测试 ---
    def run_connection_probe(self, adapter, target_url: str, stop_event: Optional[threading.Event] = None):
        return adapter.probe_connection(target_url, stop_event=stop_event)

    def stop_test(self):
        if not self.is_testing_connection:
            return

        self.connection_test_stop_event.set()
        self._deps.log("正在停止连通性测试...", "warning")
        if proxy_pool.enabled and not proxy_pool.proxies:
            self._deps.log(
                "如果当前正在拉取或验证代理池节点，通常要等这轮网络请求超时或当前批次校验收尾后才会完全停止。",
                "warning",
            )
        self._deps.status("正在停止连通性测试...")
        self.refresh()

    def start_test(self):
        if self.is_testing_connection:
            return

        current_url = (self._deps.get_download_url() or "").strip()
        current_adapter = self._deps.get_adapter()
        adapter = resolve_adapter_from_url(current_url, fallback_key=current_adapter.key) if current_url else current_adapter
        if current_url and adapter.key != current_adapter.key:
            self._deps.log(
                f"⚠️ 当前输入框里的链接属于 {adapter.display_name}，本次将按链接所属站点测试；若要测试当前下拉站点，请先清空链接框。",
                "warning",
            )

        if not self.apply_manual_settings(show_feedback=False):
            return

        target_url = self._deps.get_connection_test_target(adapter)
        route_label = self._deps.get_connection_route_label(adapter)
        self.connection_test_stop_event.clear()
        self.is_testing_connection = True
        self.refresh()
        self._deps.status(f"正在测试 {adapter.display_name} 连通性...")
        self._deps.log(f"🌐 正在测试 {adapter.display_name} 连通性...", "info")
        self._deps.log(f"目标地址: {target_url}", "info")
        self._deps.log(f"当前请求方式: {route_label}", "info")
        if proxy_pool.enabled and not proxy_pool.proxies:
            self._deps.log(
                "🔄 代理池当前还没有可用节点，正在后台拉取并验证公开代理，首次可能需要一些时间。",
                "warning",
            )
            self._deps.status("正在加载代理池节点...")

        def worker():
            try:
                status_code, final_url = self.run_connection_probe(
                    adapter,
                    target_url,
                    stop_event=self.connection_test_stop_event,
                )
                if self.connection_test_stop_event.is_set():
                    raise OperationCancelledError("已停止连通性测试")
                if status_code >= 500:
                    self._deps.log(
                        f"⚠️ 已连到 {adapter.display_name}，但站点返回 HTTP {status_code}",
                        "warning",
                    )
                    self._deps.status(f"{adapter.display_name} 可连接，但站点返回异常")
                    self._deps.run_on_ui_thread(
                        messagebox.showwarning,
                        "测试结果",
                        f"{adapter.display_name} 已可达，但站点返回了 HTTP {status_code}。\n\n这更像是站点临时异常，不是本地网络完全不通。\n最终地址: {final_url}",
                    )
                    return

                self._deps.log(
                    f"✅ 连通性测试通过: {adapter.display_name} 可访问 (HTTP {status_code})",
                    "success",
                )
                self._deps.status(f"{adapter.display_name} 连通性正常")
                self._deps.run_on_ui_thread(
                    messagebox.showinfo,
                    "测试结果",
                    f"{adapter.display_name} 当前可通过\"{route_label}\"访问。\n\nHTTP 状态: {status_code}\n最终地址: {final_url}",
                )
            except OperationCancelledError:
                self._deps.log("🛑 已停止连通性测试", "warning")
                self._deps.status("已停止连通性测试")
            except Exception as e:
                raw_message = self._deps.normalize(str(e))
                troubleshooting = self._deps.get_connection_troubleshooting_text(adapter)
                self._deps.log(f"❌ 连通性测试失败: {raw_message or e}", "error")
                for line in troubleshooting.splitlines():
                    self._deps.log(line, "warning")
                self._deps.status(f"{adapter.display_name} 连通性测试失败")
                self._deps.run_on_ui_thread(
                    messagebox.showwarning,
                    "测试失败",
                    f"{adapter.display_name} 当前无法通过\"{route_label}\"访问。\n\n建议按这个顺序排查：\n{troubleshooting}\n\n错误详情:\n{raw_message or e}",
                )
            finally:
                self.connection_test_stop_event.clear()
                self.is_testing_connection = False
                self._deps.run_on_ui_thread(self.refresh)

        threading.Thread(target=worker, daemon=True).start()
