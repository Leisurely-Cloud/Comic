"""UI 样式配置。"""
from __future__ import annotations


COLORS = {
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
    'border': '#d7e0ea',
}


def setup_ui_styles(root, style):
    style.theme_use('clam')
    colors = dict(COLORS)

    root.configure(bg=colors['bg'])

    style.configure('TFrame', background=colors['bg'])
    style.configure('TLabel', background=colors['bg'], foreground=colors['fg'])
    style.configure('TLabelFrame', background=colors['surface'], foreground=colors['dark'])
    style.configure('Panel.TFrame', background=colors['surface'])
    style.configure('Surface.TFrame', background=colors['surface_alt'])
    style.configure('Content.TPanedwindow', background=colors['surface'])
    style.configure('Inner.TPanedwindow', background=colors['surface'])
    style.configure('Title.TLabel', background=colors['bg'], foreground=colors['fg'],
                     font=('Microsoft YaHei UI', 18, 'bold'))
    style.configure('Subtitle.TLabel', background=colors['bg'], foreground=colors['muted'],
                     font=('Microsoft YaHei UI', 10))
    style.configure('Hint.TLabel', background=colors['surface'], foreground=colors['muted'],
                     font=('Microsoft YaHei UI', 9))
    style.configure('Section.TLabelframe', background=colors['surface'],
                     borderwidth=1, relief='solid')
    style.configure('Section.TLabelframe.Label', background=colors['surface'],
                     foreground=colors['fg'], font=('Microsoft YaHei UI', 10, 'bold'))
    style.configure('Info.TLabel', background=colors['surface'], foreground=colors['muted'],
                     font=('Microsoft YaHei UI', 9))
    style.configure('Footer.TLabel', background=colors['surface'], foreground=colors['fg'],
                     font=('Microsoft YaHei UI', 9))
    button_font = ('Microsoft YaHei UI', 10, 'bold')

    style.configure(
        'TButton',
        background=colors['bg'],
        foreground=colors['fg'],
        font=button_font,
        padding=(14, 8)
    )
    style.map(
        'TButton',
        background=[('active', '#e5edf7'), ('disabled', '#eef3f8')],
        foreground=[('active', colors['fg']), ('disabled', '#7f8c8d')]
    )
    style.configure('Accent.TButton',
           background=colors['accent'],
           foreground=colors['light'],
           font=button_font,
           padding=(14, 8),
           borderwidth=1,
           focusthickness=1,
           focuscolor=colors['accent'])
    style.map('Accent.TButton',
         background=[('active', '#1767bf'), ('disabled', '#bdc3c7')],
         foreground=[('active', colors['light']), ('disabled', '#6c757d')])
    style.configure('TEntry', fieldbackground='white', foreground=colors['fg'],
                     bordercolor=colors['border'], lightcolor=colors['accent_soft'])
    style.configure('TCheckbutton', background=colors['bg'], foreground=colors['fg'])
    style.configure('TSpinbox', fieldbackground='white', foreground=colors['fg'])
    style.configure(
        'Ranking.Treeview',
        background=colors['surface'],
        fieldbackground=colors['surface'],
        foreground=colors['fg'],
        bordercolor=colors['border'],
        lightcolor=colors['surface'],
        darkcolor=colors['surface'],
        rowheight=28,
    )
    style.map(
        'Ranking.Treeview',
        background=[('selected', colors['accent_soft'])],
        foreground=[('selected', colors['fg'])]
    )
    style.configure(
        'Ranking.Treeview.Heading',
        background='#eef4fb',
        foreground=colors['fg'],
        relief='flat',
        padding=(8, 6),
        font=('Microsoft YaHei UI', 9, 'bold')
    )
    style.map(
        'Ranking.Treeview.Heading',
        background=[('active', '#e3edf8')]
    )
    style.configure('Download.Horizontal.TProgressbar',
           background=colors['accent'],
           troughcolor='#ecf0f1',
           borderwidth=0,
           lightcolor=colors['accent'],
           darkcolor=colors['accent'])
    style.configure('Success.Horizontal.TProgressbar',
           background=colors['success'],
           troughcolor='#ecf0f1',
           borderwidth=0,
           lightcolor=colors['success'],
           darkcolor=colors['success'])
    style.configure('Warning.Horizontal.TProgressbar',
           background=colors['warning'],
           troughcolor='#ecf0f1',
           borderwidth=0,
           lightcolor=colors['warning'],
           darkcolor=colors['warning'])
    style.configure('Danger.Horizontal.TProgressbar',
           background=colors['danger'],
           troughcolor='#ecf0f1',
           borderwidth=0,
           lightcolor=colors['danger'],
           darkcolor=colors['danger'])

    style.configure('Success.TButton',
           background=colors['success'],
           foreground=colors['light'],
           font=button_font,
           padding=(14, 8))
    style.map('Success.TButton',
         background=[('active', '#18854a'), ('disabled', '#bdc3c7')],
         foreground=[('active', colors['light']), ('disabled', '#6c757d')])

    style.configure('Warning.TButton',
           background=colors['warning'],
           foreground=colors['light'],
           font=button_font,
           padding=(14, 8))
    style.map('Warning.TButton',
         background=[('active', '#b9770e'), ('disabled', '#bdc3c7')],
         foreground=[('active', colors['light']), ('disabled', '#6c757d')])

    style.configure('Danger.TButton',
           background=colors['danger'],
           foreground=colors['light'],
           font=button_font,
           padding=(14, 8))
    style.map('Danger.TButton',
         background=[('active', '#a93226'), ('disabled', '#bdc3c7')],
         foreground=[('active', colors['light']), ('disabled', '#6c757d')])

    return colors
