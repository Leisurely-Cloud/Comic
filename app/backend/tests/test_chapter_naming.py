from __future__ import annotations

import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from backend.support.chapter_naming import (
    is_final_chapter_dir_name,
    is_temp_chapter_dir_name,
    looks_like_manga_download_dir,
)


class TestIsFinalChapterDirName(unittest.TestCase):
    def test_valid_names(self):
        self.assertTrue(is_final_chapter_dir_name("001_第1话"))
        self.assertTrue(is_final_chapter_dir_name("001_Chapter 1"))
        self.assertTrue(is_final_chapter_dir_name("123_漫画"))
        self.assertTrue(is_final_chapter_dir_name("1_测试"))

    def test_invalid_names(self):
        self.assertFalse(is_final_chapter_dir_name(""))
        self.assertFalse(is_final_chapter_dir_name("abc_第1话"))
        self.assertFalse(is_final_chapter_dir_name("第1话"))
        self.assertFalse(is_final_chapter_dir_name(".下载中_001_第1话"))
        self.assertFalse(is_final_chapter_dir_name("001"))


class TestIsTempChapterDirName(unittest.TestCase):
    def test_valid_names(self):
        self.assertTrue(is_temp_chapter_dir_name(".下载中_001_第1话"))
        self.assertTrue(is_temp_chapter_dir_name(".下载中_123_Chapter"))

    def test_invalid_names(self):
        self.assertFalse(is_temp_chapter_dir_name(""))
        self.assertFalse(is_temp_chapter_dir_name("001_第1话"))
        self.assertFalse(is_temp_chapter_dir_name("下载中_001_第1话"))
        self.assertFalse(is_temp_chapter_dir_name(".下载中_"))


class TestLooksLikeMangaDownloadDir(unittest.TestCase):
    def test_with_final_chapters(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            os.makedirs(os.path.join(tmpdir, "001_第1话"))
            os.makedirs(os.path.join(tmpdir, "002_第2话"))
            self.assertTrue(looks_like_manga_download_dir(tmpdir))

    def test_with_temp_chapters(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            os.makedirs(os.path.join(tmpdir, ".下载中_001_第1话"))
            self.assertTrue(looks_like_manga_download_dir(tmpdir))

    def test_with_mixed_chapters(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            os.makedirs(os.path.join(tmpdir, "001_第1话"))
            os.makedirs(os.path.join(tmpdir, ".下载中_002_第2话"))
            self.assertTrue(looks_like_manga_download_dir(tmpdir))

    def test_empty_directory(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            self.assertFalse(looks_like_manga_download_dir(tmpdir))

    def test_non_chapter_files(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            os.makedirs(os.path.join(tmpdir, "images"))
            os.makedirs(os.path.join(tmpdir, "metadata"))
            self.assertFalse(looks_like_manga_download_dir(tmpdir))

    def test_nonexistent_directory(self):
        self.assertFalse(looks_like_manga_download_dir("/nonexistent/path"))

    def test_with_files(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            with open(os.path.join(tmpdir, "001_第1话"), "w") as f:
                f.write("test")
            self.assertFalse(looks_like_manga_download_dir(tmpdir))


if __name__ == "__main__":
    unittest.main()
