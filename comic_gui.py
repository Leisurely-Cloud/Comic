import tkinter as tk
from tkinter import ttk, messagebox, scrolledtext
import threading
import queue
import os
import sys
import json
import pickle
from downcomic import (
    get_manga_info_from_url, get_all_chapters, download_chapter_images, 
    sanitize_filename, proxy_pool, print_lock
)
from concurrent.futures import ThreadPoolExecutor, wait, FIRST_COMPLETED, as_completed
import time

class ComicDownloaderGUI:
    def __init__(self, root):
        self.root = root
        self.root.title("🚀 智能漫画下载器")
        self.root.geometry("1080x820")
        self.root.minsize(920, 680)
        self.set_window_icon()
        
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
        
        # 创建界面
        self.create_widgets()
        
        # 重定向打印输出到文本框
        self.redirect_output()
        self.root.protocol("WM_DELETE_WINDOW", self.on_window_close)
        
        # 检查是否有可恢复的下载
        self.root.after(1000, self.check_resume_download_on_startup)

    def set_window_icon(self):
        """如果项目目录存在 app.ico，则设置窗口图标。"""
        icon_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "app.ico")
        if os.path.exists(icon_path):
            try:
                self.root.iconbitmap(icon_path)
            except Exception:
                pass
        
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
        self.style.configure('Accent.TButton', 
                       background=self.colors['accent'], 
                       foreground=self.colors['light'],
                       font=button_font,
                       padding=(14, 8),
                       borderwidth=1,
                       focusthickness=1,
                       focuscolor=self.colors['accent'])
        self.style.map('Accent.TButton',
                 background=[('active', '#2980b9'), ('disabled', '#bdc3c7')],
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
                 background=[('active', '#229954'), ('disabled', '#bdc3c7')],
                 foreground=[('active', self.colors['light']), ('disabled', '#6c757d')])
        
        self.style.configure('Warning.TButton', 
                       background=self.colors['warning'], 
                       foreground=self.colors['light'],
                       font=button_font,
                       padding=(14, 8))
        self.style.map('Warning.TButton',
                 background=[('active', '#d68910'), ('disabled', '#bdc3c7')],
                 foreground=[('active', self.colors['light']), ('disabled', '#6c757d')])
        
        self.style.configure('Danger.TButton', 
                       background=self.colors['danger'], 
                       foreground=self.colors['light'],
                       font=button_font,
                       padding=(14, 8))
        self.style.map('Danger.TButton',
                 background=[('active', '#c0392b'), ('disabled', '#bdc3c7')],
                 foreground=[('active', self.colors['light']), ('disabled', '#6c757d')])
        
    def create_widgets(self):
        # 主框架
        main_frame = ttk.Frame(self.root, padding="14")
        main_frame.grid(row=0, column=0, sticky="nsew")
        
        # 配置网格权重
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(0, weight=1)
        main_frame.columnconfigure(0, weight=1)
        main_frame.rowconfigure(4, weight=1)

        hero_frame = ttk.Frame(main_frame)
        hero_frame.grid(row=0, column=0, sticky="ew", pady=(0, 12))
        hero_frame.columnconfigure(0, weight=1)

        title_frame = ttk.Frame(hero_frame)
        title_frame.grid(row=0, column=0, sticky="w")
        ttk.Label(title_frame, text="漫画下载控制台", style='Title.TLabel').grid(row=0, column=0, sticky="w")
        ttk.Label(
            title_frame,
            text="更清晰地管理 URL、下载设置、实时日志和状态反馈",
            style='Subtitle.TLabel'
        ).grid(row=1, column=0, sticky="w", pady=(4, 0))

        self.status_badge = tk.Label(
            hero_frame,
            text="就绪",
            bg=self.colors['accent_soft'],
            fg=self.colors['accent'],
            font=('Microsoft YaHei UI', 10, 'bold'),
            padx=12,
            pady=6
        )
        self.status_badge.grid(row=0, column=1, rowspan=2, sticky="e")
        
        # URL输入区域
        url_frame = ttk.LabelFrame(main_frame, text="漫画 URL", padding="12", style='Section.TLabelframe')
        url_frame.grid(row=1, column=0, sticky="ew", pady=(0, 12))
        url_frame.columnconfigure(0, weight=1)
        
        self.url_entry = ttk.Entry(url_frame, width=60, font=('Microsoft YaHei UI', 10))
        self.url_entry.grid(row=0, column=0, sticky="ew", padx=(0, 10), ipady=4)
        self.url_entry.insert(0, "https://baozimh.org/chapterlist/")
        
        # 示例提示
        ttk.Label(
            url_frame,
            text="支持目录页和章节页链接，建议直接粘贴完整的 `https://baozimh.org/...` 地址。",
            style='Hint.TLabel'
        ).grid(row=1, column=0, sticky=tk.W, pady=(8, 0))
        
        # 控制按钮
        controls_panel = ttk.LabelFrame(main_frame, text="下载控制", padding="12", style='Section.TLabelframe')
        controls_panel.grid(row=2, column=0, sticky="ew", pady=(0, 12))
        controls_panel.columnconfigure(0, weight=1)

        ttk.Label(
            controls_panel,
            text="开始、暂停、恢复和停止操作都会立即反馈到日志与进度区。",
            style='Info.TLabel'
        ).grid(row=0, column=0, sticky="w", pady=(0, 10))

        button_frame = ttk.Frame(controls_panel, style='Panel.TFrame')
        button_frame.grid(row=1, column=0, sticky="w")
        
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
        
        # 设置区域
        settings_frame = ttk.LabelFrame(main_frame, text="下载设置", padding="12", style='Section.TLabelframe')
        settings_frame.grid(row=3, column=0, sticky="ew", pady=(0, 12))
        for col in range(7):
            settings_frame.columnconfigure(col, weight=1 if col in (1, 3, 6) else 0)
        
        # 并发设置
        ttk.Label(settings_frame, text="章节并发数:").grid(row=0, column=0, sticky=tk.W, padx=(0, 10))
        self.concurrent_var = tk.IntVar(value=3)
        concurrent_spin = ttk.Spinbox(settings_frame, from_=1, to=10, 
                                     textvariable=self.concurrent_var, width=5)
        concurrent_spin.grid(row=0, column=1, padx=(0, 20), sticky="w")
        
        ttk.Label(settings_frame, text="图片并发数:").grid(row=0, column=2, sticky=tk.W, padx=(0, 10))
        self.image_concurrent_var = tk.IntVar(value=3)
        image_concurrent_spin = ttk.Spinbox(settings_frame, from_=1, to=10, 
                                           textvariable=self.image_concurrent_var, width=5)
        image_concurrent_spin.grid(row=0, column=3, padx=(0, 20), sticky="w")
        
        # 代理设置
        self.proxy_var = tk.BooleanVar(value=False)
        proxy_check = ttk.Checkbutton(settings_frame, text="启用代理池", 
                                     variable=self.proxy_var)
        proxy_check.grid(row=0, column=4, padx=(0, 20), sticky="w")
        
        # 起始章节
        ttk.Label(settings_frame, text="起始章节:").grid(row=0, column=5, sticky=tk.W, padx=(0, 10))
        self.start_var = tk.IntVar(value=1)
        start_spin = ttk.Spinbox(settings_frame, from_=1, to=9999, 
                                textvariable=self.start_var, width=5)
        start_spin.grid(row=0, column=6, sticky="w")

        ttk.Label(
            settings_frame,
            text="建议网络稳定时提高并发，若遇到失败或限流可适当降低。",
            style='Info.TLabel'
        ).grid(row=1, column=0, columnspan=7, sticky="w", pady=(10, 0))
        
        # 日志区域
        log_frame = ttk.LabelFrame(main_frame, text="下载日志", padding="12", style='Section.TLabelframe')
        log_frame.grid(row=4, column=0, sticky="nsew", pady=(0, 10))
        log_frame.columnconfigure(0, weight=1)
        log_frame.rowconfigure(0, weight=1)
        
        self.log_text = scrolledtext.ScrolledText(
            log_frame,
            height=15,
            width=80,
            wrap=tk.WORD,
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
        self.configure_log_tags()
        
        footer_frame = ttk.Frame(main_frame, style='Panel.TFrame')
        footer_frame.grid(row=5, column=0, sticky="ew")
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
        
        # 状态栏
        self.status_var = tk.StringVar(value="就绪")
        status_bar = ttk.Label(
            footer_frame,
            textvariable=self.status_var,
            style='Footer.TLabel',
            anchor="w"
        )
        status_bar.grid(row=2, column=0, sticky="ew")

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
        """线程安全地向日志框追加文本。"""
        def append():
            if self._closing or not self.log_text.winfo_exists():
                return
            self.log_text.insert(tk.END, message, tag)
            self.log_text.see(tk.END)
            self.log_text.update_idletasks()
        self.run_on_ui_thread(append)

    def safe_append_text(self, message, tag="info"):
        self.append_log_line(message, self.infer_log_tag(message, tag))

    def log_raw_output(self, message):
        """格式化后台 print 输出，避免多段内容挤在一行。"""
        cleaned = " ".join(message.split())
        if not cleaned:
            return
        self.safe_append_text(f"{cleaned}\n")
        
    def log_message(self, message, tag="info"):
        """添加日志消息"""
        timestamp = time.strftime("%H:%M:%S")
        self.append_log_line(f"[{timestamp}] {message}\n", self.infer_log_tag(message, tag))

    def update_status_badge(self, text):
        """更新顶部状态徽标。"""
        if any(token in text for token in ['停止', '错误']):
            bg, fg = '#fde8e6', self.colors['danger']
        elif '暂停' in text:
            bg, fg = '#fff4d6', self.colors['warning']
        elif any(token in text for token in ['完成', '成功']):
            bg, fg = '#ddf7ea', self.colors['success']
        elif any(token in text for token in ['分析', '获取', '进度', '下载中']):
            bg, fg = self.colors['accent_soft'], self.colors['accent']
        else:
            bg, fg = '#e9f0f7', self.colors['muted']

        def apply():
            if self._closing or not self.status_badge.winfo_exists():
                return
            self.status_badge.config(text=text, bg=bg, fg=fg)

        self.run_on_ui_thread(apply)

    def set_status(self, text):
        self.run_on_ui_thread(self.status_var.set, text)
        self.update_status_badge(text)

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
        
    def start_download(self):
        """开始下载"""
        url = self.url_entry.get().strip()
        if not url:
            messagebox.showwarning("警告", "请输入漫画URL")
            return
            
        if not url.startswith("https://baozimh.org/"):
            messagebox.showwarning("警告", "URL必须以 https://baozimh.org/ 开头")
            return
            
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
            state_data = {
                'url': self.url_entry.get().strip(),
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
                self.url_entry.delete(0, tk.END)
                self.url_entry.insert(0, state['url'])
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
            self.status_var.set(final_status)
            self.update_status_badge(final_status)
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
