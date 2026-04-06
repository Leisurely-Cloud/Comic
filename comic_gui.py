import tkinter as tk
from tkinter import ttk, messagebox, scrolledtext
import threading
import queue
import os
import sys
import json
import pickle
import io
import re
import subprocess
import zipfile
import xml.etree.ElementTree as ET
import requests
from datetime import datetime
from urllib.parse import urljoin
from downcomic import (
    HomepageMangaCard, sanitize_filename, proxy_pool, print_lock, unwrap_cover_url
)
from site_adapters import (
    DEFAULT_SITE_KEY,
    MangaDetail,
    get_adapter,
    get_adapter_by_display_name,
    get_site_display_names,
    resolve_adapter_from_url,
)
from concurrent.futures import ThreadPoolExecutor, wait, FIRST_COMPLETED, as_completed
import time

try:
    from PIL import Image, ImageTk
except ImportError:
    Image = None
    ImageTk = None

class ComicDownloaderGUI:
    def __init__(self, root):
        self.root = root
        self.root.title("智能漫画下载器")
        self.root.geometry("1320x990")
        self.root.minsize(1080, 810)
        
        # 设置样式
        self.setup_style()
        
        # 下载队列
        self.download_queue = queue.Queue()
        self.is_downloading = False
        self.is_paused = False
        self.current_thread = None
        self.executor = None
        self._closing = False
        self._force_exit_scheduled = False
        self.original_stdout = sys.stdout
        self.original_stderr = sys.stderr
        self.download_state = {}  # 下载状态跟踪
        self.resume_data_file = "download_resume_data.json"  # 断点续传数据文件
        self.manga_detail_cache_file = "manga_detail_cache.json"
        self.stop_event = threading.Event()  # 停止事件
        self.pause_event = threading.Event()  # 暂停事件
        self.pause_event.set()  # 默认不暂停
        self.rank_cards = []
        self.current_homepage_cards = []
        self.cover_image = None
        self.current_cover_url = None
        self.current_detail_root_dir = ""
        self.current_detail_title = ""
        self.current_detail_url = ""
        self.current_detail_library_entry = None
        self.is_exporting_cbz = False
        self.is_checking_library_updates = False
        self.rank_detail_cache = {}
        self.current_detail_request_key = None
        self.current_download_url = ""
        self.active_download_url = ""
        self.active_download_root_dir = ""
        self.active_manga_title = ""
        self.active_download_metadata = None
        self.local_library_page_size = 50
        self.library_metadata_file_name = "元数据.json"
        self.download_site_key = DEFAULT_SITE_KEY
        self.current_adapter = get_adapter(DEFAULT_SITE_KEY)
        self.site_var = tk.StringVar(value=self.current_adapter.display_name)
        self.download_url_var = tk.StringVar()
        self.manual_proxy_enabled_var = tk.BooleanVar(value=False)
        self.manual_proxy_url_var = tk.StringVar()
        self.section_options = self.get_adapter_section_options(self.current_adapter)
        self.theme_options = self.get_adapter_theme_options(self.current_adapter)
        self.current_section_page = 1
        self.search_query_var = tk.StringVar()
        self.clear_download_url_on_next_refresh = False
        self.skip_next_ranking_selection_url_sync = False
        self.is_fetching_manga_detail = False
        self.is_testing_connection = False
        self._syncing_proxy_controls = False
        self.saved_manga_detail_cache = self.load_manga_detail_cache()
        self.log_queue = queue.Queue()
        self.ui_task_queue = queue.Queue()
        self.ui_thread_ident = threading.get_ident()
        self.ui_task_pump_job = None
        self.log_flush_job = None
        self.ranking_request_id = 0
        self.max_log_lines = 800
        self._pane_restore_job = None
        self._pane_restore_followup_job = None
        self._window_was_iconic = False
        self.saved_content_sash = None
        self.saved_ranking_sash = None
        
        # 创建界面
        self.create_widgets()
        self.root.after_idle(self.center_window)
        
        # 重定向打印输出到文本框
        self.redirect_output()
        self.root.protocol("WM_DELETE_WINDOW", self.on_window_close)
        self.root.bind("<Unmap>", self.on_window_unmap, add="+")
        self.root.bind("<Map>", self.on_window_map, add="+")
        self.root.bind("<Configure>", self.on_window_configure, add="+")
        self.schedule_ui_task_pump()
        self.schedule_log_flush()
        
        # 检查是否有可恢复的下载
        self.root.after(1000, self.check_resume_download_on_startup)
        self.root.after(300, self.refresh_rankings)
        self.root.after(250, self.configure_initial_pane_layout)
        self.root.after(900, self.configure_initial_pane_layout)

    def center_window(self):
        self.root.update_idletasks()
        width = self.root.winfo_width()
        height = self.root.winfo_height()
        if width <= 1 or height <= 1:
            width = self.root.winfo_reqwidth()
            height = self.root.winfo_reqheight()

        screen_width = self.root.winfo_screenwidth()
        screen_height = self.root.winfo_screenheight()
        x = max((screen_width - width) // 2, 0)
        y = max((screen_height - height) // 2, 0)
        self.root.geometry(f"{width}x{height}+{x}+{y}")

    def center_child_window(self, child, width=None, height=None):
        self.root.update_idletasks()
        child.update_idletasks()

        dialog_width = width or child.winfo_reqwidth()
        dialog_height = height or child.winfo_reqheight()

        root_x = self.root.winfo_rootx()
        root_y = self.root.winfo_rooty()
        root_width = self.root.winfo_width() or self.root.winfo_reqwidth()
        root_height = self.root.winfo_height() or self.root.winfo_reqheight()

        x = max(root_x + (root_width - dialog_width) // 2, 0)
        y = max(root_y + (root_height - dialog_height) // 2, 0)
        child.geometry(f"{dialog_width}x{dialog_height}+{x}+{y}")

    def clear_pending_ranking_selection_url_sync(self):
        self.skip_next_ranking_selection_url_sync = False

    def ask_resume_download_confirmation(self, resume_adapter, state):
        result = {"value": False}
        dialog = tk.Toplevel(self.root)
        dialog.title("发现未完成的下载")
        dialog.transient(self.root)
        dialog.resizable(False, False)
        dialog.protocol("WM_DELETE_WINDOW", lambda: on_close())
        dialog.columnconfigure(0, weight=1)
        dialog.rowconfigure(0, weight=1)

        container = ttk.Frame(dialog, padding=16)
        container.grid(row=0, column=0, sticky="nsew")
        container.columnconfigure(0, weight=1)

        message = (
            "发现未完成的下载任务:\n"
            f"站点: {resume_adapter.display_name}\n"
            f"漫画URL: {state['url']}\n"
            f"上次进度: 第{state['current_chapter_order']}章\n"
            f"总章节数: {state['total_chapters']}\n"
            f"保存时间: {state['timestamp']}\n\n"
            "是否恢复下载？"
        )

        ttk.Label(
            container,
            text=message,
            justify="left",
            anchor="w",
            wraplength=420,
        ).grid(row=0, column=0, sticky="w")

        button_row = ttk.Frame(container, style='Panel.TFrame')
        button_row.grid(row=1, column=0, pady=(16, 0))

        def close_with(value):
            result["value"] = value
            try:
                dialog.grab_release()
            except Exception:
                pass
            dialog.destroy()

        def on_close(event=None):
            close_with(False)

        def on_confirm(event=None):
            close_with(True)

        yes_btn = ttk.Button(button_row, text="是", command=on_confirm, width=12)
        yes_btn.pack(side=tk.LEFT, padx=(0, 8))

        no_btn = ttk.Button(button_row, text="否", command=on_close, width=12)
        no_btn.pack(side=tk.LEFT)

        dialog.bind("<Escape>", on_close)
        dialog.bind("<Return>", on_confirm)
        dialog.bind("<KP_Enter>", on_confirm)

        self.center_child_window(dialog, width=470, height=245)
        dialog.lift()
        dialog.grab_set()
        dialog.after_idle(dialog.focus_force)
        self.root.wait_window(dialog)
        return result["value"]

    def ask_archive_download_confirmation(self, manga_title, root_dir, completed_chapters, failed_chapters):
        result = {"value": False}
        dialog = tk.Toplevel(self.root)
        dialog.title("下载完成")
        dialog.transient(self.root)
        dialog.resizable(False, False)
        dialog.protocol("WM_DELETE_WINDOW", lambda: on_close())
        dialog.columnconfigure(0, weight=1)
        dialog.rowconfigure(0, weight=1)

        container = ttk.Frame(dialog, padding=16)
        container.grid(row=0, column=0, sticky="nsew")
        container.columnconfigure(0, weight=1)

        display_title = manga_title or (os.path.basename(root_dir.rstrip("\\/")) if root_dir else "当前漫画")
        message = (
            f"《{display_title}》下载任务已结束。\n"
            f"成功章节: {completed_chapters}\n"
            f"失败章节: {failed_chapters}\n"
            f"保存目录: {root_dir}\n\n"
            "是否将当前这部漫画打包成 ZIP 压缩包？"
        )

        ttk.Label(
            container,
            text=message,
            justify="left",
            anchor="w",
            wraplength=460,
        ).grid(row=0, column=0, sticky="w")

        button_row = ttk.Frame(container, style='Panel.TFrame')
        button_row.grid(row=1, column=0, pady=(16, 0))

        def close_with(value):
            result["value"] = value
            try:
                dialog.grab_release()
            except Exception:
                pass
            dialog.destroy()

        def on_close(event=None):
            close_with(False)

        def on_confirm(event=None):
            close_with(True)

        yes_btn = ttk.Button(button_row, text="打包", command=on_confirm, width=12)
        yes_btn.pack(side=tk.LEFT, padx=(0, 8))

        no_btn = ttk.Button(button_row, text="暂不", command=on_close, width=12)
        no_btn.pack(side=tk.LEFT)

        dialog.bind("<Escape>", on_close)
        dialog.bind("<Return>", on_confirm)
        dialog.bind("<KP_Enter>", on_confirm)

        self.center_child_window(dialog, width=520, height=255)
        dialog.lift()
        dialog.grab_set()
        dialog.after_idle(dialog.focus_force)
        self.root.wait_window(dialog)
        return result["value"]

    def build_unique_archive_path(self, root_dir):
        parent_dir = os.path.dirname(root_dir.rstrip("\\/"))
        base_name = os.path.basename(root_dir.rstrip("\\/")) or "漫画下载"
        archive_path = os.path.join(parent_dir, f"{base_name}.zip")
        suffix = 2
        while os.path.exists(archive_path):
            archive_path = os.path.join(parent_dir, f"{base_name}_{suffix}.zip")
            suffix += 1
        return archive_path

    def create_zip_archive_for_manga(self, root_dir):
        if not root_dir or not os.path.isdir(root_dir):
            raise FileNotFoundError("下载目录不存在，暂时无法创建压缩包。")

        archive_path = self.build_unique_archive_path(root_dir)
        parent_dir = os.path.dirname(root_dir.rstrip("\\/"))
        file_count = 0

        with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for current_dir, dir_names, file_names in os.walk(root_dir):
                dir_names[:] = [
                    dir_name for dir_name in sorted(dir_names)
                    if not self.is_temp_chapter_dir_name(dir_name)
                ]
                relative_dir = os.path.relpath(current_dir, parent_dir).replace("\\", "/")
                if not dir_names and not file_names:
                    archive.writestr(f"{relative_dir}/", "")
                for file_name in sorted(file_names):
                    file_path = os.path.join(current_dir, file_name)
                    archive_name = os.path.relpath(file_path, parent_dir).replace("\\", "/")
                    archive.write(file_path, archive_name)
                    file_count += 1

        return archive_path, file_count

    def build_cbz_export_dir(self, root_dir):
        resolved_root_dir = root_dir.rstrip("\\/")
        if not resolved_root_dir:
            raise FileNotFoundError("下载目录不存在，暂时无法导出 CBZ。")

        parent_dir = os.path.dirname(resolved_root_dir)
        base_name = os.path.basename(resolved_root_dir) or "漫画下载"
        export_dir = os.path.join(parent_dir, f"{base_name}_CBZ")
        os.makedirs(export_dir, exist_ok=True)
        return export_dir

    def list_exportable_image_files(self, chapter_dir):
        image_files = []
        try:
            for entry in os.scandir(chapter_dir):
                if entry.is_file() and entry.name.lower().endswith((".jpg", ".jpeg", ".png", ".webp")):
                    image_files.append(entry.path)
        except Exception:
            return []
        image_files.sort(key=lambda item: os.path.basename(item).lower())
        return image_files

    def build_cbz_comicinfo_xml(self, manga_title, chapter_title, chapter_number, chapter_count, page_count, manga_url=""):
        root = ET.Element("ComicInfo")

        def add_text_node(tag_name, value):
            if value is None:
                return
            text = str(value).strip()
            if not text:
                return
            ET.SubElement(root, tag_name).text = text

        add_text_node("Series", manga_title or "漫画")
        add_text_node("Title", chapter_title or manga_title or "章节")
        add_text_node("Number", chapter_number)
        add_text_node("Count", chapter_count)
        add_text_node("PageCount", page_count)
        add_text_node("Manga", "YesAndRightToLeft")
        add_text_node("Web", manga_url)

        return ET.tostring(root, encoding="utf-8", xml_declaration=True)

    def create_cbz_archive_for_chapter(self, export_dir, chapter_dir_name, chapter_dir_path, manga_title, manga_url, chapter_number, chapter_count):
        image_files = self.list_exportable_image_files(chapter_dir_path)
        if not image_files:
            return "", 0

        chapter_title = chapter_dir_name.split("_", 1)[1] if "_" in chapter_dir_name else chapter_dir_name
        archive_path = os.path.join(export_dir, f"{chapter_dir_name}.cbz")

        with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for image_path in image_files:
                archive.write(image_path, os.path.basename(image_path))
            archive.writestr(
                "ComicInfo.xml",
                self.build_cbz_comicinfo_xml(
                    manga_title=manga_title,
                    chapter_title=chapter_title,
                    chapter_number=chapter_number,
                    chapter_count=chapter_count,
                    page_count=len(image_files),
                    manga_url=manga_url,
                ),
            )

        return archive_path, len(image_files)

    def export_manga_to_cbz(self, root_dir, manga_title, manga_url=""):
        resolved_root_dir = (root_dir or "").strip()
        if not resolved_root_dir or not os.path.isdir(resolved_root_dir):
            raise FileNotFoundError("当前没有可导出的本地目录。")

        chapter_entries = []
        try:
            for entry in os.scandir(resolved_root_dir):
                if entry.is_dir() and self.is_final_chapter_dir_name(entry.name):
                    chapter_entries.append((entry.name, entry.path))
        except Exception as exc:
            raise RuntimeError(f"读取章节目录失败: {str(exc)}")

        chapter_entries.sort(key=lambda item: item[0])
        if not chapter_entries:
            raise RuntimeError("当前漫画目录里没有可导出的已完成章节。")

        export_dir = self.build_cbz_export_dir(resolved_root_dir)
        exported_archives = []
        skipped_chapters = []
        total_chapters = len(chapter_entries)

        for chapter_index, (chapter_dir_name, chapter_dir_path) in enumerate(chapter_entries, 1):
            archive_path, image_count = self.create_cbz_archive_for_chapter(
                export_dir=export_dir,
                chapter_dir_name=chapter_dir_name,
                chapter_dir_path=chapter_dir_path,
                manga_title=manga_title,
                manga_url=manga_url,
                chapter_number=chapter_index,
                chapter_count=total_chapters,
            )
            if archive_path:
                exported_archives.append((archive_path, image_count))
            else:
                skipped_chapters.append(chapter_dir_name)

        if not exported_archives:
            raise RuntimeError("没有找到可写入 CBZ 的图片文件。")

        return export_dir, exported_archives, skipped_chapters

    def offer_archive_after_download(self, download_summary):
        if not download_summary or self._closing:
            return

        root_dir = (download_summary.get("root_dir") or "").strip()
        manga_title = (download_summary.get("manga_title") or "").strip()
        completed_chapters = int(download_summary.get("completed_chapters") or 0)
        failed_chapters = int(download_summary.get("failed_chapters") or 0)
        should_offer_archive = bool(download_summary.get("should_offer_archive"))

        if not should_offer_archive or not root_dir or not os.path.isdir(root_dir):
            return

        if not self.ask_archive_download_confirmation(manga_title, root_dir, completed_chapters, failed_chapters):
            return

        self.log_message(f"📦 正在打包漫画压缩包: {root_dir}")
        self.set_status("正在创建压缩包...")

        def worker():
            try:
                archive_path, file_count = self.create_zip_archive_for_manga(root_dir)
                self.log_message(f"✅ 压缩包已创建: {archive_path}")
                self.log_message(f"📦 共写入 {file_count} 个文件")
                self.set_status("压缩包已创建")
                self.run_on_ui_thread(
                    messagebox.showinfo,
                    "打包完成",
                    f"已创建压缩包：\n{archive_path}",
                )
            except Exception as exc:
                self.log_message(f"❌ 创建压缩包失败: {str(exc)}", "error")
                self.set_status("创建压缩包失败")
                self.run_on_ui_thread(
                    messagebox.showwarning,
                    "打包失败",
                    str(exc),
                )

        threading.Thread(target=worker, daemon=True).start()

    def setup_style(self):
        self.style = ttk.Style()
        self.style.theme_use('clam')
        
        # 配置现代化颜色主题
        self.colors = {
            'bg': '#eef3f8',
            'surface': '#ffffff',
            'surface_alt': '#f8fbff',
            'fg': '#223042',
            'muted': '#6b7a90',
            'accent': '#1f7ae0',
            'accent_soft': '#dcecff',
            'success': '#1f9d63',
            'warning': '#e6a100',
            'danger': '#dd5a4f',
            'secondary': '#95a5a6',
            'dark': '#34495e',
            'light': '#ffffff',
            'border': '#d7e0ea'
        }
        
        self.root.configure(bg=self.colors['bg'])
        
        # 配置ttk样式
        self.style.configure('TFrame', background=self.colors['bg'])
        self.style.configure('TLabel', background=self.colors['bg'], foreground=self.colors['fg'])
        self.style.configure('TLabelFrame', background=self.colors['surface'], foreground=self.colors['dark'])
        self.style.configure('Panel.TFrame', background=self.colors['surface'])
        self.style.configure('Surface.TFrame', background=self.colors['surface_alt'])
        self.style.configure('Content.TPanedwindow', background=self.colors['surface'])
        self.style.configure('Inner.TPanedwindow', background=self.colors['surface'])
        self.style.configure('Title.TLabel', background=self.colors['bg'], foreground=self.colors['fg'],
                             font=('Microsoft YaHei UI', 18, 'bold'))
        self.style.configure('Subtitle.TLabel', background=self.colors['bg'], foreground=self.colors['muted'],
                             font=('Microsoft YaHei UI', 10))
        self.style.configure('Hint.TLabel', background=self.colors['surface'], foreground=self.colors['muted'],
                             font=('Microsoft YaHei UI', 9))
        self.style.configure('Section.TLabelframe', background=self.colors['surface'],
                             borderwidth=1, relief='solid')
        self.style.configure('Section.TLabelframe.Label', background=self.colors['surface'],
                             foreground=self.colors['fg'], font=('Microsoft YaHei UI', 10, 'bold'))
        self.style.configure('Info.TLabel', background=self.colors['surface'], foreground=self.colors['muted'],
                             font=('Microsoft YaHei UI', 9))
        self.style.configure('Footer.TLabel', background=self.colors['surface'], foreground=self.colors['fg'],
                             font=('Microsoft YaHei UI', 9))
        button_font = ('Microsoft YaHei UI', 10, 'bold')

        self.style.configure(
            'TButton',
            background=self.colors['bg'],
            foreground=self.colors['fg'],
            font=button_font,
            padding=(14, 8)
        )
        self.style.map(
            'TButton',
            background=[('active', '#e5edf7'), ('disabled', '#eef3f8')],
            foreground=[('active', self.colors['fg']), ('disabled', '#7f8c8d')]
        )
        self.style.configure('Accent.TButton', 
                       background=self.colors['accent'], 
                       foreground=self.colors['light'],
                       font=button_font,
                       padding=(14, 8),
                       borderwidth=1,
                       focusthickness=1,
                       focuscolor=self.colors['accent'])
        self.style.map('Accent.TButton',
                 background=[('active', '#1767bf'), ('disabled', '#bdc3c7')],
                 foreground=[('active', self.colors['light']), ('disabled', '#6c757d')])
        self.style.configure('TEntry', fieldbackground='white', foreground=self.colors['fg'],
                             bordercolor=self.colors['border'], lightcolor=self.colors['accent_soft'])
        self.style.configure('TCheckbutton', background=self.colors['bg'], foreground=self.colors['fg'])
        self.style.configure('TSpinbox', fieldbackground='white', foreground=self.colors['fg'])
        self.style.configure(
            'Ranking.Treeview',
            background=self.colors['surface'],
            fieldbackground=self.colors['surface'],
            foreground=self.colors['fg'],
            bordercolor=self.colors['border'],
            lightcolor=self.colors['surface'],
            darkcolor=self.colors['surface'],
            rowheight=28,
        )
        self.style.map(
            'Ranking.Treeview',
            background=[('selected', self.colors['accent_soft'])],
            foreground=[('selected', self.colors['fg'])]
        )
        self.style.configure(
            'Ranking.Treeview.Heading',
            background='#eef4fb',
            foreground=self.colors['fg'],
            relief='flat',
            padding=(8, 6),
            font=('Microsoft YaHei UI', 9, 'bold')
        )
        self.style.map(
            'Ranking.Treeview.Heading',
            background=[('active', '#e3edf8')]
        )
        self.style.configure('Download.Horizontal.TProgressbar',
                       background=self.colors['accent'],
                       troughcolor='#ecf0f1',
                       borderwidth=0,
                       lightcolor=self.colors['accent'],
                       darkcolor=self.colors['accent'])
        self.style.configure('Success.Horizontal.TProgressbar',
                       background=self.colors['success'],
                       troughcolor='#ecf0f1',
                       borderwidth=0,
                       lightcolor=self.colors['success'],
                       darkcolor=self.colors['success'])
        self.style.configure('Warning.Horizontal.TProgressbar',
                       background=self.colors['warning'],
                       troughcolor='#ecf0f1',
                       borderwidth=0,
                       lightcolor=self.colors['warning'],
                       darkcolor=self.colors['warning'])
        self.style.configure('Danger.Horizontal.TProgressbar',
                       background=self.colors['danger'],
                       troughcolor='#ecf0f1',
                       borderwidth=0,
                       lightcolor=self.colors['danger'],
                       darkcolor=self.colors['danger'])
        
        # 添加更多按钮样式
        self.style.configure('Success.TButton', 
                       background=self.colors['success'], 
                       foreground=self.colors['light'],
                       font=button_font,
                       padding=(14, 8))
        self.style.map('Success.TButton',
                 background=[('active', '#18854a'), ('disabled', '#bdc3c7')],
                 foreground=[('active', self.colors['light']), ('disabled', '#6c757d')])
        
        self.style.configure('Warning.TButton', 
                       background=self.colors['warning'], 
                       foreground=self.colors['light'],
                       font=button_font,
                       padding=(14, 8))
        self.style.map('Warning.TButton',
                 background=[('active', '#b9770e'), ('disabled', '#bdc3c7')],
                 foreground=[('active', self.colors['light']), ('disabled', '#6c757d')])
        
        self.style.configure('Danger.TButton', 
                       background=self.colors['danger'], 
                       foreground=self.colors['light'],
                       font=button_font,
                       padding=(14, 8))
        self.style.map('Danger.TButton',
                 background=[('active', '#a93226'), ('disabled', '#bdc3c7')],
                 foreground=[('active', self.colors['light']), ('disabled', '#6c757d')])
        
    def create_widgets(self):
        # 主框架
        main_frame = ttk.Frame(self.root, padding="10")
        main_frame.grid(row=0, column=0, sticky="nsew")
        
        # 配置网格权重
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(0, weight=1)
        main_frame.columnconfigure(0, weight=1)
        main_frame.rowconfigure(1, weight=1)
        
        action_panel = ttk.LabelFrame(main_frame, text="下载操作", padding="8", style='Section.TLabelframe')
        action_panel.grid(row=0, column=0, sticky="ew", pady=(0, 8))
        action_panel.columnconfigure(0, weight=1)
        action_panel.columnconfigure(1, weight=1)

        button_frame = ttk.Frame(action_panel, style='Panel.TFrame')
        button_frame.grid(row=0, column=0, sticky="w", padx=(0, 12))
        
        self.download_btn = ttk.Button(button_frame, text="开始下载", 
                                      command=self.start_download, 
                                      style='Success.TButton')
        self.download_btn.pack(side=tk.LEFT, padx=(0, 10))
        
        self.pause_btn = ttk.Button(button_frame, text="暂停", 
                                   command=self.pause_download, 
                                   state=tk.DISABLED,
                                   style='Warning.TButton')
        self.pause_btn.pack(side=tk.LEFT, padx=(0, 10))
        
        self.resume_btn = ttk.Button(button_frame, text="继续", 
                                    command=self.resume_download, 
                                    state=tk.DISABLED,
                                    style='Success.TButton')
        self.resume_btn.pack(side=tk.LEFT, padx=(0, 10))
        
        self.stop_btn = ttk.Button(button_frame, text="停止下载", 
                                  command=self.stop_download, 
                                  state=tk.DISABLED,
                                  style='Danger.TButton')
        self.stop_btn.pack(side=tk.LEFT, padx=(0, 10))
        
        self.clear_btn = ttk.Button(button_frame, text="清空日志", 
                                   command=self.clear_log,
                                   style='Accent.TButton')
        self.clear_btn.pack(side=tk.LEFT)

        settings_frame = ttk.Frame(action_panel, style='Panel.TFrame')
        settings_frame.grid(row=0, column=1, sticky="ew")
        for col in range(5):
            settings_frame.columnconfigure(col, weight=0)
        settings_frame.columnconfigure(3, weight=1)
        
        # 并发设置
        ttk.Label(settings_frame, text="章节并发数:").grid(row=0, column=0, sticky=tk.W, padx=(0, 10))
        self.concurrent_var = tk.IntVar(value=5)
        concurrent_spin = ttk.Spinbox(settings_frame, from_=1, to=10, 
                                     textvariable=self.concurrent_var, width=7)
        concurrent_spin.grid(row=0, column=1, padx=(0, 26), sticky="w", ipady=2)
        
        ttk.Label(settings_frame, text="图片并发数:").grid(row=0, column=2, sticky=tk.W, padx=(0, 10))
        self.image_concurrent_var = tk.IntVar(value=5)
        image_concurrent_spin = ttk.Spinbox(settings_frame, from_=1, to=10, 
                                           textvariable=self.image_concurrent_var, width=7)
        image_concurrent_spin.grid(row=0, column=3, padx=(0, 26), sticky="w", ipady=2)

        ttk.Label(settings_frame, text="站点:").grid(row=1, column=0, sticky=tk.W, padx=(0, 10), pady=(10, 0))
        self.site_combo = ttk.Combobox(
            settings_frame,
            width=12,
            state="readonly",
            textvariable=self.site_var,
            values=get_site_display_names()
        )
        self.site_combo.grid(row=1, column=1, padx=(0, 26), pady=(10, 0), sticky="w")
        self.site_combo.bind("<<ComboboxSelected>>", self.on_site_change)

        ttk.Label(settings_frame, text="漫画链接:").grid(row=1, column=2, sticky=tk.W, padx=(0, 10), pady=(10, 0))
        self.download_url_entry = ttk.Entry(
            settings_frame,
            textvariable=self.download_url_var,
            font=('Microsoft YaHei UI', 10)
        )
        self.download_url_entry.grid(row=1, column=3, padx=(0, 26), pady=(10, 0), sticky="ew", ipady=3)
        self.download_url_entry.bind("<Return>", self.fetch_manga_detail)

        self.fetch_info_btn = ttk.Button(
            settings_frame,
            text="获取信息",
            command=self.fetch_manga_detail,
            style='Accent.TButton'
        )
        self.fetch_info_btn.grid(row=1, column=4, pady=(10, 0), sticky="w")

        ttk.Label(settings_frame, text="代理:").grid(row=2, column=0, sticky=tk.W, padx=(0, 10), pady=(10, 0))
        self.proxy_toggle_btn = ttk.Checkbutton(
            settings_frame,
            text="启用手动代理",
            variable=self.manual_proxy_enabled_var,
            command=self.on_proxy_toggle,
        )
        self.proxy_toggle_btn.grid(row=2, column=1, padx=(0, 16), pady=(10, 0), sticky="w")

        self.proxy_entry = ttk.Entry(
            settings_frame,
            textvariable=self.manual_proxy_url_var,
            font=('Microsoft YaHei UI', 10)
        )
        self.proxy_entry.grid(row=2, column=2, columnspan=2, padx=(0, 10), pady=(10, 0), sticky="ew", ipady=3)
        self.proxy_entry.bind("<Return>", self.apply_manual_proxy_settings)

        proxy_action_frame = ttk.Frame(settings_frame, style='Panel.TFrame')
        proxy_action_frame.grid(row=2, column=4, pady=(10, 0), sticky="w")

        self.proxy_apply_btn = ttk.Button(
            proxy_action_frame,
            text="应用代理",
            command=self.apply_manual_proxy_settings
        )
        self.proxy_apply_btn.pack(side=tk.LEFT, padx=(0, 8))

        self.proxy_test_btn = ttk.Button(
            proxy_action_frame,
            text="测试连接",
            command=self.test_site_connection
        )
        self.proxy_test_btn.pack(side=tk.LEFT)

        # 这些选项不再显示在界面中，保留默认行为即可
        self.proxy_var = tk.BooleanVar(value=False)
        self.start_var = tk.IntVar(value=1)
        self.refresh_proxy_controls()

        content_pane = ttk.Panedwindow(main_frame, orient=tk.HORIZONTAL, style='Content.TPanedwindow')
        content_pane.grid(row=1, column=0, sticky="nsew", pady=(0, 8))

        ranking_frame = ttk.LabelFrame(content_pane, text="首页发现", padding="10", style='Section.TLabelframe')
        ranking_frame.columnconfigure(0, weight=1)
        ranking_frame.rowconfigure(1, weight=1)

        ranking_header = ttk.Frame(ranking_frame, style='Panel.TFrame')
        ranking_header.grid(row=0, column=0, sticky="ew", pady=(0, 8))
        ranking_header.columnconfigure(0, weight=1)

        search_frame = ttk.Frame(ranking_header, style='Panel.TFrame')
        search_frame.grid(row=0, column=0, sticky="ew", pady=(0, 6))
        search_frame.columnconfigure(1, weight=1)

        ttk.Label(search_frame, text="搜索:", style='Info.TLabel').grid(row=0, column=0, sticky="w", padx=(0, 8))
        self.search_entry = ttk.Entry(search_frame, textvariable=self.search_query_var, font=('Microsoft YaHei UI', 10))
        self.search_entry.grid(row=0, column=1, sticky="ew", padx=(0, 10), ipady=3)
        self.search_entry.bind("<Return>", self.on_search_enter)

        self.search_btn = ttk.Button(
            search_frame,
            text="搜索漫画",
            command=self.search_manga,
            style='Accent.TButton'
        )
        self.search_btn.grid(row=0, column=2, sticky="w", padx=(0, 8))

        self.clear_search_btn = ttk.Button(
            search_frame,
            text="清空搜索",
            command=self.clear_search
        )
        self.clear_search_btn.grid(row=0, column=3, sticky="w")

        ranking_action_frame = ttk.Frame(ranking_header, style='Panel.TFrame')
        ranking_action_frame.grid(row=1, column=0, sticky="w")

        ttk.Label(ranking_action_frame, text="分区:", style='Info.TLabel').pack(side=tk.LEFT, padx=(0, 6))
        self.homepage_section_var = tk.StringVar(value="人气排行")
        self.homepage_section_combo = ttk.Combobox(
            ranking_action_frame,
            width=10,
            state="readonly",
            textvariable=self.homepage_section_var,
            values=list(self.section_options.keys())
        )
        self.homepage_section_combo.pack(side=tk.LEFT, padx=(0, 10))
        self.homepage_section_combo.bind("<<ComboboxSelected>>", self.on_homepage_section_change)

        ttk.Label(ranking_action_frame, text="题材:", style='Info.TLabel').pack(side=tk.LEFT, padx=(0, 6))
        self.homepage_theme_var = tk.StringVar(value="全部题材")
        self.homepage_theme_combo = ttk.Combobox(
            ranking_action_frame,
            width=10,
            state="readonly",
            textvariable=self.homepage_theme_var,
            values=list(self.theme_options.keys()) or ["全部题材"]
        )
        self.homepage_theme_combo.pack(side=tk.LEFT, padx=(0, 10))
        self.homepage_theme_combo.bind("<<ComboboxSelected>>", self.on_homepage_theme_change)

        self.prev_page_btn = ttk.Button(
            ranking_action_frame,
            text="上一页",
            command=self.load_previous_section_page
        )
        self.prev_page_btn.pack(side=tk.LEFT, padx=(0, 8))

        self.section_page_var = tk.StringVar(value="第 1 页")
        ttk.Label(
            ranking_action_frame,
            textvariable=self.section_page_var,
            style='Info.TLabel'
        ).pack(side=tk.LEFT, padx=(0, 8))

        self.next_page_btn = ttk.Button(
            ranking_action_frame,
            text="下一页",
            command=self.load_next_section_page
        )
        self.next_page_btn.pack(side=tk.LEFT)

        ranking_button_frame = ttk.Frame(ranking_header, style='Panel.TFrame')
        ranking_button_frame.grid(row=2, column=0, sticky="w", pady=(6, 0))

        self.refresh_rank_btn = ttk.Button(
            ranking_button_frame,
            text="刷新列表",
            command=self.refresh_rankings,
            style='Accent.TButton'
        )
        self.refresh_rank_btn.pack(side=tk.LEFT, padx=(0, 10))

        self.download_rank_btn = ttk.Button(
            ranking_button_frame,
            text="下载选中漫画",
            command=self.download_selected_ranking,
            style='Success.TButton'
        )
        self.download_rank_btn.pack(side=tk.LEFT)

        self.check_updates_btn = ttk.Button(
            ranking_button_frame,
            text="检查更新",
            command=self.check_local_library_updates,
            style='Accent.TButton',
            state=tk.DISABLED,
        )
        self.check_updates_btn.pack(side=tk.LEFT, padx=(10, 0))

        ranking_pane = ttk.Panedwindow(ranking_frame, orient=tk.HORIZONTAL, style='Inner.TPanedwindow')
        ranking_pane.grid(row=1, column=0, sticky="nsew")

        ranking_list = ttk.Frame(ranking_pane, style='Panel.TFrame')
        ranking_list.columnconfigure(0, weight=1)
        ranking_list.rowconfigure(0, weight=1)

        columns = ("rank", "title")
        self.rank_tree = ttk.Treeview(
            ranking_list,
            columns=columns,
            show="headings",
            height=18,
            style='Ranking.Treeview'
        )
        self.rank_tree.grid(row=0, column=0, sticky="nsew")
        self.rank_tree.heading("rank", text="排名")
        self.rank_tree.heading("title", text="漫画名称")
        self.rank_tree.column("rank", width=60, anchor="center", stretch=False)
        self.rank_tree.column("title", width=420, anchor="w")
        self.rank_tree.bind("<<TreeviewSelect>>", self.on_ranking_select)
        self.rank_tree.bind("<Double-1>", self.on_ranking_double_click)

        ranking_scroll = ttk.Scrollbar(ranking_list, orient="vertical", command=self.rank_tree.yview)
        ranking_scroll.grid(row=0, column=1, sticky="ns")
        self.rank_tree.configure(yscrollcommand=ranking_scroll.set)

        ranking_detail = ttk.Frame(ranking_pane, style='Surface.TFrame', padding="8")
        ranking_detail.columnconfigure(0, weight=1)
        ranking_detail.rowconfigure(0, weight=0)

        self.cover_preview = tk.Label(
            ranking_detail,
            text="封面预览",
            bg=self.colors['accent_soft'],
            fg=self.colors['accent'],
            font=('Microsoft YaHei UI', 11, 'bold'),
            relief=tk.FLAT,
            bd=0,
            highlightthickness=0,
            anchor="center",
            justify="center",
            padx=8,
            pady=8
        )
        self.cover_preview.grid(row=0, column=0, sticky="ew")

        self.detail_title_var = tk.StringVar(value="请选择一部漫画")
        ttk.Label(
            ranking_detail,
            textvariable=self.detail_title_var,
            style='Section.TLabelframe.Label',
            wraplength=300,
            anchor="center",
            justify="center"
        ).grid(row=1, column=0, sticky="ew", pady=(10, 6))

        self.detail_section_var = tk.StringVar(value="分区: -")
        ttk.Label(
            ranking_detail,
            textvariable=self.detail_section_var,
            style='Info.TLabel',
            wraplength=300,
            anchor="center",
            justify="center"
        ).grid(row=2, column=0, sticky="ew")

        self.detail_latest_var = tk.StringVar(value="最近章节: -")
        ttk.Label(
            ranking_detail,
            textvariable=self.detail_latest_var,
            style='Info.TLabel',
            wraplength=300,
            anchor="center",
            justify="center"
        ).grid(row=3, column=0, sticky="ew", pady=(4, 0))

        self.detail_update_var = tk.StringVar(value="更新时间: -")
        ttk.Label(
            ranking_detail,
            textvariable=self.detail_update_var,
            style='Info.TLabel',
            wraplength=300,
            anchor="center",
            justify="center"
        ).grid(row=4, column=0, sticky="ew", pady=(4, 0))

        self.detail_cover_var = tk.StringVar(value="")
        ttk.Label(
            ranking_detail,
            textvariable=self.detail_cover_var,
            style='Hint.TLabel',
            wraplength=280,
            anchor="center",
            justify="center"
        ).grid(row=5, column=0, sticky="ew", pady=(4, 0))

        self.detail_local_status_var = tk.StringVar(value="本地状态: 未检测")
        ttk.Label(
            ranking_detail,
            textvariable=self.detail_local_status_var,
            style='Info.TLabel',
            wraplength=300,
            anchor="center",
            justify="center"
        ).grid(row=6, column=0, sticky="ew", pady=(8, 0))

        self.detail_local_path_var = tk.StringVar(value="")
        ttk.Label(
            ranking_detail,
            textvariable=self.detail_local_path_var,
            style='Hint.TLabel',
            wraplength=280,
            anchor="w",
            justify="left"
        ).grid(row=7, column=0, sticky="ew", pady=(4, 0))

        self.open_local_dir_btn = ttk.Button(
            ranking_detail,
            text="打开本地目录",
            command=self.open_current_detail_root_dir,
            style='Accent.TButton',
            state=tk.DISABLED,
            width=16,
        )
        self.open_local_dir_btn.grid(row=8, column=0, pady=(10, 0))

        self.export_cbz_btn = ttk.Button(
            ranking_detail,
            text="导出 CBZ",
            command=self.export_current_detail_to_cbz,
            style='Success.TButton',
            state=tk.DISABLED,
            width=16,
        )
        self.export_cbz_btn.grid(row=9, column=0, pady=(8, 0))

        ranking_pane.add(ranking_list, weight=5)
        ranking_pane.add(ranking_detail, weight=2)
        self.ranking_pane = ranking_pane
        self.ranking_list_panel = ranking_list
        
        # 日志区域
        log_frame = ttk.LabelFrame(content_pane, text="下载日志", padding="10", style='Section.TLabelframe')
        log_frame.columnconfigure(0, weight=1)
        log_frame.rowconfigure(0, weight=1)
        
        self.log_text = scrolledtext.ScrolledText(
            log_frame,
            height=18,
            width=80,
            wrap=tk.NONE,
            font=('Consolas', 10),
            bg='#0f172a',
            fg='#e2e8f0',
            insertbackground='#e2e8f0',
            selectbackground='#334155',
            relief=tk.FLAT,
            padx=10,
            pady=8,
            spacing1=2,
            spacing3=3
        )
        self.log_text.grid(row=0, column=0, sticky="nsew")
        self.log_x_scroll = ttk.Scrollbar(log_frame, orient="horizontal", command=self.log_text.xview)
        self.log_x_scroll.grid(row=1, column=0, sticky="ew", pady=(6, 0))
        self.log_text.configure(xscrollcommand=self.log_x_scroll.set)
        self.configure_log_tags()

        content_pane.add(ranking_frame, weight=5)
        content_pane.add(log_frame, weight=4)
        self.content_pane = content_pane
        self.progress_text_var = tk.StringVar(value="等待开始")
        self.status_text_var = tk.StringVar(value="就绪")

        self.refresh_site_controls(show_log=False)
        
        footer_frame = ttk.Frame(main_frame, style='Panel.TFrame')
        footer_frame.grid(row=2, column=0, sticky="ew")
        footer_frame.columnconfigure(0, weight=1)

        footer_header = ttk.Frame(footer_frame, style='Panel.TFrame')
        footer_header.grid(row=0, column=0, sticky="ew", pady=(0, 6))
        footer_header.columnconfigure(1, weight=1)

        ttk.Label(footer_header, text="总体进度", style='Footer.TLabel').grid(row=0, column=0, sticky="w")
        ttk.Label(footer_header, textvariable=self.status_text_var, style='Info.TLabel').grid(row=0, column=1, sticky="w", padx=(12, 12))
        ttk.Label(footer_header, textvariable=self.progress_text_var, style='Info.TLabel').grid(row=0, column=2, sticky="e")

        # 进度条
        self.progress_var = tk.DoubleVar()
        self.progress_bar = ttk.Progressbar(
            footer_frame,
            variable=self.progress_var,
            maximum=100,
            mode='determinate',
            style='Download.Horizontal.TProgressbar'
        )
        self.progress_bar.grid(row=1, column=0, sticky="ew", pady=(0, 6), ipady=2)
        
    def configure_log_tags(self):
        """配置日志颜色标签。"""
        self.log_text.tag_configure('info', foreground='#e2e8f0')
        self.log_text.tag_configure('success', foreground='#4ade80')
        self.log_text.tag_configure('warning', foreground='#fbbf24')
        self.log_text.tag_configure('error', foreground='#f87171')
        self.log_text.tag_configure('debug', foreground='#93c5fd')
        self.log_text.tag_configure('path', foreground='#c4b5fd')
        self.log_text.tag_configure('status', foreground='#67e8f9')
        self.log_text.tag_configure('muted', foreground='#94a3b8')

    def configure_initial_pane_layout(self):
        """设置首页发现/日志区与列表/详情区的初始分隔比例。"""
        if self._closing or not hasattr(self, "content_pane"):
            return
        try:
            total_width = self.content_pane.winfo_width()
            if total_width > 0:
                discovery_width = int(total_width * 0.54)
                discovery_width = max(720, discovery_width)
                discovery_width = min(discovery_width, max(total_width - 430, 720))
                self.content_pane.sashpos(0, discovery_width)
                self.saved_content_sash = discovery_width
        except Exception:
            pass
        try:
            if hasattr(self, "ranking_pane"):
                total_width = self.ranking_pane.winfo_width()
                if total_width > 0:
                    list_width = int(total_width * 0.47)
                    list_width = max(330, list_width)
                    list_width = min(list_width, max(total_width - 290, 330))
                    self.ranking_pane.sashpos(0, list_width)
                    self.saved_ranking_sash = list_width
        except Exception:
            pass

    def clamp_pane_sash(self, value, total_width, min_width, trailing_min_width):
        if total_width <= 0:
            return int(value)
        max_width = max(total_width - trailing_min_width, min_width)
        return min(max(int(value), min_width), max_width)

    def capture_current_pane_layout(self):
        if self._closing:
            return
        try:
            if hasattr(self, "content_pane") and self.content_pane.winfo_exists():
                self.saved_content_sash = self.content_pane.sashpos(0)
        except Exception:
            pass
        try:
            if hasattr(self, "ranking_pane") and self.ranking_pane.winfo_exists():
                self.saved_ranking_sash = self.ranking_pane.sashpos(0)
        except Exception:
            pass

    def restore_saved_pane_layout(self):
        if self._closing or not hasattr(self, "content_pane"):
            return
        try:
            if self.root.state() == "iconic":
                return
        except Exception:
            return

        restored = False
        try:
            total_width = self.content_pane.winfo_width()
            if total_width > 0 and self.saved_content_sash is not None:
                discovery_width = self.clamp_pane_sash(self.saved_content_sash, total_width, 720, 430)
                self.content_pane.sashpos(0, discovery_width)
                self.saved_content_sash = discovery_width
                restored = True
        except Exception:
            pass

        try:
            total_width = self.ranking_pane.winfo_width()
            if total_width > 0 and self.saved_ranking_sash is not None:
                list_width = self.clamp_pane_sash(self.saved_ranking_sash, total_width, 330, 290)
                self.ranking_pane.sashpos(0, list_width)
                self.saved_ranking_sash = list_width
                restored = True
        except Exception:
            pass

        if not restored:
            self.configure_initial_pane_layout()

    def run_restore_pane_layout(self):
        self._pane_restore_job = None
        self.restore_saved_pane_layout()

    def run_restore_pane_layout_followup(self):
        self._pane_restore_followup_job = None
        self.restore_saved_pane_layout()

    def schedule_restore_pane_layout(self, delay=40):
        if self._closing or not hasattr(self, "root"):
            return
        self._window_was_iconic = False
        if self._pane_restore_job is not None:
            try:
                self.root.after_cancel(self._pane_restore_job)
            except Exception:
                pass
        if self._pane_restore_followup_job is not None:
            try:
                self.root.after_cancel(self._pane_restore_followup_job)
            except Exception:
                pass
        self._pane_restore_job = self.root.after(delay, self.run_restore_pane_layout)
        self._pane_restore_followup_job = self.root.after(delay + 140, self.run_restore_pane_layout_followup)

    def on_window_unmap(self, event=None):
        if event is not None and event.widget is not self.root:
            return
        try:
            state = self.root.state()
        except Exception:
            return
        if state == "iconic":
            self.capture_current_pane_layout()
            self._window_was_iconic = True

    def on_window_map(self, event=None):
        if event is not None and event.widget is not self.root:
            return
        if self._window_was_iconic:
            self.schedule_restore_pane_layout(delay=30)

    def on_window_configure(self, event=None):
        if event is not None and event.widget is not self.root:
            return
        if not self._window_was_iconic:
            return
        try:
            if self.root.state() != "iconic":
                self.schedule_restore_pane_layout(delay=20)
        except Exception:
            pass
        
    def redirect_output(self):
        """重定向标准输出到文本框"""
        class TextRedirector:
            def __init__(self, app, text_widget, tag="stdout"):
                self.app = app
                self.text_widget = text_widget
                self.tag = tag
                self.buffer = ""
                
            def write(self, str):
                if self.app._closing or not str:
                    return
                self.buffer += str.replace('\r', '\n')
                while '\n' in self.buffer:
                    line, self.buffer = self.buffer.split('\n', 1)
                    if line.strip():
                        self.app.log_raw_output(line.strip())
                    
            def flush(self):
                if self.buffer.strip() and not self.app._closing:
                    self.app.log_raw_output(self.buffer.strip())
                self.buffer = ""
                
        sys.stdout = TextRedirector(self, self.log_text, "stdout")
        sys.stderr = TextRedirector(self, self.log_text, "stderr")

    def run_on_ui_thread(self, func, *args, **kwargs):
        """确保 UI 更新发生在主线程。"""
        if self._closing:
            return
        if threading.get_ident() == self.ui_thread_ident:
            func(*args, **kwargs)
            return
        self.ui_task_queue.put((func, args, kwargs))

    def schedule_ui_task_pump(self):
        if self._closing or not self.root.winfo_exists():
            return
        if self.ui_task_pump_job is None:
            self.ui_task_pump_job = self.root.after(16, self.flush_ui_task_queue)

    def flush_ui_task_queue(self):
        self.ui_task_pump_job = None
        if self._closing or not self.root.winfo_exists():
            return

        processed = 0
        while processed < 200:
            try:
                func, args, kwargs = self.ui_task_queue.get_nowait()
            except queue.Empty:
                break

            try:
                func(*args, **kwargs)
            except Exception as exc:
                try:
                    self.original_stderr.write(f"[UI任务执行失败] {exc}\n")
                    self.original_stderr.flush()
                except Exception:
                    pass
            processed += 1

        self.schedule_ui_task_pump()

    def infer_log_tag(self, message, default='info'):
        text = message.lower()
        if any(token in message for token in ['❌', 'failed', '出错', '错误', 'exception']):
            return 'error'
        if any(token in message for token in ['⚠️', 'warning', '跳过', '暂停']):
            return 'warning'
        if any(token in message for token in ['✅', '完成', '成功', '已恢复']):
            return 'success'
        if any(token in message for token in ['📂', '保存目录', '保存在']):
            return 'path'
        if any(token in message for token in ['🔍', 'processing', 'analyzing', 'fetching', '准备下载']):
            return 'debug'
        if any(token in message for token in ['🛑', '停止', '进度']):
            return 'status'
        if 'http://' in text or 'https://' in text:
            return 'muted'
        return default

    def append_log_line(self, message, tag="info"):
        """线程安全地把日志放进队列，批量刷新 UI。"""
        if self._closing or not message:
            return
        self.log_queue.put((message, tag))

    def schedule_log_flush(self):
        if self._closing or not self.root.winfo_exists():
            return
        if self.log_flush_job is None:
            self.log_flush_job = self.root.after(60, self.flush_log_queue)

    def trim_log_lines(self):
        try:
            total_lines = int(self.log_text.index("end-1c").split(".")[0])
        except Exception:
            return
        overflow = total_lines - self.max_log_lines
        if overflow > 0:
            self.log_text.delete("1.0", f"{overflow + 1}.0")

    def flush_log_queue(self):
        self.log_flush_job = None
        if self._closing or not self.root.winfo_exists() or not self.log_text.winfo_exists():
            return

        pending_logs = []
        try:
            while len(pending_logs) < 200:
                pending_logs.append(self.log_queue.get_nowait())
        except queue.Empty:
            pass

        if not pending_logs:
            self.schedule_log_flush()
            return

        for message, tag in pending_logs:
            self.log_text.insert(tk.END, message, tag)

        self.trim_log_lines()
        self.log_text.see(tk.END)

        self.schedule_log_flush()

    def safe_append_text(self, message, tag="info"):
        self.append_log_line(message, self.infer_log_tag(message, tag))

    def strip_web_urls(self, message):
        """移除日志中的网页链接，保留更清爽的中文提示。"""
        return re.sub(r'https?://\S+', '', message)

    def contains_cjk(self, message):
        """判断文本里是否包含中文，便于过滤英文调试日志。"""
        return bool(re.search(r'[\u4e00-\u9fff]', message))

    def normalize_log_message(self, message):
        """清理日志文本中的链接和多余空白。"""
        message = self.strip_web_urls(message or "")
        message = re.sub(r'\(\s*,\s*', '，', message)
        message = re.sub(r'\(\s*\)', '', message)
        message = " ".join(message.split())
        return message.strip(" :-")

    def log_raw_output(self, message):
        """格式化后台 print 输出，避免多段内容挤在一行。"""
        cleaned = self.normalize_log_message(message)
        if not cleaned or not self.contains_cjk(cleaned):
            return
        self.safe_append_text(f"{cleaned}\n")
        
    def log_message(self, message, tag="info"):
        """添加日志消息"""
        cleaned = self.normalize_log_message(message)
        if not cleaned:
            return
        timestamp = time.strftime("%H:%M:%S")
        self.append_log_line(f"[{timestamp}] {cleaned}\n", self.infer_log_tag(cleaned, tag))

    def set_status(self, text):
        def apply():
            if self._closing:
                return
            if not hasattr(self, "status_text_var"):
                return
            self.status_text_var.set(text or "")
        self.run_on_ui_thread(apply)

    def is_site_access_blocked_error(self, error):
        message = str(error or "")
        return "暂时拒绝当前网络环境访问" in message

    def is_site_unreachable_error(self, error):
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

    def get_connection_route_label(self, adapter):
        supports_manual_proxy = bool(getattr(adapter, "supports_manual_proxy", lambda: False)())
        if supports_manual_proxy and adapter.has_manual_proxy():
            return f"手动代理 {adapter.get_manual_proxy_url()}"
        if bool(getattr(adapter, "should_use_env_for_http", lambda: False)()):
            return "系统代理/环境代理"
        return "直连"

    def get_connection_test_target(self, adapter):
        candidate_url = (self.download_url_var.get() or self.current_download_url or "").strip()
        if candidate_url and adapter.matches_url(candidate_url):
            return candidate_url
        return f"https://{adapter.supported_domains[0]}/"

    def get_connection_troubleshooting_text(self, adapter):
        supports_manual_proxy = bool(getattr(adapter, "supports_manual_proxy", lambda: False)())
        if supports_manual_proxy and adapter.has_manual_proxy():
            return "\n".join([
                "1. 先点“测试连接”，确认当前代理节点是否真的可用。",
                "2. 如果仍失败，优先更换代理节点，或先关闭代理后改用其它网络。",
                "3. 用浏览器直接打开站点首页，确认不是站点本身临时异常。",
            ])
        if supports_manual_proxy:
            return "\n".join([
                "1. 先换手机热点或其它网络做对比测试。",
                "2. 如果换网后恢复，说明当前宽带/IP 很可能被限制了。",
                "3. 也可以填写 HTTP/HTTPS/SOCKS5 代理后，点“测试连接”再试。",
            ])
        return "\n".join([
            "1. 先换手机热点或其它网络做对比测试。",
            "2. 用浏览器直接打开站点首页，确认是否为站点临时异常。",
            "3. 如果浏览器也不通，就先不要继续排查程序代码。",
        ])

    def handle_site_access_blocked_error(self, adapter_name, error):
        raw_message = self.normalize_log_message(str(error))
        friendly_message = (
            f"⚠️ {adapter_name} 当前拒绝了本机网络环境的接口访问，这不是程序解析错误。"
        )
        detail_message = (
            "站点返回的限制信息表明，当前环境暂时无法继续获取章节数据。"
            "建议先等站点解除限制后再试；如果后面恢复了，我这边这套适配代码就可以继续用。"
        )

        self.log_message(friendly_message, "warning")
        if raw_message:
            self.log_message(f"站点返回: {raw_message}", "warning")
        self.log_message(detail_message, "warning")
        self.run_on_ui_thread(
            messagebox.showwarning,
            "站点限制",
            f"{adapter_name} 当前拒绝了本机网络环境的接口访问。\n\n这不是程序解析错误。\n请等待站点解除限制后再试。",
        )

    def handle_site_unreachable_error(self, adapter, error):
        adapter_name = adapter.display_name if hasattr(adapter, "display_name") else str(adapter)
        raw_message = self.normalize_log_message(str(error))
        route_label = self.get_connection_route_label(adapter if hasattr(adapter, "display_name") else self.current_adapter)
        troubleshooting = self.get_connection_troubleshooting_text(adapter if hasattr(adapter, "display_name") else self.current_adapter)
        self.log_message(f"⚠️ {adapter_name} 当前从这台机器访问不稳定，暂时没能拿到网页内容。", "warning")
        self.log_message(f"当前请求方式: {route_label}", "warning")
        if raw_message:
            self.log_message(f"网络详情: {raw_message}", "warning")
        self.log_message("这更像是站点连通性问题，不一定是适配代码本身出错。", "warning")
        for line in troubleshooting.splitlines():
            self.log_message(line, "warning")
        self.run_on_ui_thread(
            messagebox.showwarning,
            "站点连接失败",
            f"{adapter_name} 当前无法通过“{route_label}”拿到网页内容。\n\n建议按这个顺序排查：\n{troubleshooting}",
        )

    def set_progress(self, value):
        def apply():
            self.progress_var.set(value)
            self.progress_text_var.set(f"{value:.0f}%")
        self.run_on_ui_thread(apply)

    def set_progress_style(self, style_name):
        self.run_on_ui_thread(self.progress_bar.configure, style=style_name)

    def update_control_buttons(self, downloading=False, paused=False):
        def apply():
            if self._closing:
                return
            self.download_btn.config(state=tk.DISABLED if downloading else tk.NORMAL)
            self.stop_btn.config(state=tk.NORMAL if downloading else tk.DISABLED)
            self.pause_btn.config(state=tk.NORMAL if downloading and not paused else tk.DISABLED)
            self.resume_btn.config(state=tk.NORMAL if downloading and paused else tk.DISABLED)
        self.run_on_ui_thread(apply)

    def set_fetch_info_button_state(self, loading=False):
        def apply():
            if self._closing:
                return
            self.fetch_info_btn.config(
                state=tk.DISABLED if loading else tk.NORMAL,
                text="获取中..." if loading else "获取信息",
            )
        self.run_on_ui_thread(apply)

    def set_ranking_buttons_state(self, loading=False):
        def apply():
            if self._closing:
                return
            section_key = self.section_options.get(self.homepage_section_var.get(), "") if self.section_options else ""
            supports_current_search = self.current_view_supports_search(section_key)
            is_local_library = self.is_local_library_section(section_key)
            if loading:
                self.refresh_rank_btn.config(state=tk.DISABLED)
                self.download_rank_btn.config(state=tk.DISABLED)
                self.check_updates_btn.config(state=tk.DISABLED, text="检查更新")
                self.prev_page_btn.config(state=tk.DISABLED)
                self.next_page_btn.config(state=tk.DISABLED)
                self.search_btn.config(state=tk.DISABLED)
                self.clear_search_btn.config(state=tk.DISABLED)
                return

            has_discovery = self.adapter_has_discovery_sections()
            has_search = self.adapter_supports_search()
            has_cards = bool(self.rank_cards)
            search_query = self.get_active_search_query()
            section_key = self.section_options.get(self.homepage_section_var.get(), "rank") if has_discovery else ""

            self.refresh_rank_btn.config(
                state=tk.NORMAL if has_discovery or (has_search and search_query) else tk.DISABLED
            )
            self.download_rank_btn.config(state=tk.NORMAL if has_cards else tk.DISABLED)
            self.check_updates_btn.config(
                state=tk.NORMAL if is_local_library and has_cards and not self.is_checking_library_updates else tk.DISABLED,
                text="检查中..." if self.is_checking_library_updates else "检查更新",
            )
            self.search_btn.config(state=tk.NORMAL if supports_current_search else tk.DISABLED)
            self.clear_search_btn.config(
                state=tk.NORMAL if supports_current_search and (search_query or has_cards) else tk.DISABLED
            )
            self.search_entry.config(state=tk.NORMAL if supports_current_search else tk.DISABLED)

            if search_query and supports_current_search:
                self.prev_page_btn.config(state=tk.DISABLED if self.current_section_page <= 1 else tk.NORMAL)
                self.next_page_btn.config(state=tk.DISABLED if not has_cards else tk.NORMAL)
            elif has_discovery:
                is_recent = section_key == "recent"
                self.prev_page_btn.config(
                    state=tk.DISABLED if is_recent or self.current_section_page <= 1 else tk.NORMAL
                )
                self.next_page_btn.config(
                    state=tk.DISABLED if is_recent or not has_cards else tk.NORMAL
                )
            else:
                self.prev_page_btn.config(state=tk.DISABLED)
                self.next_page_btn.config(state=tk.DISABLED)

            self.refresh_theme_filter_controls()
        self.run_on_ui_thread(apply)

    def update_section_pagination_ui(self, section_key, page, has_cards=True, search_query=""):
        def apply():
            if self._closing:
                return
            if search_query:
                page_label = f"本地搜索第 {page} 页" if self.is_local_library_section(section_key) else f"搜索第 {page} 页"
                self.section_page_var.set(page_label)
                self.prev_page_btn.config(state=tk.DISABLED if page <= 1 else tk.NORMAL)
                self.next_page_btn.config(state=tk.DISABLED if not has_cards else tk.NORMAL)
                return

            self.section_page_var.set(f"第 {page} 页")
            adapter_single_page = bool(
                getattr(self.current_adapter, "is_single_page_section", lambda _section: False)(section_key)
            )
            is_single_page = section_key == "recent" or adapter_single_page
            self.prev_page_btn.config(state=tk.DISABLED if is_single_page or page <= 1 else tk.NORMAL)
            self.next_page_btn.config(state=tk.DISABLED if is_single_page or not has_cards else tk.NORMAL)
        self.run_on_ui_thread(apply)

    def get_section_display_name(self, section_key):
        mapping = {
            "rank": "人气排行",
            "recent": "近期更新",
            "hot-update": "热门更新",
            "new": "最新上架",
            "recommend": "编辑推荐",
            "newest": "全新上架",
            "discover-latest": "发现更新",
            "discover-popular": "发现热门",
            "rank-day-male": "男频日榜",
            "rank-day-female": "女频日榜",
            "rank-week-male": "男频周榜",
            "rank-month-male": "男频月榜",
            "rank-total-male": "男频总榜",
            "local-library": "本地已下载",
        }
        return mapping.get(section_key, section_key)

    def format_updated_at(self, value):
        if not value:
            return "-"
        try:
            dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
            return dt.strftime("%Y-%m-%d %H:%M")
        except Exception:
            return str(value)

    def get_active_search_query(self):
        return self.search_query_var.get().strip()

    def adapter_supports_search(self):
        return bool(getattr(self.current_adapter, "supports_search", False))

    def get_adapter_section_options(self, adapter=None):
        target_adapter = adapter or self.current_adapter
        options = dict(target_adapter.get_section_options() or {})
        if getattr(target_adapter, "supports_download", False):
            options["本地已下载"] = "local-library"
        return options

    def is_local_library_section(self, section_key=""):
        resolved = section_key or self.section_options.get(self.homepage_section_var.get(), "")
        return resolved == "local-library"

    def current_view_supports_search(self, section_key=""):
        resolved = section_key or self.section_options.get(self.homepage_section_var.get(), "")
        return self.adapter_supports_search() or self.is_local_library_section(resolved)

    def adapter_has_discovery_sections(self):
        return bool(self.section_options)

    def get_adapter_theme_options(self, adapter=None):
        target_adapter = adapter or self.current_adapter
        getter = getattr(target_adapter, "get_theme_options", None)
        if callable(getter):
            return dict(getter() or {})
        return {}

    def section_supports_theme_filter(self, section_key=""):
        resolved_section_key = section_key or self.section_options.get(self.homepage_section_var.get(), "")
        checker = getattr(self.current_adapter, "supports_theme_filter", None)
        if callable(checker):
            return bool(checker(resolved_section_key))
        return False

    def refresh_theme_filter_controls(self):
        section_key = self.section_options.get(self.homepage_section_var.get(), "") if self.section_options else ""
        theme_names = list(self.theme_options.keys()) or ["全部题材"]
        current_theme = self.homepage_theme_var.get()
        if current_theme not in theme_names:
            self.homepage_theme_var.set(theme_names[0])

        supports_theme = bool(self.theme_options and self.section_supports_theme_filter(section_key))
        if not supports_theme:
            self.homepage_theme_var.set(theme_names[0])

        self.homepage_theme_combo.config(values=theme_names)
        if supports_theme and not self.get_active_search_query():
            self.homepage_theme_combo.config(state="readonly")
        else:
            self.homepage_theme_combo.config(state=tk.DISABLED)

    def clear_ranking_results(self):
        self.current_homepage_cards = []
        self.rank_cards = []
        self.populate_ranking_tree([])
        self.update_ranking_detail(None)

    def get_default_section_page_text(self):
        if self.get_active_search_query() and self.adapter_supports_search():
            return f"搜索第 {self.current_section_page} 页"
        if self.adapter_has_discovery_sections():
            return f"第 {self.current_section_page} 页"
        if self.adapter_supports_search():
            return "请输入关键词搜索"
        return "仅支持手动输入 URL"

    def populate_ranking_tree(self, cards):
        def apply():
            if self._closing or not self.rank_tree.winfo_exists():
                return
            self.rank_tree.delete(*self.rank_tree.get_children())
            for index, card in enumerate(cards, 1):
                self.rank_tree.insert(
                    "",
                    tk.END,
                    iid=str(index - 1),
                    values=(index, card.title)
                )
        self.run_on_ui_thread(apply)

    def refresh_selected_tree_row(self, card):
        def apply():
            if self._closing or not self.rank_tree.winfo_exists():
                return
            selection = self.rank_tree.selection()
            if not selection:
                return
            iid = selection[0]
            try:
                index = int(iid)
            except (TypeError, ValueError):
                return
            if index < 0 or index >= len(self.rank_cards) or not self.rank_tree.exists(iid):
                return
            self.rank_tree.item(iid, values=(index + 1, card.title))

        self.run_on_ui_thread(apply)

    def reset_cover_preview(self, text="封面预览"):
        def apply():
            if self._closing:
                return
            self.cover_image = None
            self.cover_preview.config(image="", text=text, bg=self.colors['accent_soft'], fg=self.colors['accent'])
        self.run_on_ui_thread(apply)

    def reset_detail_local_state(self, status_text="本地状态: 未检测", path_text=""):
        self.current_detail_root_dir = ""
        self.current_detail_library_entry = None

        def apply():
            if self._closing:
                return
            self.detail_local_status_var.set(status_text)
            self.detail_local_path_var.set(path_text)
            self.refresh_detail_local_action_buttons()

        self.run_on_ui_thread(apply)

    def refresh_detail_local_action_buttons(self):
        if self._closing:
            return
        has_root_dir = bool(self.current_detail_root_dir and os.path.isdir(self.current_detail_root_dir))
        self.open_local_dir_btn.config(state=tk.NORMAL if has_root_dir else tk.DISABLED)
        self.export_cbz_btn.config(
            state=tk.DISABLED if (not has_root_dir or self.is_exporting_cbz) else tk.NORMAL,
            text="导出中..." if self.is_exporting_cbz else "导出 CBZ",
        )

    def format_local_library_status(self, entry):
        downloaded_count = int(entry.get("downloaded_chapter_count") or 0)
        total_chapters = int(entry.get("total_chapters") or 0)
        completed = bool(entry.get("completed"))

        if downloaded_count <= 0:
            return "本地状态: 未发现已下载章节"
        if total_chapters > 0:
            if completed and downloaded_count >= total_chapters:
                return f"本地状态: 已下载完成（{downloaded_count}/{total_chapters} 章）"
            return f"本地状态: 已下载 {downloaded_count}/{total_chapters} 章"
        if completed:
            return f"本地状态: 已下载完成（{downloaded_count} 章）"
        return f"本地状态: 已下载 {downloaded_count} 章"

    def get_library_update_status_lines(self, entry, include_error=False):
        status = str(entry.get("update_check_status") or "").strip()
        checked_at = str(entry.get("update_last_checked_at") or "").strip()
        error = str(entry.get("update_last_error") or "").strip()
        lines = []
        if status:
            lines.append(f"更新检查: {status}")
        if checked_at:
            lines.append(f"检查时间: {checked_at}")
        if include_error and error:
            lines.append(f"错误信息: {error}")
        return lines

    def compute_update_available_count(self, entry, online_chapter_count):
        try:
            normalized_total = max(int(online_chapter_count or 0), 0)
        except Exception:
            normalized_total = 0

        try:
            last_downloaded_order = int(entry.get("last_downloaded_chapter_order") or 0)
        except Exception:
            last_downloaded_order = 0

        try:
            downloaded_count = int(entry.get("downloaded_chapter_count") or 0)
        except Exception:
            downloaded_count = 0

        known_progress = last_downloaded_order if last_downloaded_order > 0 else downloaded_count
        return max(normalized_total - max(known_progress, 0), 0)

    def get_local_library_entry_by_root(self, root_dir, site_key=""):
        resolved_root_dir = (root_dir or "").strip()
        if not resolved_root_dir or not os.path.isdir(resolved_root_dir):
            return None

        disk_has_chapter_dirs = self.looks_like_manga_download_dir(resolved_root_dir)
        if not disk_has_chapter_dirs:
            return None

        metadata = self.load_manga_library_metadata(resolved_root_dir)
        metadata_site_key = (metadata.get("site_key") or "").strip() if metadata else ""
        if site_key and metadata_site_key and metadata_site_key != site_key:
            return None

        fallback_site_key = metadata_site_key or site_key
        fallback = self.build_local_library_entry_from_fallback(resolved_root_dir, site_key=fallback_site_key)
        if metadata:
            if fallback is not None:
                metadata["site_key"] = metadata_site_key or fallback.get("site_key") or ""
                metadata["site_name"] = metadata.get("site_name") or fallback.get("site_name") or self.current_adapter.display_name
                metadata["downloaded_chapters"] = list(fallback.get("downloaded_chapters") or [])
                metadata["downloaded_chapter_count"] = int(fallback.get("downloaded_chapter_count") or 0)
                metadata["last_downloaded_chapter_title"] = fallback.get("last_downloaded_chapter_title") or metadata.get("last_downloaded_chapter_title") or ""
                metadata["last_downloaded_chapter_order"] = fallback.get("last_downloaded_chapter_order")
                metadata["saved_at"] = metadata.get("saved_at") or fallback.get("saved_at") or metadata.get("created_at") or ""
                metadata["total_chapters"] = max(
                    int(metadata.get("total_chapters") or 0),
                    int(metadata.get("downloaded_chapter_count") or 0),
                )
            if site_key and not metadata_site_key:
                fallback_for_site = self.build_local_library_entry_from_fallback(resolved_root_dir, site_key=site_key)
                if fallback_for_site is None:
                    return None
                metadata["site_key"] = fallback_for_site.get("site_key") or metadata.get("site_key") or ""
                metadata["site_name"] = fallback_for_site.get("site_name") or metadata.get("site_name") or self.current_adapter.display_name
            return self.enrich_local_library_entry_identity(metadata, preferred_site_key=site_key)

        return self.enrich_local_library_entry_identity(fallback, preferred_site_key=site_key)

    def find_local_library_entry_by_source_url(self, adapter, source_url):
        normalized_url = (source_url or "").strip()
        if not normalized_url:
            return None

        normalized_url_no_slash = normalized_url.rstrip("/")
        target_cache_keys = {
            adapter.get_manga_cache_key(normalized_url),
            adapter.get_manga_cache_key(normalized_url_no_slash),
        }
        for entry in self.iter_local_library_entries(site_key=adapter.key):
            entry_url = (entry.get("manga_url") or "").strip()
            if not entry_url:
                continue
            entry_url_no_slash = entry_url.rstrip("/")
            entry_cache_keys = {
                adapter.get_manga_cache_key(entry_url),
                adapter.get_manga_cache_key(entry_url_no_slash),
            }
            if target_cache_keys & entry_cache_keys:
                return entry

        root_dir = self.find_local_manga_root_dir(adapter, normalized_url)
        if root_dir:
            return self.get_local_library_entry_by_root(root_dir, site_key=adapter.key)
        return None

    def get_local_library_entry_for_card(self, card):
        if not card:
            return None

        inline_entry = getattr(card, "local_library_entry", None)
        if isinstance(inline_entry, dict) and inline_entry.get("root_dir"):
            return dict(inline_entry)

        local_root_dir = (getattr(card, "local_root_dir", "") or "").strip()
        local_site_key = (getattr(card, "local_site_key", "") or "").strip()
        if local_root_dir:
            entry = self.get_local_library_entry_by_root(local_root_dir, site_key=local_site_key)
            if entry is not None:
                return entry

        source_url = (getattr(card, "manga_url", "") or "").strip()
        if not source_url:
            return None

        adapter = resolve_adapter_from_url(source_url, fallback_key=self.current_adapter.key)
        return self.find_local_library_entry_by_source_url(adapter, source_url)

    def update_detail_local_state(self, card=None):
        if not card:
            self.reset_detail_local_state(status_text="本地状态: 未检测", path_text="")
            return

        entry = self.get_local_library_entry_for_card(card)
        if not entry:
            self.reset_detail_local_state(status_text="本地状态: 未发现本地下载", path_text="")
            return

        root_dir = (entry.get("root_dir") or "").strip()
        saved_at = str(entry.get("saved_at") or entry.get("created_at") or "").strip()
        path_lines = self.get_library_update_status_lines(entry, include_error=True)
        if root_dir:
            path_lines.append(f"本地目录: {root_dir}")
        if saved_at:
            path_lines.append(f"最近保存: {saved_at}")

        self.current_detail_root_dir = root_dir if os.path.isdir(root_dir) else ""
        self.current_detail_library_entry = dict(entry)

        def apply():
            if self._closing:
                return
            self.detail_local_status_var.set(self.format_local_library_status(entry))
            self.detail_local_path_var.set("\n".join(path_lines))
            self.refresh_detail_local_action_buttons()

        self.run_on_ui_thread(apply)

    def open_current_detail_root_dir(self):
        root_dir = (self.current_detail_root_dir or "").strip()
        if not root_dir or not os.path.isdir(root_dir):
            self.reset_detail_local_state(status_text="本地状态: 目录不可用", path_text="")
            self.run_on_ui_thread(messagebox.showwarning, "提示", "当前没有可打开的本地目录。")
            return

        try:
            if hasattr(os, "startfile"):
                os.startfile(root_dir)
            else:
                subprocess.Popen(["explorer", root_dir])
            self.log_message(f"📂 已打开本地目录: {root_dir}")
            self.set_status("已打开本地目录")
        except Exception as exc:
            self.log_message(f"打开本地目录失败: {str(exc)}", "warning")
            self.run_on_ui_thread(messagebox.showwarning, "打开失败", str(exc))

    def build_checked_library_entry(self, entry, adapter, detail):
        now_text = time.strftime("%Y-%m-%d %H:%M:%S")
        updated_entry = dict(entry or {})
        online_total = int(getattr(detail, "chapter_count", 0) or updated_entry.get("total_chapters") or 0)
        update_available_count = self.compute_update_available_count(updated_entry, online_total)

        updated_entry["schema_version"] = max(int(updated_entry.get("schema_version") or 1), 1)
        updated_entry["site_key"] = adapter.key
        updated_entry["site_name"] = adapter.display_name
        updated_entry["manga_title"] = str(getattr(detail, "title", "") or updated_entry.get("manga_title") or "本地漫画")
        updated_entry["manga_url"] = (updated_entry.get("manga_url") or getattr(detail, "manga_url", "") or "").strip()
        updated_entry["cover_url"] = str(getattr(detail, "cover_url", "") or updated_entry.get("cover_url") or "")
        updated_entry["total_chapters"] = max(online_total, int(updated_entry.get("downloaded_chapter_count") or 0))
        updated_entry["latest_known_chapter_title"] = str(
            getattr(detail, "latest_chapter", "") or updated_entry.get("latest_known_chapter_title") or updated_entry.get("last_downloaded_chapter_title") or "-"
        )
        updated_entry["latest_known_update_time"] = str(
            getattr(detail, "update_time", "") or updated_entry.get("latest_known_update_time") or "-"
        )
        updated_entry["update_available_count"] = update_available_count
        updated_entry["update_last_checked_at"] = now_text
        updated_entry["update_last_error"] = ""
        if online_total > 0 and update_available_count > 0:
            updated_entry["update_check_status"] = f"发现 {update_available_count} 章可更新"
        elif online_total > 0:
            updated_entry["update_check_status"] = "已是最新"
        else:
            updated_entry["update_check_status"] = "检查完成，但未获取到有效章节数"
        return updated_entry

    def build_failed_update_check_entry(self, entry, status_text, error_message=""):
        now_text = time.strftime("%Y-%m-%d %H:%M:%S")
        updated_entry = dict(entry or {})
        updated_entry["schema_version"] = max(int(updated_entry.get("schema_version") or 1), 1)
        updated_entry["update_check_status"] = str(status_text or "检查失败")
        updated_entry["update_available_count"] = 0
        updated_entry["update_last_checked_at"] = now_text
        updated_entry["update_last_error"] = str(error_message or "")
        return updated_entry

    def check_local_library_updates(self):
        if self.is_checking_library_updates:
            return

        section_key = self.section_options.get(self.homepage_section_var.get(), "") if self.section_options else ""
        if not self.is_local_library_section(section_key):
            self.run_on_ui_thread(messagebox.showwarning, "提示", "请先切到“本地已下载”分区。")
            return

        if not self.apply_manual_proxy_settings(show_feedback=False):
            return

        cards = list(self.rank_cards)
        if not cards:
            self.run_on_ui_thread(messagebox.showwarning, "提示", "当前页没有可检查更新的本地漫画。")
            return

        entries = []
        seen_root_dirs = set()
        for card in cards:
            entry = self.get_local_library_entry_for_card(card)
            if not entry:
                continue
            root_dir = (entry.get("root_dir") or "").strip()
            if not root_dir or root_dir in seen_root_dirs:
                continue
            seen_root_dirs.add(root_dir)
            entries.append(entry)

        if not entries:
            self.run_on_ui_thread(messagebox.showwarning, "提示", "当前页没有可检查更新的本地漫画。")
            return

        self.is_checking_library_updates = True
        self.run_on_ui_thread(self.set_ranking_buttons_state, False)
        self.log_message(f"🔄 正在检查本地漫画更新: 当前页共 {len(entries)} 部")
        self.set_status("正在检查本地漫画更新...")

        def worker():
            updatable_count = 0
            latest_count = 0
            skipped_count = 0
            failed_count = 0

            try:
                for index, entry in enumerate(entries, 1):
                    manga_title = str(entry.get("manga_title") or f"本地漫画{index}")
                    manga_url = (entry.get("manga_url") or "").strip()
                    site_key = (entry.get("site_key") or self.current_adapter.key).strip()

                    if not manga_url:
                        skipped_count += 1
                        skipped_entry = self.build_failed_update_check_entry(entry, "缺少原始链接，无法检查更新")
                        self.save_library_entry_metadata(skipped_entry)
                        self.log_message(f"⚠️ [{index}/{len(entries)}] {manga_title}: 缺少原始链接，已跳过", "warning")
                        continue

                    try:
                        adapter = resolve_adapter_from_url(manga_url, fallback_key=site_key or self.current_adapter.key)
                        detail = adapter.fetch_manga_detail(manga_url)
                        self.cache_manga_detail(adapter, manga_url, detail)
                        checked_entry = self.build_checked_library_entry(entry, adapter, detail)
                        self.save_library_entry_metadata(checked_entry)

                        update_available_count = int(checked_entry.get("update_available_count") or 0)
                        if update_available_count > 0:
                            updatable_count += 1
                            self.log_message(
                                f"🆕 [{index}/{len(entries)}] {manga_title}: 发现 {update_available_count} 章可更新",
                                "warning",
                            )
                        else:
                            latest_count += 1
                            self.log_message(f"✅ [{index}/{len(entries)}] {manga_title}: 已是最新")
                    except Exception as exc:
                        failed_count += 1
                        failed_entry = self.build_failed_update_check_entry(entry, "检查失败", str(exc))
                        self.save_library_entry_metadata(failed_entry)
                        self.log_message(f"❌ [{index}/{len(entries)}] {manga_title}: 检查更新失败: {str(exc)}", "error")
            finally:
                self.is_checking_library_updates = False

            summary = (
                f"检查完成: 可更新 {updatable_count} 部，"
                f"已最新 {latest_count} 部，"
                f"跳过 {skipped_count} 部，"
                f"失败 {failed_count} 部"
            )
            self.log_message(f"📚 {summary}")
            self.set_status(summary)
            self.run_on_ui_thread(self.set_ranking_buttons_state, False)
            self.run_on_ui_thread(self.refresh_rankings)

        threading.Thread(target=worker, daemon=True).start()

    def export_current_detail_to_cbz(self):
        if self.is_exporting_cbz:
            return

        root_dir = (self.current_detail_root_dir or "").strip()
        if not root_dir or not os.path.isdir(root_dir):
            self.reset_detail_local_state(status_text="本地状态: 目录不可用", path_text="")
            self.run_on_ui_thread(messagebox.showwarning, "提示", "当前没有可导出的本地目录。")
            return

        entry = dict(self.current_detail_library_entry or {})
        manga_title = str(entry.get("manga_title") or self.current_detail_title or os.path.basename(root_dir.rstrip("\\/")) or "本地漫画")
        manga_url = str(entry.get("manga_url") or self.current_detail_url or "")

        self.is_exporting_cbz = True
        self.run_on_ui_thread(self.refresh_detail_local_action_buttons)
        self.log_message(f"📚 正在导出 CBZ: {manga_title}")
        self.set_status("正在导出 CBZ...")

        def worker():
            try:
                export_dir, exported_archives, skipped_chapters = self.export_manga_to_cbz(
                    root_dir=root_dir,
                    manga_title=manga_title,
                    manga_url=manga_url,
                )
                total_pages = sum(image_count for _, image_count in exported_archives)
                self.log_message(f"✅ CBZ 导出完成: {export_dir}")
                self.log_message(f"📚 共导出 {len(exported_archives)} 个章节 CBZ，写入 {total_pages} 张图片")
                if skipped_chapters:
                    self.log_message(
                        f"⚠️ 以下章节因未发现图片而跳过: {', '.join(skipped_chapters[:5])}"
                        + (" ..." if len(skipped_chapters) > 5 else ""),
                        "warning",
                    )
                self.set_status("CBZ 导出完成")
                self.run_on_ui_thread(
                    messagebox.showinfo,
                    "导出完成",
                    f"已导出 {len(exported_archives)} 个 CBZ 文件：\n{export_dir}",
                )
            except Exception as exc:
                self.log_message(f"❌ 导出 CBZ 失败: {str(exc)}", "error")
                self.set_status("导出 CBZ 失败")
                self.run_on_ui_thread(
                    messagebox.showwarning,
                    "导出失败",
                    str(exc),
                )
            finally:
                self.is_exporting_cbz = False
                self.run_on_ui_thread(self.refresh_detail_local_action_buttons)

        threading.Thread(target=worker, daemon=True).start()

    def update_ranking_detail(self, card=None):
        if not card:
            self.current_detail_title = ""
            self.current_detail_url = ""
            self.run_on_ui_thread(self.detail_title_var.set, "请选择一部漫画")
            self.run_on_ui_thread(self.detail_section_var.set, "分区: -")
            self.run_on_ui_thread(self.detail_latest_var.set, "最近章节: -")
            self.run_on_ui_thread(self.detail_update_var.set, "更新时间: -")
            self.run_on_ui_thread(self.detail_cover_var.set, "")
            self.reset_detail_local_state()
            self.reset_cover_preview()
            return

        self.current_detail_title = str(getattr(card, "title", "") or "")
        self.current_detail_url = str(getattr(card, "manga_url", "") or "")
        self.run_on_ui_thread(self.detail_title_var.set, card.title)
        section_text = getattr(card, "detail_section_label", f"分区: {card.section}")
        detail_hint = getattr(card, "detail_hint", "")
        self.run_on_ui_thread(self.detail_section_var.set, section_text)
        self.run_on_ui_thread(self.detail_latest_var.set, f"最近章节: {card.latest_chapter or '-'}")
        self.run_on_ui_thread(self.detail_update_var.set, f"更新时间: {card.update_time or '-'}")
        self.run_on_ui_thread(self.detail_cover_var.set, detail_hint)
        self.update_detail_local_state(card)
        self.load_cover_preview(card.cover_url, card.title, getattr(card, "manga_url", ""))
        self.enrich_card_detail(card)

    def enrich_card_detail(self, card):
        if getattr(card, "disable_detail_enrich", False):
            return

        cache_key = card.manga_url
        self.current_detail_request_key = cache_key

        cached = self.rank_detail_cache.get(cache_key)
        if cached:
            card.latest_chapter = cached.get("latest_chapter", card.latest_chapter)
            card.update_time = cached.get("update_time", card.update_time)
            self.run_on_ui_thread(self.detail_latest_var.set, f"最近章节: {card.latest_chapter or '-'}")
            self.run_on_ui_thread(self.detail_update_var.set, f"更新时间: {card.update_time or '-'}")
            self.refresh_selected_tree_row(card)
            return

        if card.latest_chapter and card.update_time:
            return

        self.run_on_ui_thread(self.detail_latest_var.set, f"最近章节: {card.latest_chapter or '加载中...'}")
        self.run_on_ui_thread(self.detail_update_var.set, f"更新时间: {card.update_time or '加载中...'}")

        def worker():
            try:
                adapter = resolve_adapter_from_url(card.manga_url, fallback_key=self.current_adapter.key)
                manga_id, _, _ = adapter.get_manga_info_from_url(card.manga_url)
                if not manga_id:
                    return
                _, chapters = adapter.get_all_chapters(manga_id)
                if not chapters:
                    return

                latest = chapters[-1]
                latest_title = latest.get("title") or latest.get("slug") or "-"
                updated_at = self.format_updated_at(latest.get("updated_at"))
                self.rank_detail_cache[cache_key] = {
                    "latest_chapter": latest_title,
                    "update_time": updated_at,
                }

                if self.current_detail_request_key != cache_key:
                    return

                card.latest_chapter = latest_title
                card.update_time = updated_at
                self.run_on_ui_thread(self.detail_latest_var.set, f"最近章节: {latest_title}")
                self.run_on_ui_thread(self.detail_update_var.set, f"更新时间: {updated_at}")
                self.refresh_selected_tree_row(card)
            except Exception:
                if self.current_detail_request_key == cache_key:
                    if not card.latest_chapter:
                        self.run_on_ui_thread(self.detail_latest_var.set, "最近章节: -")
                    if not card.update_time:
                        self.run_on_ui_thread(self.detail_update_var.set, "更新时间: -")

        threading.Thread(target=worker, daemon=True).start()

    def normalize_cover_preview_url(self, cover_url, source_url=""):
        candidate = unwrap_cover_url((cover_url or "").strip())
        if not candidate:
            return ""
        if os.path.isfile(candidate):
            return candidate
        if candidate.startswith("//"):
            return f"https:{candidate}"
        if source_url:
            return urljoin(source_url, candidate)
        return candidate

    def build_cover_preview_referers(self, adapter, source_url=""):
        referers = []
        normalized_source = (source_url or "").strip()
        if normalized_source:
            referers.append(normalized_source)
        if adapter.supported_domains:
            referers.append(f"https://{adapter.supported_domains[0]}/")
        referers.append("https://baozimh.org/")

        unique_referers = []
        for referer in referers:
            if referer and referer not in unique_referers:
                unique_referers.append(referer)
        return unique_referers

    def load_cover_preview(self, cover_url, title, source_url=""):
        normalized_cover_url = self.normalize_cover_preview_url(cover_url, source_url)
        self.current_cover_url = normalized_cover_url
        if not normalized_cover_url:
            self.reset_cover_preview("暂无封面")
            return

        if Image is None or ImageTk is None:
            self.reset_cover_preview("未安装 Pillow\n无法显示封面")
            return

        self.reset_cover_preview("封面加载中...")

        def worker():
            try:
                resampling_module = getattr(Image, "Resampling", Image)

                if os.path.isfile(normalized_cover_url):
                    with Image.open(normalized_cover_url) as image:
                        image.load()
                        image.thumbnail((260, 360), resampling_module.LANCZOS)
                        prepared_image = image.copy()

                    def apply_local_image():
                        if self._closing or normalized_cover_url != self.current_cover_url:
                            return
                        try:
                            photo = ImageTk.PhotoImage(prepared_image)
                        except Exception as exc:
                            self.log_message(f"⚠️ 封面渲染失败: {str(exc)}", "warning")
                            self.reset_cover_preview(f"{title}\n封面加载失败")
                            return
                        self.cover_image = photo
                        self.cover_preview.config(image=photo, text="", bg=self.colors['surface_alt'])

                    self.run_on_ui_thread(apply_local_image)
                    return

                adapter = resolve_adapter_from_url(source_url or normalized_cover_url, fallback_key=self.current_adapter.key)
                session = requests.Session()
                adapter.configure_requests_session(session, for_image=True)
                image = None
                last_error = None
                for referer in self.build_cover_preview_referers(adapter, source_url):
                    try:
                        resp = session.get(
                            normalized_cover_url,
                            timeout=20,
                            headers={
                                'User-Agent': 'Mozilla/5.0',
                                'Referer': referer,
                                'Accept': 'image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8'
                            }
                        )
                        resp.raise_for_status()
                        image = Image.open(io.BytesIO(resp.content))
                        image.load()
                        break
                    except Exception as exc:
                        last_error = exc
                        continue

                if image is None:
                    raise last_error or RuntimeError("封面请求失败")
                image.thumbnail((180, 220), resampling_module.LANCZOS)
                prepared_image = image.copy()

                def apply():
                    if self._closing or normalized_cover_url != self.current_cover_url:
                        return
                    try:
                        photo = ImageTk.PhotoImage(prepared_image)
                    except Exception as exc:
                        self.log_message(f"⚠️ 封面渲染失败: {str(exc)}", "warning")
                        self.reset_cover_preview(f"{title}\n封面加载失败")
                        return
                    self.cover_image = photo
                    self.cover_preview.config(image=photo, text="", bg=self.colors['surface_alt'])

                self.run_on_ui_thread(apply)
            except Exception as exc:
                if normalized_cover_url == self.current_cover_url:
                    self.log_message(f"⚠️ 封面加载失败: {str(exc)}", "warning")
                    self.reset_cover_preview(f"{title}\n封面加载失败")

        threading.Thread(target=worker, daemon=True).start()

    def on_search_enter(self, event=None):
        self.search_manga()

    def search_manga(self):
        keyword = self.get_active_search_query()
        if not keyword:
            messagebox.showwarning("提示", "请输入漫画名称后再搜索。")
            return
        self.current_section_page = 1
        self.refresh_rankings()

    def clear_search(self):
        if not self.get_active_search_query():
            if self.adapter_supports_search() and not self.adapter_has_discovery_sections() and self.rank_cards:
                self.clear_ranking_results()
                self.current_section_page = 1
                self.section_page_var.set(self.get_default_section_page_text())
                self.set_ranking_buttons_state(loading=False)
                self.set_status(f"{self.current_adapter.display_name} 等待搜索")
            return
        self.search_query_var.set("")
        self.current_section_page = 1
        self.log_message("已清空搜索结果。")
        if self.adapter_supports_search() and not self.adapter_has_discovery_sections():
            self.clear_ranking_results()
            self.section_page_var.set(self.get_default_section_page_text())
            self.set_ranking_buttons_state(loading=False)
            self.set_status(f"{self.current_adapter.display_name} 等待搜索")
            return
        self.refresh_rankings()

    def fetch_manga_detail(self, event=None):
        if self.is_fetching_manga_detail:
            return

        url = (self.download_url_var.get() or self.current_download_url or "").strip()
        if not url:
            messagebox.showwarning("提示", "请先输入漫画链接后再获取信息。")
            return

        adapter = resolve_adapter_from_url(url, fallback_key=self.get_selected_adapter().key)
        if not adapter.matches_url(url):
            supported_sites = "、".join(get_site_display_names())
            messagebox.showwarning("提示", f"暂时无法识别该链接所属站点。当前已接入站点: {supported_sites}")
            return

        self.is_fetching_manga_detail = True
        self.set_fetch_info_button_state(loading=True)
        self.set_active_adapter(adapter.key)
        if not self.apply_manual_proxy_settings(show_feedback=False):
            self.is_fetching_manga_detail = False
            self.set_fetch_info_button_state(loading=False)
            return
        self.set_download_url(url)
        self.set_status("正在获取漫画信息...")
        self.log_message(f"🔍 正在获取 {adapter.display_name} 漫画详情...")

        def worker():
            try:
                detail = adapter.fetch_manga_detail(url)
                self.cache_manga_detail(adapter, url, detail)
                if detail.latest_chapter or detail.update_time:
                    self.rank_detail_cache[detail.manga_url] = {
                        "latest_chapter": detail.latest_chapter,
                        "update_time": detail.update_time,
                    }
                self.update_ranking_detail(detail)
                chapter_summary = getattr(detail, "detail_hint", "") or "漫画详情已更新"
                self.log_message(f"✅ 已获取漫画信息: {detail.title}")
                self.log_message(f"📚 {chapter_summary}")
                self.set_status(f"{detail.title} 信息已更新")
            except RuntimeError as e:
                fallback_detail, fallback_source = self.get_fallback_manga_detail(adapter, url)
                if fallback_detail is not None:
                    self.update_ranking_detail(fallback_detail)
                    if fallback_source == "local":
                        self.log_message(f"⚠️ {adapter.display_name} 当前无法在线获取详情，已改为显示本地下载记录。", "warning")
                        self.set_status(f"{fallback_detail.title} 本地信息已显示")
                    else:
                        self.log_message(f"⚠️ {adapter.display_name} 当前无法在线获取详情，已改为显示缓存信息。", "warning")
                        self.set_status(f"{fallback_detail.title} 缓存信息已显示")
                    return
                if self.is_site_access_blocked_error(e):
                    self.handle_site_access_blocked_error(adapter.display_name, e)
                elif self.is_site_unreachable_error(e):
                    self.handle_site_unreachable_error(adapter, e)
                else:
                    self.log_message(f"❌ 获取漫画信息失败: {str(e)}", "error")
                    self.run_on_ui_thread(messagebox.showwarning, "获取失败", str(e))
            except Exception as e:
                fallback_detail, fallback_source = self.get_fallback_manga_detail(adapter, url)
                if fallback_detail is not None:
                    self.update_ranking_detail(fallback_detail)
                    if fallback_source == "local":
                        self.log_message(f"⚠️ {adapter.display_name} 当前无法在线获取详情，已改为显示本地下载记录。", "warning")
                        self.set_status(f"{fallback_detail.title} 本地信息已显示")
                    else:
                        self.log_message(f"⚠️ {adapter.display_name} 当前无法在线获取详情，已改为显示缓存信息。", "warning")
                        self.set_status(f"{fallback_detail.title} 缓存信息已显示")
                    return
                self.log_message(f"❌ 获取漫画信息失败: {str(e)}", "error")
                self.run_on_ui_thread(messagebox.showwarning, "获取失败", str(e))
            finally:
                self.is_fetching_manga_detail = False
                self.set_fetch_info_button_state(loading=False)

        threading.Thread(target=worker, daemon=True).start()

    def set_download_url(self, url):
        normalized_url = (url or "").strip()
        self.current_download_url = normalized_url
        self.download_url_var.set(normalized_url)

    def refresh_proxy_controls(self):
        adapter = self.current_adapter
        supports_manual_proxy = bool(getattr(adapter, "supports_manual_proxy", lambda: False)())
        self._syncing_proxy_controls = True
        try:
            if supports_manual_proxy:
                self.manual_proxy_enabled_var.set(adapter.has_manual_proxy())
                self.manual_proxy_url_var.set(adapter.get_manual_proxy_url())
            else:
                self.manual_proxy_enabled_var.set(False)
                self.manual_proxy_url_var.set("")
        finally:
            self._syncing_proxy_controls = False

        if hasattr(self, "proxy_toggle_btn"):
            self.proxy_toggle_btn.config(state=tk.NORMAL if supports_manual_proxy else tk.DISABLED)
        if hasattr(self, "proxy_apply_btn"):
            self.proxy_apply_btn.config(
                state=tk.NORMAL if supports_manual_proxy and not self.is_testing_connection else tk.DISABLED
            )
        if hasattr(self, "proxy_test_btn"):
            self.proxy_test_btn.config(
                state=tk.NORMAL if supports_manual_proxy and not self.is_testing_connection else tk.DISABLED,
                text="测试中..." if self.is_testing_connection else "测试连接",
            )
        if hasattr(self, "proxy_entry"):
            self.proxy_entry.config(
                state=tk.NORMAL if supports_manual_proxy and self.manual_proxy_enabled_var.get() else tk.DISABLED
            )

    def on_proxy_toggle(self):
        if self._syncing_proxy_controls:
            return

        enabled = bool(self.manual_proxy_enabled_var.get())
        self.proxy_entry.config(state=tk.NORMAL if enabled else tk.DISABLED)
        if enabled:
            self.set_status("已启用手动代理输入，请点击“应用代理”或直接开始请求。")
            return

        try:
            self.current_adapter.set_manual_proxy("")
            self.log_message(f"已关闭 {self.current_adapter.display_name} 手动代理。")
            self.set_status(f"{self.current_adapter.display_name} 已关闭手动代理")
        except Exception as e:
            self.log_message(f"关闭手动代理失败: {str(e)}", "warning")

    def apply_manual_proxy_settings(self, event=None, show_feedback=True):
        if self._syncing_proxy_controls:
            return True

        adapter = self.current_adapter
        supports_manual_proxy = bool(getattr(adapter, "supports_manual_proxy", lambda: False)())
        if not supports_manual_proxy:
            self.refresh_proxy_controls()
            return True

        enabled = bool(self.manual_proxy_enabled_var.get())
        proxy_text = (self.manual_proxy_url_var.get() or "").strip()

        if not enabled:
            try:
                adapter.set_manual_proxy("")
                self.proxy_entry.config(state=tk.DISABLED)
                if show_feedback:
                    self.log_message(f"已关闭 {adapter.display_name} 手动代理。")
                    self.set_status(f"{adapter.display_name} 已关闭手动代理")
                return True
            except Exception as e:
                self.log_message(f"关闭手动代理失败: {str(e)}", "warning")
                return False

        if not proxy_text:
            messagebox.showwarning("提示", "请输入代理地址，例如 127.0.0.1:7890 或 http://127.0.0.1:7890")
            return False

        try:
            adapter.set_manual_proxy(proxy_text)
            self.proxy_entry.config(state=tk.NORMAL)
            self.manual_proxy_url_var.set(adapter.get_manual_proxy_url())
            if show_feedback:
                self.log_message(f"已为 {adapter.display_name} 应用手动代理: {adapter.get_manual_proxy_url()}")
                self.set_status(f"{adapter.display_name} 手动代理已应用")
            return True
        except Exception as e:
            self.log_message(f"应用手动代理失败: {str(e)}", "warning")
            self.run_on_ui_thread(messagebox.showwarning, "代理设置失败", str(e))
            return False

    def run_connection_probe(self, adapter, target_url):
        session = requests.Session()
        adapter.configure_requests_session(session, for_image=False)
        headers = {
            "Referer": f"https://{adapter.supported_domains[0]}/",
            "Cache-Control": "no-cache",
            "Pragma": "no-cache",
        }
        with session.get(
            target_url,
            headers=headers,
            timeout=(8, 12),
            allow_redirects=True,
            stream=True,
        ) as response:
            return response.status_code, response.url

    def test_site_connection(self):
        if self.is_testing_connection:
            return

        current_url = (self.download_url_var.get() or self.current_download_url or "").strip()
        adapter = resolve_adapter_from_url(current_url, fallback_key=self.current_adapter.key) if current_url else self.current_adapter
        if current_url and adapter.key != self.current_adapter.key:
            self.set_active_adapter(adapter.key)

        if not self.apply_manual_proxy_settings(show_feedback=False):
            return

        target_url = self.get_connection_test_target(adapter)
        route_label = self.get_connection_route_label(adapter)
        self.is_testing_connection = True
        self.refresh_proxy_controls()
        self.set_status(f"正在测试 {adapter.display_name} 连通性...")
        self.log_message(f"🌐 正在测试 {adapter.display_name} 连通性...")
        self.log_message(f"目标地址: {target_url}")
        self.log_message(f"当前请求方式: {route_label}")

        def worker():
            try:
                status_code, final_url = self.run_connection_probe(adapter, target_url)
                if status_code >= 500:
                    self.log_message(f"⚠️ 已连到 {adapter.display_name}，但站点返回 HTTP {status_code}", "warning")
                    self.set_status(f"{adapter.display_name} 可连接，但站点返回异常")
                    self.run_on_ui_thread(
                        messagebox.showwarning,
                        "测试结果",
                        f"{adapter.display_name} 已可达，但站点返回了 HTTP {status_code}。\n\n这更像是站点临时异常，不是本地网络完全不通。\n最终地址: {final_url}",
                    )
                    return

                self.log_message(f"✅ 连通性测试通过: {adapter.display_name} 可访问 (HTTP {status_code})", "success")
                self.set_status(f"{adapter.display_name} 连通性正常")
                self.run_on_ui_thread(
                    messagebox.showinfo,
                    "测试结果",
                    f"{adapter.display_name} 当前可通过“{route_label}”访问。\n\nHTTP 状态: {status_code}\n最终地址: {final_url}",
                )
            except Exception as e:
                raw_message = self.normalize_log_message(str(e))
                troubleshooting = self.get_connection_troubleshooting_text(adapter)
                self.log_message(f"❌ 连通性测试失败: {raw_message or e}", "error")
                for line in troubleshooting.splitlines():
                    self.log_message(line, "warning")
                self.set_status(f"{adapter.display_name} 连通性测试失败")
                self.run_on_ui_thread(
                    messagebox.showwarning,
                    "测试失败",
                    f"{adapter.display_name} 当前无法通过“{route_label}”访问。\n\n建议按这个顺序排查：\n{troubleshooting}\n\n错误详情:\n{raw_message or e}",
                )
            finally:
                self.is_testing_connection = False
                self.run_on_ui_thread(self.refresh_proxy_controls)

    def get_selected_adapter(self):
        return get_adapter_by_display_name(self.site_var.get())

    def set_active_adapter(self, site_key, sync_site_var=True):
        previous_key = self.current_adapter.key if getattr(self, "current_adapter", None) else None
        self.current_adapter = get_adapter(site_key)
        if previous_key and previous_key != self.current_adapter.key:
            self.current_section_page = 1
            self.clear_ranking_results()
        if sync_site_var:
            self.site_var.set(self.current_adapter.display_name)
        self.refresh_proxy_controls()
        self.refresh_site_controls()

    def refresh_site_controls(self, show_log=True):
        self.section_options = self.get_adapter_section_options(self.current_adapter)
        self.theme_options = self.get_adapter_theme_options(self.current_adapter)
        has_discovery = self.adapter_has_discovery_sections()
        has_search = self.adapter_supports_search()

        if has_discovery or has_search:
            if has_discovery:
                section_names = list(self.section_options.keys())
                current_section = self.homepage_section_var.get()
                self.homepage_section_combo.config(state="readonly", values=section_names)
                if current_section not in section_names:
                    self.homepage_section_var.set(section_names[0])
            else:
                self.homepage_section_var.set(self.current_adapter.discovery_placeholder)
                self.homepage_section_combo.config(
                    state=tk.DISABLED,
                    values=[self.current_adapter.discovery_placeholder],
                )

            self.refresh_theme_filter_controls()

            if self.current_view_supports_search():
                self.search_entry.config(state=tk.NORMAL)
            else:
                self.search_query_var.set("")
                self.search_entry.config(state=tk.DISABLED)

            self.section_page_var.set(self.get_default_section_page_text())
            self.set_ranking_buttons_state(loading=False)
        else:
            self.homepage_section_var.set(self.current_adapter.discovery_placeholder)
            self.homepage_section_combo.config(
                state=tk.DISABLED,
                values=[self.current_adapter.discovery_placeholder],
            )
            self.theme_options = {}
            self.homepage_theme_var.set("全部题材")
            self.homepage_theme_combo.config(
                state=tk.DISABLED,
                values=["全部题材"],
            )
            self.search_query_var.set("")
            self.search_entry.config(state=tk.DISABLED)
            self.search_btn.config(state=tk.DISABLED)
            self.clear_search_btn.config(state=tk.DISABLED)
            self.check_updates_btn.config(state=tk.DISABLED, text="检查更新")
            self.current_section_page = 1
            self.clear_ranking_results()
            self.prev_page_btn.config(state=tk.DISABLED)
            self.next_page_btn.config(state=tk.DISABLED)
            self.refresh_rank_btn.config(state=tk.DISABLED)
            self.download_rank_btn.config(state=tk.DISABLED)
            self.section_page_var.set("仅支持手动输入 URL")

        if show_log and self.current_adapter.status_hint:
            self.log_message(f"站点已切换到 {self.current_adapter.display_name}。{self.current_adapter.status_hint}")
        self.set_status(f"当前站点: {self.current_adapter.display_name}")

    def on_site_change(self, event=None):
        selected_adapter = self.get_selected_adapter()
        has_sections = bool(self.get_adapter_section_options(selected_adapter))
        should_refresh_search = bool(self.get_active_search_query()) and bool(getattr(selected_adapter, "supports_search", False))
        self.current_section_page = 1
        self.clear_ranking_results()
        self.clear_download_url_on_next_refresh = has_sections or should_refresh_search
        self.set_download_url("")
        self.set_active_adapter(selected_adapter.key, sync_site_var=False)
        if has_sections or should_refresh_search:
            self.refresh_rankings()

    def refresh_rankings(self):
        search_query = self.get_active_search_query()
        has_discovery = self.adapter_has_discovery_sections()
        has_search = self.adapter_supports_search()
        section_key = self.section_options.get(self.homepage_section_var.get(), "rank") if has_discovery else ""
        is_local_library = self.is_local_library_section(section_key)
        supports_current_search = self.current_view_supports_search(section_key)
        adapter = self.current_adapter

        if search_query and not supports_current_search:
            self.log_message(f"⚠️ {adapter.display_name} 暂不支持站内搜索", "warning")
            self.set_status(f"{adapter.display_name} 暂不支持站内搜索")
            self.set_ranking_buttons_state(loading=False)
            return

        if not search_query and not has_discovery:
            if has_search:
                self.section_page_var.set(self.get_default_section_page_text())
                self.set_status(f"{adapter.display_name} 暂不支持首页浏览，请输入关键词搜索")
            else:
                self.set_status(f"{adapter.display_name} 暂不支持首页浏览")
            self.set_ranking_buttons_state(loading=False)
            return

        if not is_local_library and not self.apply_manual_proxy_settings(show_feedback=False):
            self.set_ranking_buttons_state(loading=False)
            return

        clear_download_url = self.clear_download_url_on_next_refresh
        self.clear_download_url_on_next_refresh = False
        section_label = self.get_section_display_name(section_key) if has_discovery else self.current_adapter.discovery_placeholder
        theme_key = self.theme_options.get(self.homepage_theme_var.get(), "") if has_discovery and self.section_supports_theme_filter(section_key) else ""
        theme_label = self.homepage_theme_var.get().strip() if theme_key else ""
        page = self.current_section_page
        discovery_label = f"{section_label} · {theme_label}" if theme_label else section_label
        if is_local_library:
            target_label = f"{section_label}搜索“{search_query}”" if search_query else section_label
            list_label = "本地漫画库"
        else:
            target_label = f"搜索“{search_query}”" if search_query else discovery_label
            list_label = "搜索结果" if search_query else "首页列表"
        self.ranking_request_id += 1
        request_id = self.ranking_request_id
        self.log_message(f"🔍 正在刷新{list_label}: {target_label} 第 {page} 页...")
        self.set_status(f"正在刷新{target_label}...")
        self.refresh_theme_filter_controls()
        self.set_ranking_buttons_state(loading=True)

        def worker():
            try:
                cards = (
                    self.fetch_local_library_cards(site_key=adapter.key, page=page, keyword=search_query)
                    if is_local_library else
                    adapter.fetch_search_cards(search_query, page=page)
                    if search_query else
                    adapter.fetch_section_cards(section_key, page=page, theme=theme_key)
                )
                if self._closing or request_id != self.ranking_request_id:
                    return
                if not cards and page > 1:
                    self.current_section_page = max(1, page - 1)
                    self.log_message(f"⚠️ {target_label}第 {page} 页暂无内容，已返回上一页", "warning")
                    self.update_section_pagination_ui(section_key, self.current_section_page, has_cards=False, search_query=search_query)
                    self.set_ranking_buttons_state(loading=False)
                    self.run_on_ui_thread(self.refresh_rankings)
                    return
                self.current_homepage_cards = cards
                self.rank_cards = cards
                self.populate_ranking_tree(cards)
                if not cards:
                    self.update_ranking_detail(None)
                self.update_section_pagination_ui(section_key, page, has_cards=bool(cards), search_query=search_query)
                if cards:
                    def select_first_card():
                        if self._closing or not self.rank_tree.winfo_exists():
                            return
                        if clear_download_url:
                            self.skip_next_ranking_selection_url_sync = True
                        self.rank_tree.selection_set("0")
                        self.rank_tree.focus("0")
                        self.rank_tree.see("0")
                        self.on_ranking_select()
                        if clear_download_url:
                            self.root.after_idle(self.clear_pending_ranking_selection_url_sync)

                    self.run_on_ui_thread(select_first_card)
                elif search_query:
                    self.log_message(f"⚠️ 未找到与“{search_query}”相关的漫画", "warning")

                self.log_message(f"✅ 已加载{target_label}第 {page} 页，共 {len(cards)} 部漫画")
                if not self.is_downloading:
                    self.set_status(f"{target_label}已更新")
            except Exception as e:
                if self._closing or request_id != self.ranking_request_id:
                    return
                if isinstance(e, RuntimeError) and self.is_site_access_blocked_error(e):
                    self.handle_site_access_blocked_error(adapter.display_name, e)
                elif isinstance(e, RuntimeError) and self.is_site_unreachable_error(e):
                    self.handle_site_unreachable_error(adapter, e)
                else:
                    self.log_message(f"❌ 刷新{target_label}失败: {str(e)}", "error")
                self.current_homepage_cards = []
                self.rank_cards = []
                self.populate_ranking_tree([])
                self.update_ranking_detail(None)
                self.update_section_pagination_ui(section_key, page, has_cards=False, search_query=search_query)
                if not self.is_downloading:
                    self.set_status(f"{target_label}刷新失败")
            finally:
                if not self._closing and request_id == self.ranking_request_id:
                    self.set_ranking_buttons_state(loading=False)

        thread = threading.Thread(target=worker, daemon=True)
        thread.start()

    def on_homepage_section_change(self, event=None):
        if self.get_active_search_query():
            self.search_query_var.set("")
        self.current_section_page = 1
        self.refresh_theme_filter_controls()
        self.refresh_rankings()

    def on_homepage_theme_change(self, event=None):
        if self.get_active_search_query():
            self.search_query_var.set("")
        self.current_section_page = 1
        self.refresh_theme_filter_controls()
        self.refresh_rankings()

    def load_previous_section_page(self):
        if self.current_section_page > 1:
            self.current_section_page -= 1
            self.refresh_rankings()

    def load_next_section_page(self):
        self.current_section_page += 1
        self.refresh_rankings()

    def get_selected_ranking_card(self):
        selection = self.rank_tree.selection()
        if not selection:
            return None
        try:
            index = int(selection[0])
        except (TypeError, ValueError):
            return None
        if 0 <= index < len(self.rank_cards):
            return self.rank_cards[index]
        return None

    def on_ranking_select(self, event=None):
        card = self.get_selected_ranking_card()
        if not card:
            return
        if self.skip_next_ranking_selection_url_sync:
            self.skip_next_ranking_selection_url_sync = False
            self.update_ranking_detail(card)
            return
        if getattr(card, "manga_url", "").strip():
            self.set_download_url(card.manga_url)
        self.update_ranking_detail(card)
        self.log_message(f"已选中列表漫画: {card.title}")

    def download_selected_ranking(self):
        if self.is_downloading:
            messagebox.showwarning("提示", "当前已有下载任务，请先暂停或停止后再试。")
            return

        card = self.get_selected_ranking_card()
        if not card:
            messagebox.showwarning("提示", "请先在列表中选择一部漫画。")
            return

        if not (card.manga_url or "").strip():
            messagebox.showwarning("提示", "这个本地条目没有保存原始漫画链接，暂时无法直接继续下载。")
            return

        self.set_download_url(card.manga_url)
        self.log_message(f"🎯 从列表启动下载: {card.title}")
        self.start_download(card.manga_url)

    def on_ranking_double_click(self, event=None):
        card = self.get_selected_ranking_card()
        if card:
            self.download_selected_ranking()
        
    def start_download(self, url=None):
        """开始下载"""
        if not url:
            url = (self.download_url_var.get() or "").strip()
            if not url:
                card = self.get_selected_ranking_card()
                if card:
                    url = card.manga_url
                else:
                    url = (self.current_download_url or "").strip()

        if not url:
            messagebox.showwarning("警告", "请先输入漫画链接，或在首页列表中选择一部漫画")
            return

        adapter = resolve_adapter_from_url(url, fallback_key=self.get_selected_adapter().key)
        if not adapter.matches_url(url):
            supported_sites = "、".join(get_site_display_names())
            messagebox.showwarning("警告", f"暂时无法识别该链接所属站点。当前已接入站点: {supported_sites}")
            return

        if not adapter.supports_download:
            messagebox.showinfo(
                "提示",
                f"{adapter.display_name} 已预留接入入口，但下载逻辑还没完成。\n\n你现在可以继续收集这个站的章节接口和图片规则，后面我们再把它补进去。"
            )
            return

        self.download_site_key = adapter.key
        self.set_active_adapter(adapter.key)
        if not self.apply_manual_proxy_settings(show_feedback=False):
            self.active_download_url = ""
            return
        self.active_download_url = url
        self.active_download_metadata = None
        self.set_download_url(url)
            
        # 重置停止事件
        self.stop_event.clear()
        self.pause_event.set()
        self.is_paused = False
        
        # 禁用下载按钮，启用控制按钮
        self.is_downloading = True
        self.update_control_buttons(downloading=True, paused=False)
        self.set_progress_style('Download.Horizontal.TProgressbar')
        self.set_progress(0)
        self.set_status("准备开始下载...")
        
        # 配置代理
        proxy_pool.enabled = self.proxy_var.get()
        
        # 在新线程中开始下载
        self.current_thread = threading.Thread(target=self.download_manga, args=(url, adapter.key))
        self.current_thread.daemon = True
        self.current_thread.start()
        
    def stop_download(self):
        """停止下载"""
        self.is_downloading = False
        self.stop_event.set()  # 设置停止事件
        self.log_message("正在停止下载...")
        if self.download_site_key == "manhuagui":
            self.log_message("漫画柜当前若正在请求网页，通常要等这次网络请求超时后才会完全停止。", "warning")
        self.set_status("正在停止...")
        self.set_progress_style('Danger.Horizontal.TProgressbar')
        
        # 强制关闭executor以立即停止所有线程
        if self.executor:
            try:
                self.executor.shutdown(wait=False, cancel_futures=True)
                self.log_message("已强制停止所有下载线程")
            except Exception as e:
                self.log_message(f"停止线程时出错: {str(e)}", "error")
        
    def pause_download(self):
        """暂停下载"""
        if self.is_downloading and not self.is_paused:
            self.is_paused = True
            self.pause_event.clear()  # 清除暂停事件，暂停下载
            self.log_message("⏸️ 下载已暂停")
            self.set_status("下载已暂停")
            self.set_progress_style('Warning.Horizontal.TProgressbar')
            # 更新按钮状态
            self.update_control_buttons(downloading=True, paused=True)
            
    def resume_download(self):
        """恢复下载"""
        if self.is_downloading and self.is_paused:
            self.is_paused = False
            self.pause_event.set()  # 设置暂停事件，恢复下载
            self.log_message("▶️ 下载已恢复")
            self.set_status("下载中...")
            self.set_progress_style('Download.Horizontal.TProgressbar')
            # 更新按钮状态
            self.update_control_buttons(downloading=True, paused=False)
        
    def clear_log(self):
        """清空日志"""
        try:
            while True:
                self.log_queue.get_nowait()
        except queue.Empty:
            pass
        self.log_text.delete(1.0, tk.END)

    def load_manga_detail_cache(self):
        try:
            if os.path.exists(self.manga_detail_cache_file):
                with open(self.manga_detail_cache_file, 'r', encoding='utf-8') as f:
                    data = json.load(f)
                    if isinstance(data, dict):
                        return data
        except Exception:
            pass
        return {}

    def save_manga_detail_cache(self):
        try:
            with open(self.manga_detail_cache_file, 'w', encoding='utf-8') as f:
                json.dump(self.saved_manga_detail_cache, f, ensure_ascii=False, indent=2)
        except Exception as e:
            if hasattr(self, "log_queue"):
                self.log_message(f"保存漫画信息缓存失败: {str(e)}", "warning")

    def cache_manga_detail(self, adapter, source_url, detail):
        if not detail:
            return

        cache_key = adapter.get_manga_cache_key(source_url or detail.manga_url)
        payload = detail.to_cache_dict() if hasattr(detail, "to_cache_dict") else {
            "title": getattr(detail, "title", ""),
            "manga_url": getattr(detail, "manga_url", ""),
            "section": getattr(detail, "section", ""),
            "cover_url": getattr(detail, "cover_url", ""),
            "latest_chapter": getattr(detail, "latest_chapter", ""),
            "update_time": getattr(detail, "update_time", "-"),
            "detail_hint": getattr(detail, "detail_hint", ""),
            "detail_section_label": getattr(detail, "detail_section_label", ""),
            "chapter_count": getattr(detail, "chapter_count", 0),
            "start_chapter_title": getattr(detail, "start_chapter_title", ""),
        }
        payload["manga_url"] = (source_url or detail.manga_url or "").strip()
        self.saved_manga_detail_cache[cache_key] = payload
        self.save_manga_detail_cache()

    def get_cached_manga_detail(self, adapter, source_url):
        cache_key = adapter.get_manga_cache_key(source_url)
        payload = self.saved_manga_detail_cache.get(cache_key)
        if not payload:
            return None
        try:
            return MangaDetail(**payload)
        except Exception:
            return None

    def get_download_workspace_dir(self):
        return os.path.dirname(os.path.abspath(__file__))

    def get_manga_metadata_path(self, root_dir):
        return os.path.join(root_dir, self.library_metadata_file_name)

    def get_library_scan_excluded_dirs(self):
        return {
            "__pycache__", ".git", ".venv", "build", "build_pyinstaller",
            "dist", "dist_build", "release",
        }

    def count_image_files_in_dir(self, directory_path):
        count = 0
        try:
            for entry in os.scandir(directory_path):
                if entry.is_file() and entry.name.lower().endswith((".jpg", ".jpeg", ".png", ".webp")):
                    count += 1
        except Exception:
            return 0
        return count

    def get_known_cover_url_for_download(self, adapter, source_url):
        source_url = (source_url or "").strip()
        if not source_url:
            return ""

        cached_detail = self.get_cached_manga_detail(adapter, source_url)
        if cached_detail and cached_detail.cover_url:
            return cached_detail.cover_url

        for card in self.rank_cards:
            if getattr(card, "manga_url", "").strip() == source_url and getattr(card, "cover_url", "").strip():
                return card.cover_url

        if self.current_download_url == source_url and self.current_cover_url:
            return self.current_cover_url

        return ""

    def build_library_title_key(self, title):
        normalized = sanitize_filename(str(title or "")).strip().lower()
        if normalized:
            return normalized
        return str(title or "").strip().lower()

    def extract_site_key_from_cache_key(self, cache_key):
        normalized_key = str(cache_key or "").strip()
        if ":" not in normalized_key:
            return ""
        return normalized_key.split(":", 1)[0].strip()

    def find_cached_library_identity_by_title(self, manga_title, preferred_site_key=""):
        target_title_key = self.build_library_title_key(manga_title)
        if not target_title_key:
            return None

        matches = []
        for cache_key, payload in (self.saved_manga_detail_cache or {}).items():
            if not isinstance(payload, dict):
                continue
            cached_title_key = self.build_library_title_key(payload.get("title"))
            if cached_title_key != target_title_key:
                continue

            site_key = self.extract_site_key_from_cache_key(cache_key)
            site_name = get_adapter(site_key).display_name if site_key else ""
            matches.append({
                "site_key": site_key,
                "site_name": site_name,
                "manga_url": str(payload.get("manga_url") or "").strip(),
                "cover_url": str(payload.get("cover_url") or "").strip(),
                "latest_chapter": str(payload.get("latest_chapter") or "").strip(),
                "chapter_count": int(payload.get("chapter_count") or 0),
            })

        if not matches:
            return None

        def sort_key(item):
            return (
                0 if item.get("cover_url") else 1,
                0 if item.get("manga_url") else 1,
                -int(item.get("chapter_count") or 0),
            )

        matches.sort(key=sort_key)
        preferred_site_key = (preferred_site_key or "").strip()
        if preferred_site_key:
            preferred_matches = [item for item in matches if item.get("site_key") == preferred_site_key]
            if preferred_matches:
                return preferred_matches[0]

        unique_site_keys = {item.get("site_key") for item in matches if item.get("site_key")}
        if len(matches) == 1 or len(unique_site_keys) <= 1:
            return matches[0]
        return None

    def infer_site_key_from_chapter_dirs(self, chapter_dirs):
        numeric_prefixes = []
        for dir_name in chapter_dirs or []:
            prefix, _, _ = str(dir_name or "").partition("_")
            if not prefix.isdigit():
                continue
            numeric_prefixes.append(prefix)

        if any(len(prefix) >= 6 for prefix in numeric_prefixes):
            return "manhuagui"
        if any(int(prefix) == 0 for prefix in numeric_prefixes):
            return "baozimh"
        return ""

    def find_local_library_cover_path(self, root_dir):
        resolved_root_dir = (root_dir or "").strip()
        if not resolved_root_dir or not os.path.isdir(resolved_root_dir):
            return ""

        chapter_dirs = []
        try:
            for entry in os.scandir(resolved_root_dir):
                if entry.is_dir() and self.is_final_chapter_dir_name(entry.name):
                    chapter_dirs.append((entry.name, entry.path))
        except Exception:
            return ""

        chapter_dirs.sort(key=lambda item: item[0])
        for _, chapter_dir in chapter_dirs:
            image_files = self.list_exportable_image_files(chapter_dir)
            if image_files:
                return image_files[0]
        return ""

    def enrich_local_library_entry_identity(self, entry, preferred_site_key=""):
        if not isinstance(entry, dict):
            return None

        enriched = dict(entry)
        preferred_site_key = (preferred_site_key or "").strip()
        current_site_key = (enriched.get("site_key") or "").strip()
        root_dir = (enriched.get("root_dir") or "").strip()
        manga_title = str(enriched.get("manga_title") or os.path.basename(root_dir.rstrip("\\/")) or "本地漫画")

        guessed_site_key = ""
        if root_dir and os.path.isdir(root_dir):
            chapter_dir_names = []
            try:
                for item in os.scandir(root_dir):
                    if item.is_dir() and self.is_final_chapter_dir_name(item.name):
                        chapter_dir_names.append(item.name)
            except Exception:
                chapter_dir_names = []
            guessed_site_key = self.infer_site_key_from_chapter_dirs(chapter_dir_names)

        cached_identity = self.find_cached_library_identity_by_title(
            manga_title,
            preferred_site_key=current_site_key or guessed_site_key or preferred_site_key,
        )

        resolved_site_key = (cached_identity or {}).get("site_key") or guessed_site_key or current_site_key
        if preferred_site_key:
            if not resolved_site_key or resolved_site_key != preferred_site_key:
                return None

        enriched["manga_title"] = manga_title
        enriched["site_key"] = resolved_site_key
        if cached_identity:
            enriched["site_name"] = cached_identity.get("site_name") or enriched.get("site_name") or ""
            if not (enriched.get("manga_url") or "").strip():
                enriched["manga_url"] = cached_identity.get("manga_url") or ""
            if not (enriched.get("cover_url") or "").strip():
                enriched["cover_url"] = cached_identity.get("cover_url") or ""
        if not enriched.get("site_name"):
            enriched["site_name"] = get_adapter(resolved_site_key).display_name if resolved_site_key else "未知站点（旧下载）"

        enriched["_local_cover_path"] = self.find_local_library_cover_path(root_dir)
        return enriched

    def compact_chapter_info(self, chapter):
        return {
            "order": chapter.get("order"),
            "slug": str(chapter.get("slug") or ""),
            "title": str(chapter.get("title") or ""),
            "updated_at": str(chapter.get("updated_at") or ""),
        }

    def build_downloaded_chapter_record(self, chapter, dir_name, image_count=0):
        title = ""
        prefix = ""
        if "_" in dir_name:
            prefix, title = dir_name.split("_", 1)
        else:
            title = dir_name
            prefix = dir_name

        title = title or (chapter.get("title") if chapter else "") or dir_name
        slug = str(chapter.get("slug") or "") if chapter else (prefix if prefix.isdigit() else "")
        order = chapter.get("order") if chapter else None
        display_order = order + 1 if isinstance(order, int) else None

        return {
            "order": display_order,
            "slug": slug,
            "title": title,
            "updated_at": str((chapter or {}).get("updated_at") or ""),
            "dir_name": dir_name,
            "image_count": int(image_count or 0),
        }

    def build_downloaded_chapter_records_from_disk(self, root_dir, known_chapters):
        records = []
        final_dirs = []
        try:
            for entry in os.scandir(root_dir):
                if entry.is_dir() and self.is_final_chapter_dir_name(entry.name):
                    final_dirs.append((entry.name, entry.path, self.count_image_files_in_dir(entry.path)))
        except Exception:
            return records

        final_dirs.sort(key=lambda item: item[0])
        normalized_known_chapters = list(known_chapters or [])
        used_indices = set()

        for dir_name, dir_path, image_count in final_dirs:
            prefix, _, title_part = dir_name.partition("_")
            safe_dir_title = sanitize_filename(title_part or dir_name)
            matched_index = None
            matched_chapter = None

            if prefix.isdigit():
                prefix_value = int(prefix)
                for index, chapter in enumerate(normalized_known_chapters):
                    if index in used_indices:
                        continue
                    order = chapter.get("order")
                    slug = str(chapter.get("slug") or "")
                    if isinstance(order, int) and order + 1 == prefix_value:
                        matched_index = index
                        matched_chapter = chapter
                        break
                    if slug.isdigit() and int(slug) == prefix_value:
                        matched_index = index
                        matched_chapter = chapter
                        break

            if matched_chapter is None and safe_dir_title:
                for index, chapter in enumerate(normalized_known_chapters):
                    if index in used_indices:
                        continue
                    if sanitize_filename(str(chapter.get("title") or "")) == safe_dir_title:
                        matched_index = index
                        matched_chapter = chapter
                        break

            if matched_index is not None:
                used_indices.add(matched_index)

            records.append(self.build_downloaded_chapter_record(matched_chapter, dir_name, image_count))

        records.sort(key=lambda item: (
            item.get("order") is None,
            item.get("order") if item.get("order") is not None else item.get("dir_name", ""),
        ))
        return records

    def build_active_download_metadata(self, adapter, source_url, manga_title, root_dir, all_chapters, start_order, start_chapter_title):
        known_chapters = [self.compact_chapter_info(chapter) for chapter in all_chapters]
        downloaded_chapters = self.build_downloaded_chapter_records_from_disk(root_dir, known_chapters)
        latest_known = known_chapters[-1] if known_chapters else {}
        now_text = time.strftime("%Y-%m-%d %H:%M:%S")
        cover_url = self.get_known_cover_url_for_download(adapter, source_url)
        last_downloaded = downloaded_chapters[-1] if downloaded_chapters else {}

        return {
            "schema_version": 1,
            "site_key": adapter.key,
            "site_name": adapter.display_name,
            "manga_title": str(manga_title or ""),
            "manga_url": (source_url or "").strip(),
            "root_dir": root_dir,
            "cover_url": cover_url,
            "total_chapters": len(known_chapters),
            "start_chapter_order": max(int(start_order) + 1, 1),
            "start_chapter_title": start_chapter_title or "",
            "latest_known_chapter_title": latest_known.get("title") or "-",
            "latest_known_update_time": self.format_updated_at(latest_known.get("updated_at")),
            "downloaded_chapter_count": len(downloaded_chapters),
            "last_downloaded_chapter_title": last_downloaded.get("title") or "",
            "last_downloaded_chapter_order": last_downloaded.get("order"),
            "downloaded_chapters": downloaded_chapters,
            "completed": False,
            "update_check_status": "",
            "update_available_count": 0,
            "update_last_checked_at": "",
            "update_last_error": "",
            "created_at": now_text,
            "saved_at": now_text,
            "_known_chapters": known_chapters,
        }

    def save_active_download_metadata(self, mark_completed=False):
        metadata = self.active_download_metadata
        if not metadata:
            return

        root_dir = (metadata.get("root_dir") or "").strip()
        if not root_dir:
            return

        try:
            os.makedirs(root_dir, exist_ok=True)
        except Exception:
            return

        known_chapters = metadata.get("_known_chapters") or []
        downloaded_chapters = self.build_downloaded_chapter_records_from_disk(root_dir, known_chapters)
        last_downloaded = downloaded_chapters[-1] if downloaded_chapters else {}

        metadata["downloaded_chapters"] = downloaded_chapters
        metadata["downloaded_chapter_count"] = len(downloaded_chapters)
        metadata["last_downloaded_chapter_title"] = last_downloaded.get("title") or ""
        metadata["last_downloaded_chapter_order"] = last_downloaded.get("order")
        metadata["saved_at"] = time.strftime("%Y-%m-%d %H:%M:%S")
        metadata["completed"] = bool(mark_completed or metadata.get("completed"))

        payload = {key: value for key, value in metadata.items() if not key.startswith("_")}
        try:
            with open(self.get_manga_metadata_path(root_dir), "w", encoding="utf-8") as file_obj:
                json.dump(payload, file_obj, ensure_ascii=False, indent=2)
        except Exception as exc:
            self.log_message(f"保存本地漫画元数据失败: {str(exc)}", "warning")

    def save_library_entry_metadata(self, entry):
        if not isinstance(entry, dict):
            return False

        root_dir = (entry.get("root_dir") or "").strip()
        if not root_dir:
            return False

        try:
            os.makedirs(root_dir, exist_ok=True)
        except Exception:
            return False

        payload = {}
        for key, value in entry.items():
            if key == "root_dir" or str(key).startswith("_"):
                continue
            payload[key] = value

        if "schema_version" not in payload:
            payload["schema_version"] = 1

        try:
            with open(self.get_manga_metadata_path(root_dir), "w", encoding="utf-8") as file_obj:
                json.dump(payload, file_obj, ensure_ascii=False, indent=2)
            return True
        except Exception as exc:
            self.log_message(f"保存本地漫画元数据失败: {str(exc)}", "warning")
            return False

    def load_manga_library_metadata(self, root_dir):
        metadata_path = self.get_manga_metadata_path(root_dir)
        if not os.path.exists(metadata_path):
            return None
        try:
            with open(metadata_path, "r", encoding="utf-8") as file_obj:
                payload = json.load(file_obj)
        except Exception:
            return None

        if not isinstance(payload, dict):
            return None

        payload["root_dir"] = root_dir
        payload["downloaded_chapters"] = list(payload.get("downloaded_chapters") or [])
        payload["downloaded_chapter_count"] = int(payload.get("downloaded_chapter_count") or len(payload["downloaded_chapters"]) or 0)
        payload["total_chapters"] = int(payload.get("total_chapters") or payload["downloaded_chapter_count"] or 0)
        payload["update_check_status"] = str(payload.get("update_check_status") or "")
        payload["update_available_count"] = int(payload.get("update_available_count") or 0)
        payload["update_last_checked_at"] = str(payload.get("update_last_checked_at") or "")
        payload["update_last_error"] = str(payload.get("update_last_error") or "")
        return payload

    def build_local_library_entry_from_fallback(self, directory_path, site_key=""):
        chapter_dirs = []
        try:
            for entry in os.scandir(directory_path):
                if entry.is_dir() and self.is_final_chapter_dir_name(entry.name):
                    chapter_dirs.append(entry.name)
        except Exception:
            return None

        if not chapter_dirs:
            return None

        chapter_dirs.sort()
        latest_dir_name = chapter_dirs[-1]
        latest_title = latest_dir_name.split("_", 1)[1] if "_" in latest_dir_name else latest_dir_name
        modified_at = datetime.fromtimestamp(os.path.getmtime(directory_path)).strftime("%Y-%m-%d %H:%M:%S")

        entry = {
            "schema_version": 0,
            "site_key": "",
            "site_name": "",
            "manga_title": os.path.basename(directory_path.rstrip("\\/")) or "本地漫画",
            "manga_url": "",
            "root_dir": directory_path,
            "cover_url": "",
            "total_chapters": len(chapter_dirs),
            "downloaded_chapter_count": len(chapter_dirs),
            "last_downloaded_chapter_title": latest_title,
            "last_downloaded_chapter_order": None,
            "downloaded_chapters": [
                self.build_downloaded_chapter_record(None, dir_name, self.count_image_files_in_dir(os.path.join(directory_path, dir_name)))
                for dir_name in chapter_dirs
            ],
            "completed": True,
            "created_at": modified_at,
            "saved_at": modified_at,
        }
        return self.enrich_local_library_entry_identity(entry, preferred_site_key=site_key)

    def iter_local_library_entries(self, site_key=""):
        base_dir = self.get_download_workspace_dir()
        entries = []
        excluded_dirs = self.get_library_scan_excluded_dirs()

        try:
            dir_entries = list(os.scandir(base_dir))
        except Exception:
            return entries

        for entry in dir_entries:
            if not entry.is_dir():
                continue
            if entry.name in excluded_dirs:
                continue
            if entry.name.startswith(".") and not self.looks_like_manga_download_dir(entry.path):
                continue

            disk_has_chapter_dirs = self.looks_like_manga_download_dir(entry.path)
            metadata = self.load_manga_library_metadata(entry.path)
            if metadata:
                if not disk_has_chapter_dirs:
                    continue
                metadata_site_key = (metadata.get("site_key") or "").strip()
                if site_key and metadata_site_key and metadata_site_key != site_key:
                    continue
                fallback_site_key = metadata_site_key or site_key
                fallback = self.build_local_library_entry_from_fallback(entry.path, site_key=fallback_site_key)
                if fallback is not None:
                    metadata["site_key"] = metadata_site_key or fallback.get("site_key") or ""
                    metadata["site_name"] = metadata.get("site_name") or fallback.get("site_name") or self.current_adapter.display_name
                    metadata["downloaded_chapters"] = list(fallback.get("downloaded_chapters") or [])
                    metadata["downloaded_chapter_count"] = int(fallback.get("downloaded_chapter_count") or 0)
                    metadata["last_downloaded_chapter_title"] = fallback.get("last_downloaded_chapter_title") or metadata.get("last_downloaded_chapter_title") or ""
                    metadata["last_downloaded_chapter_order"] = fallback.get("last_downloaded_chapter_order")
                    metadata["saved_at"] = metadata.get("saved_at") or fallback.get("saved_at") or metadata.get("created_at") or ""
                    metadata["total_chapters"] = max(
                        int(metadata.get("total_chapters") or 0),
                        int(metadata.get("downloaded_chapter_count") or 0),
                    )

                if site_key and not metadata_site_key:
                    fallback = self.build_local_library_entry_from_fallback(entry.path, site_key=site_key)
                    if fallback is not None:
                        metadata["site_key"] = fallback.get("site_key") or metadata.get("site_key") or ""
                        metadata["site_name"] = fallback.get("site_name") or metadata.get("site_name") or ""
                    else:
                        continue

                finalized_metadata = self.enrich_local_library_entry_identity(metadata, preferred_site_key=site_key)
                if finalized_metadata is not None:
                    entries.append(finalized_metadata)
                continue

            if not disk_has_chapter_dirs:
                continue
            fallback = self.build_local_library_entry_from_fallback(entry.path, site_key=site_key)
            if fallback is not None:
                entries.append(fallback)

        def sort_key(item):
            saved_at = str(item.get("saved_at") or item.get("created_at") or "")
            try:
                return self.parse_resume_timestamp(saved_at) or datetime.fromtimestamp(0)
            except Exception:
                return datetime.fromtimestamp(0)

        entries.sort(key=sort_key, reverse=True)
        return entries

    def fetch_local_library_cards(self, site_key="", page=1, keyword=""):
        normalized_keyword = (keyword or "").strip().lower()
        library_entries = self.iter_local_library_entries(site_key=site_key)
        if normalized_keyword:
            library_entries = [
                item for item in library_entries
                if normalized_keyword in str(item.get("manga_title") or "").lower()
            ]

        page = max(int(page or 1), 1)
        page_size = max(int(self.local_library_page_size), 1)
        start = (page - 1) * page_size
        end = start + page_size
        cards = []

        for item in library_entries[start:end]:
            downloaded_count = int(item.get("downloaded_chapter_count") or 0)
            total_chapters = int(item.get("total_chapters") or 0)
            root_dir = (item.get("root_dir") or "").strip()
            last_title = item.get("last_downloaded_chapter_title") or "-"
            site_name = item.get("site_name") or self.current_adapter.display_name
            display_cover = str(item.get("cover_url") or item.get("_local_cover_path") or "")
            detail_lines = [
                f"站点: {site_name}",
                f"已下载: {downloaded_count} / {total_chapters or '?'} 章",
            ]
            detail_lines.extend(self.get_library_update_status_lines(item))
            if root_dir:
                detail_lines.append(f"目录: {root_dir}")

            card = HomepageMangaCard(
                section="本地已下载",
                title=str(item.get("manga_title") or "本地漫画"),
                manga_url=str(item.get("manga_url") or ""),
                chapterlist_url=str(item.get("manga_url") or ""),
                cover_url=display_cover,
                latest_chapter=str(last_title),
                update_time=str(item.get("saved_at") or item.get("created_at") or "-"),
            )
            card.detail_section_label = f"来源: 本地漫画库 · 站点: {site_name}"
            card.detail_hint = "\n".join(detail_lines)
            card.disable_detail_enrich = True
            card.local_root_dir = root_dir
            card.local_site_key = str(item.get("site_key") or "")
            card.local_library_entry = dict(item)
            cards.append(card)

        return cards

    def is_final_chapter_dir_name(self, name):
        return bool(re.match(r"^\d+_.+", name or ""))

    def is_temp_chapter_dir_name(self, name):
        return bool(re.match(r"^\.下载中_\d+_.+", name or ""))

    def looks_like_manga_download_dir(self, directory_path):
        try:
            for entry in os.scandir(directory_path):
                if entry.is_dir() and (
                    self.is_final_chapter_dir_name(entry.name)
                    or self.is_temp_chapter_dir_name(entry.name)
                ):
                    return True
        except Exception:
            return False
        return False

    def parse_resume_timestamp(self, value):
        if not value:
            return None
        try:
            return datetime.strptime(str(value), "%Y-%m-%d %H:%M:%S")
        except Exception:
            return None

    def find_local_manga_root_dir(self, adapter, source_url):
        if adapter.key != "manhuagui":
            return None

        source_cache_key = adapter.get_manga_cache_key(source_url)
        active_url = (self.active_download_url or self.current_download_url or "").strip()
        if (
            self.active_download_root_dir
            and active_url
            and source_cache_key == adapter.get_manga_cache_key(active_url)
            and os.path.isdir(self.active_download_root_dir)
        ):
            return self.active_download_root_dir

        resume_state = self.load_download_state() or {}
        resume_url = (resume_state.get("url") or "").strip()
        resume_matches = (
            resume_state.get("site_key") == adapter.key
            and resume_url
            and adapter.get_manga_cache_key(resume_url) == source_cache_key
        )

        root_dir = resume_state.get("root_dir") or ""
        if resume_matches and root_dir and os.path.isdir(root_dir):
            return root_dir

        script_dir = os.path.dirname(os.path.abspath(__file__))
        if resume_matches:
            resume_title = (resume_state.get("manga_title") or "").strip()
            if resume_title:
                resume_dir = os.path.join(script_dir, sanitize_filename(resume_title))
                if os.path.isdir(resume_dir) and self.looks_like_manga_download_dir(resume_dir):
                    return resume_dir

            resume_dt = self.parse_resume_timestamp(resume_state.get("timestamp"))
            if resume_dt is not None:
                candidates = []
                excluded_dirs = {
                    "__pycache__", ".git", ".venv", "build", "build_pyinstaller",
                    "dist", "dist_build", "release",
                }
                try:
                    for entry in os.scandir(script_dir):
                        if not entry.is_dir():
                            continue
                        if entry.name in excluded_dirs or entry.name.startswith("."):
                            continue
                        if not self.looks_like_manga_download_dir(entry.path):
                            continue
                        try:
                            modified_at = datetime.fromtimestamp(entry.stat().st_mtime)
                        except Exception:
                            continue
                        delta_seconds = abs((modified_at - resume_dt).total_seconds())
                        if delta_seconds <= 20 * 60:
                            candidates.append((delta_seconds, entry.path))
                except Exception:
                    candidates = []

                if candidates:
                    candidates.sort(key=lambda item: item[0])
                    return candidates[0][1]

        cached_detail = self.get_cached_manga_detail(adapter, source_url)
        if cached_detail and cached_detail.title:
            cached_dir = os.path.join(script_dir, sanitize_filename(cached_detail.title))
            if os.path.isdir(cached_dir) and self.looks_like_manga_download_dir(cached_dir):
                return cached_dir

        return None

    def get_local_manga_detail(self, adapter, source_url):
        entry = self.find_local_library_entry_by_source_url(adapter, source_url)
        if entry is None:
            return None

        root_dir = (entry.get("root_dir") or "").strip()
        downloaded_count = int(entry.get("downloaded_chapter_count") or 0)
        if downloaded_count <= 0:
            return None
        latest_title = entry.get("last_downloaded_chapter_title") or "-"
        manga_title = str(entry.get("manga_title") or os.path.basename(root_dir.rstrip("\\/")) or "本地漫画")

        resume_state = self.load_download_state() or {}
        resume_url = (resume_state.get("url") or "").strip()
        resume_matches = (
            resume_state.get("site_key") == adapter.key
            and resume_url
            and adapter.get_manga_cache_key(resume_url) == adapter.get_manga_cache_key(source_url)
        )
        total_chapters = int(resume_state.get("total_chapters") or 0) if resume_matches else 0
        next_chapter_order = int(resume_state.get("current_chapter_order") or 0) if resume_matches else 0
        metadata_total = int(entry.get("total_chapters") or 0)
        effective_total = max(total_chapters, metadata_total)

        detail_parts = [self.format_local_library_status(entry).replace("本地状态: ", "")]
        detail_parts.extend(self.get_library_update_status_lines(entry))
        if latest_title:
            detail_parts.append(f"已下载到 {latest_title}")
        if next_chapter_order > 0:
            detail_parts.append("可从本地断点继续下载")
        if effective_total > 0:
            detail_parts.append(f"总章节数约 {effective_total} 章")
        if root_dir:
            detail_parts.append(f"目录: {root_dir}")

        return MangaDetail(
            title=manga_title,
            manga_url=(source_url or "").strip(),
            section="本地离线",
            cover_url=str(entry.get("cover_url") or entry.get("_local_cover_path") or ""),
            latest_chapter=latest_title or "-",
            update_time="本地目录",
            detail_hint="\n".join(detail_parts),
            detail_section_label=f"站点: {adapter.display_name}（离线）",
            chapter_count=effective_total or downloaded_count,
            start_chapter_title="",
        )

    def get_fallback_manga_detail(self, adapter, source_url):
        cached_detail = self.get_cached_manga_detail(adapter, source_url)
        if cached_detail is not None:
            cached_hint = cached_detail.detail_hint or ""
            suffix = "当前站点暂时无法访问，已显示上次成功获取的缓存信息。"
            cached_detail.detail_hint = f"{cached_hint}；{suffix}" if cached_hint else suffix
            return cached_detail, "cache"

        local_detail = self.get_local_manga_detail(adapter, source_url)
        if local_detail is not None:
            local_hint = local_detail.detail_hint or ""
            suffix = "当前站点暂时无法访问\n已显示本地下载记录"
            local_detail.detail_hint = f"{local_hint}\n{suffix}" if local_hint else suffix
            return local_detail, "local"

        return None, ""
        
    def download_manga(self, url, adapter_key):
        """下载漫画的主要逻辑"""
        download_summary = None
        try:
            adapter = get_adapter(adapter_key)
            self.set_status("正在分析漫画信息...")
            self.log_message(f"开始下载: {url}")
            self.log_message(f"当前站点适配器: {adapter.display_name}")
            
            # 1. 获取漫画信息
            manga_id, manga_slug, url_start_slug = adapter.get_manga_info_from_url(url)
            if not manga_id or not manga_slug:
                self.log_message("❌ 无法获取漫画信息", "error")
                download_summary = {"final_state": "failed"}
                return
            self.log_message(f"✅ 已识别漫画链接，漫画标识: {manga_id}")
                
            # 2. 获取所有章节
            self.set_status("正在获取章节列表...")
            self.log_message("🔍 正在请求漫画主页并解析章节列表...")
            if adapter.key == "manhuagui":
                self.log_message("⚠️ 漫画柜站点响应可能较慢，主页链接首次解析通常需要几秒。", "warning")
            manga_title, all_chapters = adapter.get_all_chapters(manga_id)
            if not all_chapters:
                self.log_message("❌ 无法获取章节列表", "error")
                download_summary = {"final_state": "failed"}
                return
                
            self.log_message(f"✅ 找到漫画: {manga_title}, 共 {len(all_chapters)} 章")
            
            # 3. 确定起始章节
            start_order = self.start_var.get() - 1  # 转换为0基索引
            start_chapter_title = ""
            if start_order <= 0 and url_start_slug:
                matched_chapter = next(
                    (
                        chapter for chapter in all_chapters
                        if chapter.get("slug") == url_start_slug or chapter.get("uuid") == url_start_slug
                    ),
                    None,
                )
                if matched_chapter is not None:
                    start_order = matched_chapter.get("order", start_order)
                    start_chapter_title = matched_chapter.get("title") or url_start_slug
                    self.log_message(f"⚙️ 已根据链接定位起始章节: {matched_chapter.get('title') or url_start_slug}")
            pending_chapters = [c for c in all_chapters if c["order"] >= start_order]
            
            if not pending_chapters:
                self.log_message("⚠️ 没有找到需要下载的章节", "warning")
                download_summary = {"final_state": "empty"}
                return

            latest_chapter = all_chapters[-1] if all_chapters else {}
            detail_parts = [f"共 {len(all_chapters)} 章"]
            if start_chapter_title:
                detail_parts.append(f"当前链接定位到 {start_chapter_title}")
            self.cache_manga_detail(
                adapter,
                url,
                MangaDetail(
                    title=manga_title,
                    manga_url=url,
                    section="手动链接",
                    cover_url="",
                    latest_chapter=latest_chapter.get("title") or "-",
                    update_time=self.format_updated_at(latest_chapter.get("updated_at")),
                    detail_hint="，".join(detail_parts),
                    detail_section_label=f"站点: {adapter.display_name}",
                    chapter_count=len(all_chapters),
                    start_chapter_title=start_chapter_title,
                ),
            )
                
            # 4. 设置保存目录
            script_dir = os.path.dirname(os.path.abspath(__file__))
            safe_manga_title = sanitize_filename(str(manga_title))
            root_dir = os.path.join(script_dir, f"{safe_manga_title}")
            os.makedirs(root_dir, exist_ok=True)
            self.active_manga_title = str(manga_title)
            self.active_download_root_dir = root_dir
            self.active_download_metadata = self.build_active_download_metadata(
                adapter,
                url,
                manga_title,
                root_dir,
                all_chapters,
                start_order,
                start_chapter_title,
            )
            self.save_active_download_metadata()
            
            self.log_message(f"📂 保存目录: {root_dir}")
            self.log_message(f"📥 准备下载 {len(pending_chapters)} 章")
            
            # 5. 构建基础URL模板
            base_url_template = adapter.build_chapter_url_template(manga_slug)
            
            # 6. 开始下载
            max_concurrent = self.concurrent_var.get()
            max_image_concurrent = self.image_concurrent_var.get()
            max_concurrent, max_image_concurrent, settings_message = adapter.adjust_download_settings(
                max_concurrent,
                max_image_concurrent,
            )
            if settings_message:
                self.log_message(settings_message, "warning")
            
            download_summary = self.download_chapters_concurrently(
                adapter,
                pending_chapters,
                base_url_template,
                root_dir,
                max_concurrent,
                max_image_concurrent,
            )
            
        except RuntimeError as e:
            if self.is_site_access_blocked_error(e):
                self.handle_site_access_blocked_error(adapter.display_name, e)
            elif self.is_site_unreachable_error(e):
                self.handle_site_unreachable_error(adapter, e)
            else:
                self.log_message(f"❌ 下载过程中出现错误: {str(e)}", "error")
            if download_summary is None:
                download_summary = {"final_state": "failed"}
        except Exception as e:
            self.log_message(f"❌ 下载过程中出现错误: {str(e)}", "error")
            if download_summary is None:
                download_summary = {"final_state": "failed"}
        finally:
            self.download_complete(download_summary)
            
    def download_chapters_concurrently(self, adapter, chapters, base_url_template, root_dir, 
                                     max_concurrent, max_image_concurrent):
        """并发下载章节"""
        chapter_queue = [dict(chapter, _retry_count=0) for chapter in chapters]
        total_chapters = len(chapter_queue)
        completed_chapters = 0
        failed_chapters = 0
        retry_limit = adapter.get_chapter_retry_limit()
        cooldown_until = 0.0
        summary = {
            "final_state": "failed",
            "root_dir": root_dir,
            "manga_title": self.active_manga_title,
            "total_chapters": total_chapters,
            "completed_chapters": 0,
            "failed_chapters": 0,
            "should_offer_archive": False,
        }
        
        self.log_message(f"开始并发下载，章节并发数: {max_concurrent}, 图片并发数: {max_image_concurrent}")
        
        try:
            # 保存executor实例以便可以强制停止
            self.executor = ThreadPoolExecutor(max_workers=max_concurrent)
            
            futures = {}
            
            # 主循环
            while chapter_queue or futures:
                while self.is_paused and self.is_downloading and not self.stop_event.is_set():
                    self.pause_event.wait(timeout=0.2)

                # 检查是否停止
                if not self.is_downloading:
                    self.log_message("🛑 下载已停止")
                    # 取消所有未完成的任务
                    for future in futures:
                        future.cancel()
                    break

                if not futures and chapter_queue and cooldown_until > time.time():
                    remaining = max(cooldown_until - time.time(), 0)
                    self.set_status(f"网络波动，{remaining:.0f} 秒后自动重试...")
                    time.sleep(min(remaining, 0.5))
                    continue
                
                # 提交新任务
                while chapter_queue and len(futures) < max_concurrent:
                    if cooldown_until > time.time():
                        break
                    chapter = chapter_queue.pop(0)
                    future = self.executor.submit(
                        adapter.download_chapter_images,
                        chapter["slug"],
                        base_url_template,
                        root_dir,
                        max_image_concurrent,
                        self.stop_event,
                        False,
                    )
                    futures[future] = chapter
                
                if not futures:
                    break
                
                # 等待任务完成
                done, _ = wait(list(futures.keys()), timeout=0.2, return_when=FIRST_COMPLETED)
                if not done:
                    continue
                
                for future in done:
                    chapter = futures.pop(future)
                    try:
                        count, next_slug, _ = future.result()
                        if count > 0:
                            completed_chapters += 1
                            self.log_message(f"✅ 第 {chapter['order'] + 1} 章下载完成 ({completed_chapters}/{total_chapters})")
                            # 保存进度状态
                            self.save_download_state(chapter["order"], total_chapters)
                            self.save_active_download_metadata()
                        else:
                            failed_chapters += 1
                            self.log_message(f"⚠️ 第 {chapter['order'] + 1} 章下载失败", "warning")
                            
                    except Exception as e:
                        retry_count = chapter.get("_retry_count", 0)
                        if (
                            retry_limit > 0
                            and retry_count < retry_limit
                            and adapter.should_retry_download_error(e)
                            and self.is_downloading
                            and not self.stop_event.is_set()
                        ):
                            retry_count += 1
                            delay = adapter.get_retry_delay_seconds(retry_count)
                            chapter["_retry_count"] = retry_count
                            chapter_queue.insert(0, chapter)
                            cooldown_until = max(cooldown_until, time.time() + delay)
                            self.log_message(
                                f"⚠️ 第 {chapter['order'] + 1} 章请求超时，{delay:.0f} 秒后自动重试 ({retry_count}/{retry_limit})",
                                "warning",
                            )
                            self.set_status(f"第 {chapter['order'] + 1} 章重试准备中...")
                            continue

                        failed_chapters += 1
                        self.log_message(f"❌ 第 {chapter['order'] + 1} 章下载出错: {str(e)}", "error")
                    
                    # 更新进度
                    progress = (completed_chapters + failed_chapters) / total_chapters * 100
                    self.set_progress(progress)
                    self.set_status(f"进度: {completed_chapters + failed_chapters}/{total_chapters}")
                    
            # 清理executor
            if self.executor:
                self.executor.shutdown(wait=False, cancel_futures=True)
                self.executor = None
                
            # 下载完成总结
            if self.is_downloading:
                self.log_message(f"\n📊 下载完成统计:")
                self.log_message(f"✅ 成功: {completed_chapters} 章")
                self.log_message(f"❌ 失败: {failed_chapters} 章")
                self.log_message(f"📁 文件保存在: {root_dir}")
                # 清除断点续传数据
                self.clear_download_state()
                self.save_active_download_metadata(
                    mark_completed=(
                        completed_chapters > 0
                        and failed_chapters <= 0
                        and completed_chapters >= total_chapters
                    )
                )
                summary.update({
                    "final_state": "completed",
                    "completed_chapters": completed_chapters,
                    "failed_chapters": failed_chapters,
                    "should_offer_archive": completed_chapters > 0 and os.path.isdir(root_dir),
                })
                
        except Exception as e:
            self.log_message(f"❌ 并发下载出错: {str(e)}", "error")
            if self.executor:
                self.executor.shutdown(wait=False, cancel_futures=True)
                self.executor = None
        return summary
            
    def save_download_state(self, current_chapter_order, total_chapters):
        """保存下载状态用于断点续传"""
        try:
            state_url = (self.active_download_url or self.current_download_url or "").strip()
            if not state_url:
                return
            state_data = {
                'state_version': 2,
                'site_key': self.download_site_key,
                'url': state_url,
                'current_chapter_order': current_chapter_order + 2,  # 保存下一章的人类可读序号
                'total_chapters': total_chapters,
                'timestamp': time.strftime("%Y-%m-%d %H:%M:%S"),
                'manga_title': self.active_manga_title,
                'root_dir': self.active_download_root_dir,
            }
            with open(self.resume_data_file, 'w', encoding='utf-8') as f:
                json.dump(state_data, f, ensure_ascii=False, indent=2)
        except Exception as e:
            self.log_message(f"保存下载状态时出错: {str(e)}", "warning")

    def load_download_state(self):
        """加载下载状态"""
        try:
            if os.path.exists(self.resume_data_file):
                with open(self.resume_data_file, 'r', encoding='utf-8') as f:
                    state = json.load(f)
                    if isinstance(state, dict):
                        normalized = False
                        version = int(state.get("state_version") or 1)
                        current_order = state.get("current_chapter_order")
                        if version < 2 and isinstance(current_order, int) and current_order > 0:
                            state["current_chapter_order"] = current_order + 1
                            state["state_version"] = 2
                            normalized = True
                        if normalized:
                            try:
                                with open(self.resume_data_file, 'w', encoding='utf-8') as wf:
                                    json.dump(state, wf, ensure_ascii=False, indent=2)
                            except Exception:
                                pass
                        return state
        except Exception as e:
            self.log_message(f"加载下载状态时出错: {str(e)}", "warning")
        return None

    def clear_download_state(self):
        """清除下载状态"""
        try:
            if os.path.exists(self.resume_data_file):
                os.remove(self.resume_data_file)
        except Exception as e:
            self.log_message(f"清除下载状态时出错: {str(e)}", "warning")

    def check_resume_download_on_startup(self):
        """启动时检查是否有可恢复的下载"""
        self.check_resume_download()
        
    def check_resume_download(self):
        """检查是否有可恢复的下载"""
        state = self.load_download_state()
        if state:
            resume_adapter = get_adapter(state.get('site_key', DEFAULT_SITE_KEY))
            result = self.ask_resume_download_confirmation(resume_adapter, state)
            if result:
                self.download_site_key = resume_adapter.key
                self.set_active_adapter(resume_adapter.key)
                self.set_download_url(state['url'])
                self.start_var.set(state['current_chapter_order'])
                self.log_message(f"已恢复下载任务，从第{state['current_chapter_order']}章开始")
                return True
        return False

    def download_complete(self, download_summary=None):
        """下载完成后的清理工作"""
        stopped = self.stop_event.is_set()
        active_root_dir = self.active_download_root_dir
        active_manga_title = self.active_manga_title
        normalized_summary = dict(download_summary or {})
        if active_root_dir and not normalized_summary.get("root_dir"):
            normalized_summary["root_dir"] = active_root_dir
        if active_manga_title and not normalized_summary.get("manga_title"):
            normalized_summary["manga_title"] = active_manga_title

        self.is_downloading = False
        self.is_paused = False
        self.executor = None
        self.active_download_url = ""
        self.active_download_root_dir = ""
        self.active_manga_title = ""
        self.active_download_metadata = None

        def apply():
            if self._closing:
                return
            completed_chapters = int(normalized_summary.get("completed_chapters") or 0)
            failed_chapters = int(normalized_summary.get("failed_chapters") or 0)
            final_state = (normalized_summary.get("final_state") or "").strip()

            self.download_btn.config(state=tk.NORMAL)
            self.stop_btn.config(state=tk.DISABLED)
            self.pause_btn.config(state=tk.DISABLED)
            self.resume_btn.config(state=tk.DISABLED)

            if stopped:
                final_status = "下载已停止"
                progress_style = 'Danger.Horizontal.TProgressbar'
                should_fill_progress = False
            elif final_state == "empty":
                final_status = "没有可下载章节"
                progress_style = 'Warning.Horizontal.TProgressbar'
                should_fill_progress = False
            elif final_state == "failed":
                final_status = "下载失败"
                progress_style = 'Danger.Horizontal.TProgressbar'
                should_fill_progress = False
            elif completed_chapters <= 0 and failed_chapters > 0:
                final_status = "下载失败"
                progress_style = 'Danger.Horizontal.TProgressbar'
                should_fill_progress = True
            elif failed_chapters > 0:
                final_status = f"下载完成（成功 {completed_chapters} / 失败 {failed_chapters}）"
                progress_style = 'Warning.Horizontal.TProgressbar'
                should_fill_progress = True
            else:
                final_status = "下载完成"
                progress_style = 'Success.Horizontal.TProgressbar'
                should_fill_progress = True

            if should_fill_progress:
                self.progress_var.set(100)
                self.progress_text_var.set("100%")
                self.progress_bar.configure(style=progress_style)
            else:
                self.progress_text_var.set(f"{self.progress_var.get():.0f}%")
                self.progress_bar.configure(style=progress_style)
            self.status_text_var.set(final_status)
            self.stop_event.clear()
            self.pause_event.set()
            if not stopped:
                self.offer_archive_after_download(normalized_summary)

        self.run_on_ui_thread(apply)

    def on_window_close(self):
        """关闭窗口时先尝试停止下载，再安全退出。"""
        self._closing = True
        if self.ui_task_pump_job is not None:
            try:
                self.root.after_cancel(self.ui_task_pump_job)
            except Exception:
                pass
            self.ui_task_pump_job = None
        if self.log_flush_job is not None:
            try:
                self.root.after_cancel(self.log_flush_job)
            except Exception:
                pass
            self.log_flush_job = None
        sys.stdout = self.original_stdout
        sys.stderr = self.original_stderr

        if self.is_downloading:
            self.is_downloading = False
            self.stop_event.set()
            if self.executor:
                try:
                    self.executor.shutdown(wait=False, cancel_futures=True)
                except Exception:
                    pass
            self.wait_for_download_then_close()
            return

        self.root.destroy()

    def wait_for_download_then_close(self):
        if self.current_thread and self.current_thread.is_alive():
            self.root.after(200, self.wait_for_download_then_close)
            if not self._force_exit_scheduled:
                self._force_exit_scheduled = True
                self.root.after(3000, self.force_close_if_needed)
            return
        self.root.destroy()

    def force_close_if_needed(self):
        if self.current_thread and self.current_thread.is_alive():
            os._exit(0)

def main():
    root = tk.Tk()
    app = ComicDownloaderGUI(root)
    root.mainloop()

if __name__ == "__main__":
    main()
