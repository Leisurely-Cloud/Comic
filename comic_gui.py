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
import requests
from datetime import datetime
from downcomic import (
    get_manga_info_from_url, get_all_chapters, download_chapter_images,
    sanitize_filename, proxy_pool, print_lock, fetch_homepage_manga_cards,
    filter_homepage_cards, fetch_section_manga_cards, fetch_search_manga_cards
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
        self.root.geometry("1080x820")
        self.root.minsize(920, 680)
        
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
        self.stop_event = threading.Event()  # 停止事件
        self.pause_event = threading.Event()  # 暂停事件
        self.pause_event.set()  # 默认不暂停
        self.rank_cards = []
        self.current_homepage_cards = []
        self.cover_image = None
        self.current_cover_url = None
        self.rank_detail_cache = {}
        self.current_detail_request_key = None
        self.current_download_url = ""
        self.section_options = {
            "人气排行": "rank",
            "近期更新": "recent",
            "热门更新": "hot-update",
            "最新上架": "new",
        }
        self.current_section_page = 1
        self.search_query_var = tk.StringVar()
        self.log_queue = queue.Queue()
        self.log_flush_job = None
        self.max_log_lines = 800
        
        # 创建界面
        self.create_widgets()
        
        # 重定向打印输出到文本框
        self.redirect_output()
        self.root.protocol("WM_DELETE_WINDOW", self.on_window_close)
        
        # 检查是否有可恢复的下载
        self.root.after(1000, self.check_resume_download_on_startup)
        self.root.after(300, self.refresh_rankings)
        self.root.after(250, self.configure_initial_pane_layout)
        self.root.after(900, self.configure_initial_pane_layout)

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
        main_frame = ttk.Frame(self.root, padding="14")
        main_frame.grid(row=0, column=0, sticky="nsew")
        
        # 配置网格权重
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(0, weight=1)
        main_frame.columnconfigure(0, weight=1)
        main_frame.rowconfigure(1, weight=1)
        
        action_panel = ttk.LabelFrame(main_frame, text="下载操作", padding="10", style='Section.TLabelframe')
        action_panel.grid(row=0, column=0, sticky="ew", pady=(0, 12))
        action_panel.columnconfigure(0, weight=3)
        action_panel.columnconfigure(1, weight=4)

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
        for col in range(4):
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

        # 这些选项不再显示在界面中，保留默认行为即可
        self.proxy_var = tk.BooleanVar(value=False)
        self.start_var = tk.IntVar(value=1)

        content_pane = ttk.Panedwindow(main_frame, orient=tk.HORIZONTAL)
        content_pane.grid(row=1, column=0, sticky="nsew", pady=(0, 10))

        ranking_frame = ttk.LabelFrame(content_pane, text="首页发现", padding="12", style='Section.TLabelframe')
        ranking_frame.columnconfigure(0, weight=1)
        ranking_frame.rowconfigure(1, weight=1)

        ranking_header = ttk.Frame(ranking_frame, style='Panel.TFrame')
        ranking_header.grid(row=0, column=0, sticky="ew", pady=(0, 10))
        ranking_header.columnconfigure(0, weight=1)

        search_frame = ttk.Frame(ranking_header, style='Panel.TFrame')
        search_frame.grid(row=0, column=0, sticky="ew", pady=(0, 8))
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
        self.next_page_btn.pack(side=tk.LEFT, padx=(0, 10))

        self.refresh_rank_btn = ttk.Button(
            ranking_action_frame,
            text="刷新列表",
            command=self.refresh_rankings,
            style='Accent.TButton'
        )
        self.refresh_rank_btn.pack(side=tk.LEFT, padx=(0, 10))

        self.download_rank_btn = ttk.Button(
            ranking_action_frame,
            text="下载选中漫画",
            command=self.download_selected_ranking,
            style='Success.TButton'
        )
        self.download_rank_btn.pack(side=tk.LEFT)

        ranking_pane = ttk.Panedwindow(ranking_frame, orient=tk.HORIZONTAL)
        ranking_pane.grid(row=1, column=0, sticky="nsew")

        ranking_list = ttk.Frame(ranking_pane, style='Panel.TFrame')
        ranking_list.columnconfigure(0, weight=1)
        ranking_list.rowconfigure(0, weight=1)

        columns = ("rank", "title")
        self.rank_tree = ttk.Treeview(
            ranking_list,
            columns=columns,
            show="headings",
            height=6
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

        ranking_detail = ttk.Frame(ranking_pane, style='Surface.TFrame', padding="10")
        ranking_detail.columnconfigure(0, weight=1)
        ranking_detail.rowconfigure(0, weight=0)

        self.cover_preview = tk.Label(
            ranking_detail,
            text="封面预览",
            bg=self.colors['accent_soft'],
            fg=self.colors['accent'],
            font=('Microsoft YaHei UI', 11, 'bold'),
            relief=tk.FLAT,
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
            wraplength=240,
            anchor="center",
            justify="center"
        ).grid(row=5, column=0, sticky="ew", pady=(4, 0))

        ranking_pane.add(ranking_list, weight=5)
        ranking_pane.add(ranking_detail, weight=2)
        self.ranking_pane = ranking_pane
        self.ranking_list_panel = ranking_list
        
        # 日志区域
        log_frame = ttk.LabelFrame(content_pane, text="下载日志", padding="12", style='Section.TLabelframe')
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
        
        footer_frame = ttk.Frame(main_frame, style='Panel.TFrame')
        footer_frame.grid(row=2, column=0, sticky="ew")
        footer_frame.columnconfigure(0, weight=1)

        self.progress_text_var = tk.StringVar(value="等待开始")
        footer_header = ttk.Frame(footer_frame, style='Panel.TFrame')
        footer_header.grid(row=0, column=0, sticky="ew", pady=(0, 6))
        footer_header.columnconfigure(0, weight=1)

        ttk.Label(footer_header, text="总体进度", style='Footer.TLabel').grid(row=0, column=0, sticky="w")
        ttk.Label(footer_header, textvariable=self.progress_text_var, style='Info.TLabel').grid(row=0, column=1, sticky="e")

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
                self.content_pane.sashpos(0, max(780, int(total_width * 0.64)))
        except Exception:
            pass
        try:
            if hasattr(self, "ranking_pane"):
                total_width = self.ranking_pane.winfo_width()
                if total_width > 0:
                    self.ranking_pane.sashpos(0, max(430, int(total_width * 0.52)))
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
        if self._closing or not self.root.winfo_exists():
            return
        self.root.after(0, lambda: func(*args, **kwargs))

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
        self.schedule_log_flush()

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
        if self._closing or not self.log_text.winfo_exists():
            return

        pending_logs = []
        try:
            while len(pending_logs) < 200:
                pending_logs.append(self.log_queue.get_nowait())
        except queue.Empty:
            pass

        if not pending_logs:
            return

        for message, tag in pending_logs:
            self.log_text.insert(tk.END, message, tag)

        self.trim_log_lines()
        self.log_text.see(tk.END)

        if not self.log_queue.empty():
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
        pass

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

    def set_ranking_buttons_state(self, loading=False):
        def apply():
            if self._closing:
                return
            self.refresh_rank_btn.config(state=tk.DISABLED if loading else tk.NORMAL)
            self.download_rank_btn.config(state=tk.DISABLED if loading else tk.NORMAL)
            self.prev_page_btn.config(state=tk.DISABLED if loading else tk.NORMAL)
            self.next_page_btn.config(state=tk.DISABLED if loading else tk.NORMAL)
            self.search_btn.config(state=tk.DISABLED if loading else tk.NORMAL)
            self.clear_search_btn.config(state=tk.DISABLED if loading else tk.NORMAL)
        self.run_on_ui_thread(apply)

    def update_section_pagination_ui(self, section_key, page, has_cards=True, search_query=""):
        def apply():
            if self._closing:
                return
            if search_query:
                self.section_page_var.set(f"搜索第 {page} 页")
                self.prev_page_btn.config(state=tk.DISABLED if page <= 1 else tk.NORMAL)
                self.next_page_btn.config(state=tk.DISABLED if not has_cards else tk.NORMAL)
                return

            self.section_page_var.set(f"第 {page} 页")
            is_recent = section_key == "recent"
            self.prev_page_btn.config(state=tk.DISABLED if is_recent or page <= 1 else tk.NORMAL)
            self.next_page_btn.config(state=tk.DISABLED if is_recent or not has_cards else tk.NORMAL)
        self.run_on_ui_thread(apply)

    def get_section_display_name(self, section_key):
        mapping = {
            "rank": "人气排行",
            "recent": "近期更新",
            "hot-update": "热门更新",
            "new": "最新上架",
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
        selection = self.rank_tree.selection()
        if not selection:
            return
        iid = selection[0]
        try:
            index = int(iid)
        except (TypeError, ValueError):
            return
        if index < 0 or index >= len(self.rank_cards):
            return

        def apply():
            if self._closing or not self.rank_tree.exists(iid):
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

    def update_ranking_detail(self, card=None):
        if not card:
            self.current_download_url = ""
            self.run_on_ui_thread(self.detail_title_var.set, "请选择一部漫画")
            self.run_on_ui_thread(self.detail_section_var.set, "分区: -")
            self.run_on_ui_thread(self.detail_latest_var.set, "最近章节: -")
            self.run_on_ui_thread(self.detail_update_var.set, "更新时间: -")
            self.run_on_ui_thread(self.detail_cover_var.set, "")
            self.reset_cover_preview()
            return

        self.current_download_url = card.manga_url
        self.run_on_ui_thread(self.detail_title_var.set, card.title)
        self.run_on_ui_thread(self.detail_section_var.set, f"分区: {card.section}")
        self.run_on_ui_thread(self.detail_latest_var.set, f"最近章节: {card.latest_chapter or '-'}")
        self.run_on_ui_thread(self.detail_update_var.set, f"更新时间: {card.update_time or '-'}")
        self.run_on_ui_thread(self.detail_cover_var.set, "")
        self.load_cover_preview(card.cover_url, card.title)
        self.enrich_card_detail(card)

    def enrich_card_detail(self, card):
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
                manga_id, _, _ = get_manga_info_from_url(card.manga_url)
                if not manga_id:
                    return
                _, chapters = get_all_chapters(manga_id)
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

    def load_cover_preview(self, cover_url, title):
        self.current_cover_url = cover_url
        if not cover_url:
            self.reset_cover_preview("暂无封面")
            return

        if Image is None or ImageTk is None:
            self.reset_cover_preview("未安装 Pillow\n无法显示封面")
            return

        self.reset_cover_preview("封面加载中...")

        def worker():
            try:
                session = requests.Session()
                session.trust_env = False
                resp = session.get(
                    cover_url,
                    timeout=20,
                    headers={
                        'User-Agent': 'Mozilla/5.0',
                        'Referer': 'https://baozimh.org/',
                        'Accept': 'image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8'
                    }
                )
                resp.raise_for_status()
                image = Image.open(io.BytesIO(resp.content))
                image.thumbnail((180, 220))
                photo = ImageTk.PhotoImage(image)

                def apply():
                    if self._closing or cover_url != self.current_cover_url:
                        return
                    self.cover_image = photo
                    self.cover_preview.config(image=photo, text="", bg=self.colors['surface_alt'])

                self.run_on_ui_thread(apply)
            except Exception:
                if cover_url == self.current_cover_url:
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
            return
        self.search_query_var.set("")
        self.current_section_page = 1
        self.log_message("已清空搜索结果，返回分区浏览。")
        self.refresh_rankings()

    def refresh_rankings(self):
        section_key = self.section_options.get(self.homepage_section_var.get(), "rank")
        search_query = self.get_active_search_query()
        section_label = self.get_section_display_name(section_key)
        page = self.current_section_page
        target_label = f"搜索“{search_query}”" if search_query else section_label
        self.log_message(f"🔍 正在刷新首页列表: {target_label} 第 {page} 页...")
        self.set_status(f"正在刷新{target_label}...")
        self.set_ranking_buttons_state(loading=True)

        def worker():
            try:
                cards = fetch_search_manga_cards(search_query, page=page) if search_query else fetch_section_manga_cards(section_key, page=page)
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
                self.update_ranking_detail(cards[0] if cards else None)
                self.update_section_pagination_ui(section_key, page, has_cards=bool(cards), search_query=search_query)
                if cards:
                    self.run_on_ui_thread(self.rank_tree.selection_set, "0")
                    self.run_on_ui_thread(self.rank_tree.focus, "0")
                elif search_query:
                    self.log_message(f"⚠️ 未找到与“{search_query}”相关的漫画", "warning")

                self.log_message(f"✅ 已加载{target_label}第 {page} 页，共 {len(cards)} 部漫画")
                if not self.is_downloading:
                    self.set_status(f"{target_label}已更新")
            except Exception as e:
                self.log_message(f"❌ 刷新{target_label}失败: {str(e)}", "error")
                self.current_homepage_cards = []
                self.rank_cards = []
                self.populate_ranking_tree([])
                self.update_ranking_detail(None)
                self.update_section_pagination_ui(section_key, page, has_cards=False, search_query=search_query)
                if not self.is_downloading:
                    self.set_status(f"{target_label}刷新失败")
            finally:
                self.set_ranking_buttons_state(loading=False)

        thread = threading.Thread(target=worker, daemon=True)
        thread.start()

    def on_homepage_section_change(self, event=None):
        if self.get_active_search_query():
            self.search_query_var.set("")
        self.current_section_page = 1
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
        self.current_download_url = card.manga_url
        self.update_ranking_detail(card)
        self.log_message(f"已选中首页漫画: {card.title}")

    def download_selected_ranking(self):
        if self.is_downloading:
            messagebox.showwarning("提示", "当前已有下载任务，请先暂停或停止后再试。")
            return

        card = self.get_selected_ranking_card()
        if not card:
            messagebox.showwarning("提示", "请先在排行榜中选择一部漫画。")
            return

        self.current_download_url = card.manga_url
        self.log_message(f"🎯 从热门排行榜启动下载: {card.title}")
        self.start_download(card.manga_url)

    def on_ranking_double_click(self, event=None):
        card = self.get_selected_ranking_card()
        if card:
            self.download_selected_ranking()
        
    def start_download(self, url=None):
        """开始下载"""
        if not url:
            card = self.get_selected_ranking_card()
            if card:
                url = card.manga_url
            else:
                url = (self.current_download_url or "").strip()

        if not url:
            messagebox.showwarning("警告", "请先在首页列表或搜索结果中选择一部漫画")
            return
            
        if not url.startswith("https://baozimh.org/"):
            messagebox.showwarning("警告", "URL必须以 https://baozimh.org/ 开头")
            return

        self.current_download_url = url
            
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
        self.current_thread = threading.Thread(target=self.download_manga, args=(url,))
        self.current_thread.daemon = True
        self.current_thread.start()
        
    def stop_download(self):
        """停止下载"""
        self.is_downloading = False
        self.stop_event.set()  # 设置停止事件
        self.log_message("正在停止下载...")
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
        
    def download_manga(self, url):
        """下载漫画的主要逻辑"""
        try:
            self.set_status("正在分析漫画信息...")
            self.log_message(f"开始下载: {url}")
            
            # 1. 获取漫画信息
            manga_id, manga_slug, url_start_slug = get_manga_info_from_url(url)
            if not manga_id or not manga_slug:
                self.log_message("❌ 无法获取漫画信息", "error")
                self.download_complete()
                return
                
            # 2. 获取所有章节
            self.set_status("正在获取章节列表...")
            manga_title, all_chapters = get_all_chapters(manga_id)
            if not all_chapters:
                self.log_message("❌ 无法获取章节列表", "error")
                self.download_complete()
                return
                
            self.log_message(f"✅ 找到漫画: {manga_title}, 共 {len(all_chapters)} 章")
            
            # 3. 确定起始章节
            start_order = self.start_var.get() - 1  # 转换为0基索引
            pending_chapters = [c for c in all_chapters if c["order"] >= start_order]
            
            if not pending_chapters:
                self.log_message("⚠️ 没有找到需要下载的章节", "warning")
                self.download_complete()
                return
                
            # 4. 设置保存目录
            script_dir = os.path.dirname(os.path.abspath(__file__))
            safe_manga_title = sanitize_filename(str(manga_title))
            root_dir = os.path.join(script_dir, f"{safe_manga_title}")
            os.makedirs(root_dir, exist_ok=True)
            
            self.log_message(f"📂 保存目录: {root_dir}")
            self.log_message(f"📥 准备下载 {len(pending_chapters)} 章")
            
            # 5. 构建基础URL模板
            base_url_template = f"https://baozimh.org/manga/{manga_slug}/{{slug}}"
            
            # 6. 开始下载
            max_concurrent = self.concurrent_var.get()
            max_image_concurrent = self.image_concurrent_var.get()
            
            self.download_chapters_concurrently(pending_chapters, base_url_template, 
                                              root_dir, max_concurrent, max_image_concurrent)
            
        except Exception as e:
            self.log_message(f"❌ 下载过程中出现错误: {str(e)}", "error")
        finally:
            self.download_complete()
            
    def download_chapters_concurrently(self, chapters, base_url_template, root_dir, 
                                     max_concurrent, max_image_concurrent):
        """并发下载章节"""
        total_chapters = len(chapters)
        completed_chapters = 0
        failed_chapters = 0
        
        self.log_message(f"开始并发下载，章节并发数: {max_concurrent}, 图片并发数: {max_image_concurrent}")
        
        try:
            # 保存executor实例以便可以强制停止
            self.executor = ThreadPoolExecutor(max_workers=max_concurrent)
            
            futures = {}
            
            # 主循环
            while chapters or futures:
                while self.is_paused and self.is_downloading and not self.stop_event.is_set():
                    self.pause_event.wait(timeout=0.2)

                # 检查是否停止
                if not self.is_downloading:
                    self.log_message("🛑 下载已停止")
                    # 取消所有未完成的任务
                    for future in futures:
                        future.cancel()
                    break
                
                # 提交新任务
                while chapters and len(futures) < max_concurrent:
                    chapter = chapters.pop(0)
                    future = self.executor.submit(download_chapter_images, 
                                           chapter["slug"], 
                                           base_url_template, 
                                           root_dir, 
                                           max_image_concurrent,
                                           self.stop_event,
                                           False)
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
                        else:
                            failed_chapters += 1
                            self.log_message(f"⚠️ 第 {chapter['order'] + 1} 章下载失败", "warning")
                            
                    except Exception as e:
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
                
        except Exception as e:
            self.log_message(f"❌ 并发下载出错: {str(e)}", "error")
            if self.executor:
                self.executor.shutdown(wait=False, cancel_futures=True)
                self.executor = None
            
    def save_download_state(self, current_chapter_order, total_chapters):
        """保存下载状态用于断点续传"""
        try:
            state_url = (self.current_download_url or "").strip()
            if not state_url:
                selected_card = self.get_selected_ranking_card()
                if selected_card:
                    state_url = selected_card.manga_url
            if not state_url:
                return
            state_data = {
                'url': state_url,
                'current_chapter_order': current_chapter_order + 1,  # 保存下一章
                'total_chapters': total_chapters,
                'timestamp': time.strftime("%Y-%m-%d %H:%M:%S")
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
                    return json.load(f)
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
            result = messagebox.askyesno(
                "发现未完成的下载",
                f"发现未完成的下载任务:\n"
                f"漫画URL: {state['url']}\n"
                f"上次进度: 第{state['current_chapter_order']}章\n"
                f"总章节数: {state['total_chapters']}\n"
                f"保存时间: {state['timestamp']}\n\n"
                f"是否恢复下载？"
            )
            if result:
                self.current_download_url = state['url']
                self.start_var.set(state['current_chapter_order'])
                self.log_message(f"已恢复下载任务，从第{state['current_chapter_order']}章开始")
                return True
        return False

    def download_complete(self):
        """下载完成后的清理工作"""
        self.is_downloading = False
        self.is_paused = False
        self.executor = None

        def apply():
            if self._closing:
                return
            self.download_btn.config(state=tk.NORMAL)
            self.stop_btn.config(state=tk.DISABLED)
            self.pause_btn.config(state=tk.DISABLED)
            self.resume_btn.config(state=tk.DISABLED)
            final_status = "下载已停止" if self.stop_event.is_set() else "下载完成"
            if not self.stop_event.is_set():
                self.progress_var.set(100)
                self.progress_text_var.set("100%")
                self.progress_bar.configure(style='Success.Horizontal.TProgressbar')
            else:
                self.progress_text_var.set(f"{self.progress_var.get():.0f}%")
                self.progress_bar.configure(style='Danger.Horizontal.TProgressbar')
            self.stop_event.clear()
            self.pause_event.set()

        self.run_on_ui_thread(apply)

    def on_window_close(self):
        """关闭窗口时先尝试停止下载，再安全退出。"""
        self._closing = True
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
