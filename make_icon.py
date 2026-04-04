#!/usr/bin/env python3
"""
Convert a PNG image into a Windows ICO file for PyInstaller.

Usage:
    python make_icon.py
    python make_icon.py input.png
    python make_icon.py input.png output.ico
"""

from pathlib import Path
import sys


def main():
    try:
        from PIL import Image
    except ImportError:
        print("Pillow is not installed.")
        print("Please run: pip install pillow")
        return 1

    input_path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("icon.png")
    output_path = Path(sys.argv[2]) if len(sys.argv) > 2 else Path("app.ico")

    if not input_path.exists():
        print(f"Input file not found: {input_path}")
        print("Place your PNG in the project root or pass a path explicitly.")
        return 1

    try:
        img = Image.open(input_path).convert("RGBA")
        img.save(
            output_path,
            format="ICO",
            sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
        )
    except Exception as exc:
        print(f"Failed to create icon: {exc}")
        return 1

    print(f"Created icon: {output_path.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
