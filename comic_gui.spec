# -*- mode: python ; coding: utf-8 -*-

from pathlib import Path

project_dir = Path.cwd()
icon_file = project_dir / 'app.ico'
version_file = project_dir / 'version_info.txt'


def resolve_icon(path: Path):
    """只在图标文件看起来像合法 ICO 时才交给 PyInstaller。"""
    if not path.exists():
        return None

    try:
        with path.open('rb') as f:
            header = f.read(4)
    except OSError:
        return None

    # Windows ICO header: 00 00 01 00
    if header != b'\x00\x00\x01\x00':
        print(f'[spec] Skip invalid icon file: {path}')
        return None

    return str(path)

block_cipher = None

a = Analysis(
    ['run_gui.py'],
    pathex=[str(project_dir)],
    binaries=[],
    datas=[],
    hiddenimports=[
        'bs4',
        'lxml',
        'tkinter',
        'tkinter.ttk',
        'tkinter.scrolledtext',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name='comic-downloader',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=resolve_icon(icon_file),
    version=str(version_file) if version_file.exists() else None,
)
