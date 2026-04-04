#!/usr/bin/env python3
"""
漫画下载器图形界面启动器
"""

import subprocess
import sys
import os

def is_frozen_app():
    """判断当前是否运行在 PyInstaller 打包环境中。"""
    return getattr(sys, "frozen", False)

def check_requirements():
    """检查并安装所需的依赖包"""
    if is_frozen_app():
        return True

    print("正在检查依赖包...")
    
    # 检查tkinter是否可用
    try:
        import tkinter
        print("✅ tkinter 已安装")
    except ImportError:
        print("❌ tkinter 未安装")
        print("请安装tkinter:")
        print("  Windows: tkinter通常随Python一起安装")
        print("  Ubuntu/Debian: sudo apt-get install python3-tk")
        print("  macOS: tkinter通常随Python一起安装")
        return False
    
    # 检查其他依赖
    required_packages = [
        ('requests', 'requests'),
        ('beautifulsoup4', 'bs4'),
        ('tqdm', 'tqdm'),
        ('lxml', 'lxml')
    ]
    
    missing_packages = []
    for package_name, import_name in required_packages:
        try:
            __import__(import_name)
            print(f"✅ {package_name} 已安装")
        except ImportError:
            missing_packages.append(package_name)
            print(f"❌ {package_name} 未安装")
    
    if missing_packages:
        print(f"\n正在安装缺失的包: {', '.join(missing_packages)}")
        try:
            subprocess.check_call([sys.executable, '-m', 'pip', 'install'] + missing_packages)
            print("✅ 依赖包安装完成")
        except subprocess.CalledProcessError:
            print("❌ 依赖包安装失败，请手动安装")
            return False
    
    return True

def main():
    """主函数"""
    print("🚀 启动漫画下载器图形界面...")
    print("=" * 50)
    
    # 检查依赖
    if not is_frozen_app():
        if not check_requirements():
            print("❌ 依赖检查失败，无法启动图形界面")
            input("按回车键退出...")
            return
        
        print("\n✅ 所有依赖检查通过")
        print("正在启动图形界面...")
    
    try:
        # 导入并运行图形界面
        from comic_gui import main as gui_main
        gui_main()
    except Exception as e:
        print(f"❌ 启动图形界面失败: {str(e)}")
        print("请确保所有文件都在同一目录下")
        input("按回车键退出...")

if __name__ == "__main__":
    main()
